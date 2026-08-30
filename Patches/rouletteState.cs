using System;
using System.Collections.Generic;
using System.Linq;
using FishNet.Object;
using Straftapelago.Finnegan_McD.org.Archipelago;
using Straftapelago.Finnegan_McD.org.Utils;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

// The LOCAL player's roulette pools. One instance, created in Plugin.Awake() and reached
// through Plugin.RouletteState from every patch that needs it.
//
// This is one player's unlocks and nothing else. The roll happens on the peer that owns the
// player who grabbed the Roulette Item (see GrabPatches), and only the single chosen prefab
// ever crosses the wire — no peer is ever told what another peer's pool contains. That is
// why one instance per process is still enough here: a process only ever tracks the unlocks
// of the player sitting in front of it.
//
// It is a plain C# object rather than a MonoBehaviour on purpose. A UnityEngine.Object would
// need DontDestroyOnLoad to survive a scene change and would be null for the first frames of
// the process; a plain object referenced by a static field is simply never collected and is
// live the moment Awake() constructs it. It has no per-frame work, so it needs nothing Unity
// gives a component.
//
// Every weapon lives in exactly one of the three lists:
//   unowned_items  — locked. Cannot be rolled, and cannot be picked up off the floor either
//                    (see UnobtainableInteractionPatch).
//   obtained_Items — unlocked, but the player has not scored a kill with it yet.
//   hasKill_Items  — unlocked AND already used for a kill. Moved here by RecordKill, which is
//                    also what sends the Archipelago location check.
public class RouletteState
{
    public List<GameObject> obtained_Items = new();
    public List<GameObject> unowned_items = new();

    // Weapons the player has already got a kill with. A weapon only ever arrives here from
    // obtained_Items, and only through RecordKill, so "is in this list" and "the first-kill
    // check for it has been sent" mean the same thing.
    public List<GameObject> hasKill_Items = new();

    // The starter unlocks from Design_Doc.txt: a pistol plus the stun weapons. Matched
    // case-insensitively against SpawnerManager.NameToWeaponDict, because the Resources
    // paths are lowercased ("randomweapons/glock") while that dictionary is keyed on each
    // prefab's own GameObject.name, whose casing this mod does not control.
    private static readonly string[] StarterWeapons = { "glock", "taser", "stungrenade", "stunmine" };

    private bool initialized;
    private Dictionary<string, GameObject> nameLookup;
    private Dictionary<string, GameObject> displayNameLookup;

    /// <summary>
    /// Builds the pool once and then never again. This is all PlayerPickup.Awake() is
    /// allowed to call: Awake() fires for every player object every round, so calling
    /// Reset() from there wiped the pool mid-match, repeatedly, which is why a roll never
    /// had more than the one starter weapon to choose between.
    /// </summary>
    /// <remarks>
    /// The population is deliberately NOT done in the constructor. Plugin.Awake() runs from
    /// BepInEx's entrypoint, before the first scene loads, and Reset() reads
    /// SpawnerManager.AllWeapons / NameToWeaponDict, which are not up that early. The object
    /// exists from startup; the weapon list arrives the first time a player object does.
    /// </remarks>
    public void EnsureInitialized()
    {
        if (initialized) return;
        Reset();
    }

    /// <summary>
    /// Full rebuild, every call — unlike EnsureInitialized this always does the work.
    /// Only the O debug key and the first EnsureInitialized() reach it.
    /// </summary>
    public void Reset()
    {
        SpawnerManager.PopulateAllWeapons();
        GameObject[] allWeapons = SpawnerManager.AllWeapons;

        obtained_Items.Clear();
        unowned_items.Clear();
        hasKill_Items.Clear();
        nameLookup = null;
        displayNameLookup = null;

        if (allWeapons != null)
        {
            foreach (GameObject weapon in allWeapons)
            {
                // A null here would later read as a "roll" that silently produces nothing,
                // which looks exactly like a skewed distribution. Keep them out entirely.
                if (weapon != null) unowned_items.Add(weapon);
            }
        }

        initialized = true;
        SeedStarters();

        DiagLog.Log("RouletteState.Reset",
            $"{DiagLog.NetRoles()} AllWeapons={(allWeapons == null ? "NULL" : allWeapons.Length.ToString())} " +
            $"unowned={unowned_items.Count} obtained={obtained_Items.Count} hasKill={hasKill_Items.Count}");
        LogPool();
    }

    /// <summary>
    /// Seeds the starting unlocks by NAME. This replaces a hardcoded `unowned_items[30]`,
    /// which depended on Resources.LoadAll ordering that Unity does not guarantee and threw
    /// outright on a short list — and a throw here is not survivable, because Reset() is
    /// reached from a Harmony prefix on PlayerPickup.Awake(), which FishNet generates as
    ///     NetworkInitialize___Early(); Awake___UserLogic(); NetworkInitialize__Late();
    /// so throwing skips NetworkInitialize___Early() and that PlayerPickup never registers
    /// its SyncVars (crash-investigation candidate A2). Nothing below indexes unguarded.
    /// </summary>
    private void SeedStarters()
    {
        foreach (string starter in StarterWeapons)
        {
            if (!GrantByName(starter))
            {
                Plugin.BepinLogger.LogWarning(
                    $"[RouletteState] starter weapon '{starter}' did not resolve through " +
                    "SpawnerManager.NameToWeaponDict; skipping it.");
            }
        }

        if (obtained_Items.Count == 0 && unowned_items.Count > 0)
        {
            Plugin.BepinLogger.LogWarning(
                "[RouletteState] no starter weapon resolved by name; falling back to the first " +
                "entry in the weapon list so the pool is never empty.");
            Grant(unowned_items[0]);
        }
    }

    /// <summary>The single mutation point for the pool. Also the seam for a future Archipelago hook.</summary>
    public bool Grant(GameObject weapon)
    {
        if (weapon == null) return false;

        // A duplicate would give that weapon two entries and therefore double its odds,
        // which is exactly the "equal chance" guarantee this needs to hold. A weapon that
        // already earned its kill is not re-granted either - that would undo its progress.
        if (obtained_Items.Contains(weapon) || hasKill_Items.Contains(weapon)) return false;

        unowned_items.Remove(weapon);
        obtained_Items.Add(weapon);
        LogPool();
        return true;
    }

    /// <summary>Name-keyed grant — what an Archipelago item receipt will eventually call.</summary>
    public bool GrantByName(string weaponName)
    {
        GameObject weapon = Lookup(weaponName);
        return weapon != null && Grant(weapon);
    }

    /// <summary>
    /// Unlocks everything still locked, in one pass. Behind the I debug key.
    /// </summary>
    /// <remarks>
    /// Not a loop over Grant(): that calls LogPool() per weapon, so one keypress would dump
    /// the whole pool ~70 times. One move, one log line.
    /// </remarks>
    public int GrantAllUnowned()
    {
        int moved = 0;
        foreach (GameObject weapon in unowned_items)
        {
            if (weapon == null) continue;
            if (obtained_Items.Contains(weapon) || hasKill_Items.Contains(weapon)) continue;

            obtained_Items.Add(weapon);
            moved++;
        }

        unowned_items.Clear();
        LogPool();
        return moved;
    }

    /// <summary>Case-insensitive lookup over the game's own name-to-prefab dictionary.</summary>
    public GameObject Lookup(string weaponName)
    {
        if (string.IsNullOrEmpty(weaponName)) return null;

        if (nameLookup == null)
        {
            Dictionary<string, GameObject> source = SpawnerManager.NameToWeaponDict;
            if (source == null) return null;

            nameLookup = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, GameObject> pair in source)
            {
                if (pair.Key != null && pair.Value != null) nameLookup[pair.Key] = pair.Value;
            }
        }

        return nameLookup.TryGetValue(weaponName, out GameObject weapon) ? weapon : null;
    }

    /// <summary>
    /// Lookup by the name the game DISPLAYS (ItemBehaviour.weaponName) rather than by the
    /// prefab's GameObject.name.
    /// </summary>
    /// <remarks>
    /// The two are different namespaces and both are needed: the pools are keyed on prefab
    /// names ("glock", what SpawnerManager.NameToWeaponDict uses), while kill detection and
    /// the floor item's popup both speak display names ("Glock"). Built off
    /// SpawnerManager.AllWeapons, so it covers exactly the weapons the pools contain.
    /// </remarks>
    public GameObject LookupByDisplayName(string displayName)
    {
        if (string.IsNullOrEmpty(displayName)) return null;

        if (displayNameLookup == null)
        {
            GameObject[] allWeapons = SpawnerManager.AllWeapons;
            if (allWeapons == null) return null;

            displayNameLookup = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            foreach (GameObject weapon in allWeapons)
            {
                if (weapon == null) continue;

                ItemBehaviour behaviour = weapon.GetComponent<ItemBehaviour>();
                if (behaviour == null || string.IsNullOrEmpty(behaviour.weaponName)) continue;

                // First one wins, and the collision is reported rather than hidden: two
                // prefabs sharing a display name would make kill credit ambiguous, and that
                // is worth knowing about rather than silently picking one.
                if (displayNameLookup.TryGetValue(behaviour.weaponName, out GameObject existing))
                {
                    Plugin.BepinLogger.LogWarning(
                        $"[RouletteState] two weapon prefabs share the display name " +
                        $"'{behaviour.weaponName}' ({existing.name} and {weapon.name}); " +
                        $"keeping {existing.name}.");
                    continue;
                }

                displayNameLookup[behaviour.weaponName] = weapon;
            }
        }

        return displayNameLookup.TryGetValue(displayName, out GameObject match) ? match : null;
    }

    /// <summary>
    /// Resolves any name this mod might be handed - a prefab name or a display name - to the
    /// pool entry it belongs to.
    /// </summary>
    public GameObject ResolveByAnyName(string weaponName) =>
        Lookup(weaponName) ?? LookupByDisplayName(weaponName);

    /// <summary>
    /// Maps a live item in the world back to the prefab the pools hold, or null when it is
    /// not a pool weapon at all (the Roulette Item, Aboubi, a Pig Held Item).
    /// </summary>
    /// <remarks>
    /// Callers must treat null as "not restricted" rather than "locked" - failing open is
    /// what keeps a weapon this mod does not recognise pickable instead of bricking it.
    /// </remarks>
    public GameObject ResolvePrefab(ItemBehaviour item)
    {
        if (item == null) return null;

        // Instantiated copies keep the prefab's name with "(Clone)" appended, so the prefab
        // name is the more precise of the two keys and is tried first.
        string objectName = item.gameObject.name;
        const string cloneSuffix = "(Clone)";
        if (objectName != null && objectName.EndsWith(cloneSuffix, StringComparison.Ordinal))
        {
            objectName = objectName.Substring(0, objectName.Length - cloneSuffix.Length).TrimEnd();
        }

        return Lookup(objectName) ?? LookupByDisplayName(item.weaponName);
    }

    /// <summary>True when this item is a weapon the player has not unlocked yet.</summary>
    public bool IsUnobtainable(ItemBehaviour item)
    {
        GameObject prefab = ResolvePrefab(item);
        return prefab != null && unowned_items.Contains(prefab);
    }

    /// <summary>
    /// Credits a kill to the weapon that made it. The first kill with an unlocked weapon
    /// moves it to hasKill_Items and sends the Archipelago location check; every later kill
    /// with it does nothing.
    /// </summary>
    /// <remarks>
    /// Called only from the kill paths in killDetectPatches, never from a self-kill path -
    /// a suicide is not a kill and must not earn a check.
    /// </remarks>
    public void RecordKill(string weaponName)
    {
        GameObject prefab = ResolveByAnyName(weaponName);
        if (prefab == null)
        {
            DiagLog.Log("RouletteState.RecordKill",
                $"'{weaponName}' did not resolve to a pool weapon; no check sent");
            return;
        }

        // Remove() answers "was it in obtained_Items" and does the move in one step. False
        // covers all three no-op cases: already has a kill, never unlocked, or not a weapon
        // this pool tracks.
        if (!obtained_Items.Remove(prefab)) return;

        hasKill_Items.Add(prefab);
        LogPool();

        // The prefab's display name rather than the name the kill path happened to resolve:
        // that one can be either namespace, and the room's locations are named after the
        // weapons the way the game displays them.
        LocationSender.Send_Location(DisplayNameOf(prefab));
    }

    /// <summary>
    /// Moves a weapon into hasKill_Items whatever list it is in now, without caring whether
    /// the player has ever held it. RecordKill's cheat twin, behind /ap_completecheck.
    /// </summary>
    /// <remarks>
    /// Unlike RecordKill this does not send the check - the command does that itself, so it
    /// can report to the player what the room said. It also accepts a locked weapon, which
    /// RecordKill will not: a check granted by hand is the player saying the kill happened,
    /// and refusing it because the weapon was never unlocked would make the command useless
    /// for exactly the weapons it is most wanted for.
    /// </remarks>
    /// <returns>The pool weapon that was moved, or null when the name is not one.</returns>
    public GameObject MarkKillEarned(string weaponName)
    {
        GameObject prefab = ResolveByAnyName(weaponName);
        if (prefab == null) return null;

        // Already earned: still a success for the caller, but nothing to move, and adding it
        // again would give it two entries and double its odds in a roll.
        if (hasKill_Items.Contains(prefab)) return prefab;

        unowned_items.Remove(prefab);
        obtained_Items.Remove(prefab);
        hasKill_Items.Add(prefab);
        LogPool();
        return prefab;
    }

    /// <summary>
    /// The name the game displays for a pool weapon (ItemBehaviour.weaponName), falling back
    /// to the prefab name when the prefab carries no ItemBehaviour.
    /// </summary>
    public static string DisplayNameOf(GameObject weapon)
    {
        if (weapon == null) return null;

        ItemBehaviour behaviour = weapon.GetComponent<ItemBehaviour>();
        return string.IsNullOrEmpty(behaviour?.weaponName) ? weapon.name : behaviour.weaponName;
    }

    /// <summary>
    /// Picks which list this roll draws from. Shared with SelfTest so the test can never
    /// drift from the code it is checking.
    /// </summary>
    /// <param name="wantNew">
    /// What the New Weapon Chance roll asked for, BEFORE the empty-list fallback - so a
    /// caller can report the branch that was rolled as well as the list that was used.
    /// </param>
    private List<GameObject> ChoosePool(out bool wantNew)
    {
        // Range(int, int) is max-exclusive, so this is 1..100 inclusive: at a chance of 40,
        // exactly 40 of the 100 outcomes take the new-weapon branch.
        wantNew = UnityEngine.Random.Range(1, 101) <= ArchipelagoMenu.NewWeaponChance.Value;

        List<GameObject> preferred = wantNew ? obtained_Items : hasKill_Items;
        List<GameObject> other = wantNew ? hasKill_Items : obtained_Items;

        // "If there are no weapons in the old list, go straight to the new ones, and vice
        // versa" - the chance only decides anything when both lists have something in them.
        return preferred.Count > 0 ? preferred : other;
    }

    /// <summary>
    /// The roll. Two stages: New Weapon Chance decides whether this is a weapon the player
    /// has never killed with, then the draw inside that list is uniform.
    /// </summary>
    /// <remarks>
    /// Random.Range(int, int) is max-exclusive and uniform, so the only ways the draw could
    /// be biased are a duplicate entry (blocked in Grant) or a destroyed entry that produces
    /// a no-op roll — hence the compaction pass first, which reports what it removed rather
    /// than hiding it.
    /// </remarks>
    public GameObject Roll(int rollId)
    {
        EnsureInitialized();

        int compactedNulls = obtained_Items.RemoveAll(item => item == null)
            + hasKill_Items.RemoveAll(item => item == null);

        List<GameObject> pool = ChoosePool(out bool wantNew);
        bool drewNew = pool == obtained_Items;

        int poolCount = pool.Count;
        if (poolCount == 0)
        {
            DiagLog.RR(rollId, "roll",
                $"poolCount=0 wantNew={wantNew} compactedNulls={compactedNulls} — nothing to roll");
            return null;
        }

        int index = UnityEngine.Random.Range(0, poolCount);
        GameObject prefab = pool[index];
        NetworkObject nob = prefab.GetComponent<NetworkObject>();

        // prefabId/collectionId are logged here AND on the server's spawn so the two can be
        // diffed across the two machines' logs. That comparison is the only decisive test
        // for whether the peers' SpawnablePrefabs tables agree, and it cannot be
        // reconstructed after the fact.
        DiagLog.RR(rollId, "roll",
            $"newChance={ArchipelagoMenu.NewWeaponChance.Value} wantNew={wantNew} " +
            $"drewFrom={(drewNew ? "obtained_Items" : "hasKill_Items")} " +
            $"obtained={obtained_Items.Count} hasKill={hasKill_Items.Count} " +
            $"poolCount={poolCount} index={index} prefab={prefab.name} " +
            $"prefabId={(nob == null ? "NO-NETWORKOBJECT" : nob.PrefabId.ToString())} " +
            $"collectionId={(nob == null ? "n/a" : nob.SpawnableCollectionId.ToString())} " +
            $"compactedNulls={compactedNulls}");

        return prefab;
    }

    /// <summary>
    /// Debug self-test behind the K key: draws many times through the real selection path
    /// and reports the spread against what the odds should be, so "New Weapon Chance is
    /// honoured and the draw inside each list is fair" is a number in the log rather than a
    /// claim about the code.
    /// </summary>
    public void SelfTest(int iterations)
    {
        int newCount = obtained_Items.Count;
        int killCount = hasKill_Items.Count;
        if (newCount + killCount == 0)
        {
            Plugin.BepinLogger.LogInfo("[RouletteState] self-test skipped: both pools are empty");
            return;
        }

        var hits = new Dictionary<GameObject, int>();
        int newBranchDraws = 0;

        for (int draw = 0; draw < iterations; draw++)
        {
            List<GameObject> pool = ChoosePool(out bool wantNew);
            if (wantNew) newBranchDraws++;
            if (pool.Count == 0) continue;

            GameObject picked = pool[UnityEngine.Random.Range(0, pool.Count)];
            hits.TryGetValue(picked, out int count);
            hits[picked] = count + 1;
        }

        // What each weapon's share SHOULD be. When one list is empty the fallback sends
        // every draw to the other one, so that list carries the whole probability mass
        // regardless of the configured chance.
        double chance = ArchipelagoMenu.NewWeaponChance.Value / 100d;
        double newShare = killCount == 0 ? 1d : newCount == 0 ? 0d : chance;
        double killShare = 1d - newShare;

        double worstDeviation = 0d;
        var report = new System.Text.StringBuilder();

        // Fixed column widths, so the numbers line up under each other and a skewed weapon
        // is visible by scanning down the column rather than by reading every line. The name
        // column is sized to the longest name across BOTH lists, so the two blocks share one
        // set of columns; the rest are sized to the widest value they can hold.
        const string listColumn = "has kill";
        int nameWidth = Math.Max("weapon".Length, LongestName(obtained_Items, LongestName(hasKill_Items, 0)));
        int hitsWidth = Math.Max("hits".Length, iterations.ToString().Length);

        report.AppendLine(
            $"  {"list".PadRight(listColumn.Length)}  {"weapon".PadRight(nameWidth)}  " +
            $"{"hits".PadLeft(hitsWidth)}  {"actual".PadLeft(8)}  {"expected".PadLeft(8)}  {"off by".PadLeft(8)}");

        void ReportList(List<GameObject> pool, string label, double listShare)
        {
            for (int index = 0; index < pool.Count; index++)
            {
                GameObject weapon = pool[index];
                double expected = listShare / pool.Count * iterations;
                hits.TryGetValue(weapon, out int observed);

                double deviation = expected > 0d ? Math.Abs(observed - expected) / expected * 100d : 0d;
                if (deviation > worstDeviation) worstDeviation = deviation;

                report.AppendLine(
                    $"  {label.PadRight(listColumn.Length)}  " +
                    $"{(weapon == null ? "null" : weapon.name).PadRight(nameWidth)}  " +
                    $"{observed.ToString().PadLeft(hitsWidth)}  " +
                    $"{$"{observed / (double)iterations * 100d:F2}%".PadLeft(8)}  " +
                    $"{$"{listShare / pool.Count * 100d:F2}%".PadLeft(8)}  " +
                    $"{$"{deviation:F2}%".PadLeft(8)}");
            }
        }

        ReportList(obtained_Items, "no kill", newShare);
        ReportList(hasKill_Items, listColumn, killShare);

        Plugin.BepinLogger.LogInfo(
            $"[RouletteState] distribution self-test: {iterations} draws, " +
            $"New Weapon Chance={ArchipelagoMenu.NewWeaponChance.Value}% " +
            $"over {newCount} no-kill and {killCount} has-kill weapons.{Environment.NewLine}" +
            $"  branch split: {newBranchDraws / (double)iterations * 100d:F2}% rolled the new " +
            $"branch, expected {ArchipelagoMenu.NewWeaponChance.Value:F2}%" +
            $"{(newCount == 0 || killCount == 0 ? " (one list is empty, so every draw falls back to the other)" : "")}" +
            $"{Environment.NewLine}  worst per-weapon deviation {worstDeviation:F2}%" +
            $"{Environment.NewLine}{report}");
    }

    /// <summary>Longest weapon name in a list, for sizing the self-test's name column.</summary>
    private static int LongestName(List<GameObject> pool, int longestSoFar)
    {
        foreach (GameObject weapon in pool)
        {
            int length = weapon == null ? "null".Length : weapon.name.Length;
            if (length > longestSoFar) longestSoFar = length;
        }

        return longestSoFar;
    }

    /// <summary>Numbered dump of the local player's unlocks. Called on every change and every roll.</summary>
    public void LogPool()
    {
        string obtainedList = string.Join(Environment.NewLine,
            obtained_Items.Select((item, index) => $"  [{index}] {(item == null ? "null" : item.name)}"));
        string killList = string.Join(Environment.NewLine,
            hasKill_Items.Select((item, index) => $"  [{index}] {(item == null ? "null" : item.name)}"));

        Plugin.BepinLogger.LogInfo(
            $"obtained_Items ({obtained_Items.Count} total, {hasKill_Items.Count} already earned a kill, " +
            $"{unowned_items.Count} still locked):{Environment.NewLine}{obtainedList}" +
            $"{(hasKill_Items.Count == 0 ? "" : $"{Environment.NewLine}hasKill_Items:{Environment.NewLine}{killList}")}");
    }
}
