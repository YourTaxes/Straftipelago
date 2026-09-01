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

    // What the pool falls back to when there is no room to get unlocks from. Connected, NOTHING
    // is seeded from here: the room's starting_weapons option is pushed as precollected items
    // and they arrive through ReceiveWeapon like any other unlock, so hardcoding a starter would
    // hand the player a weapon the multiworld never granted. Offline it is the only thing
    // keeping the roulette able to roll at all.
    //
    // Split by which list they land in. The Glock is the one weapon the player is meant to be
    // working ON, so it goes to obtained_Items where New Weapon Chance can draw it as a weapon
    // with no kill yet. The stun weapons and the propeller cannot earn a check at all, so they
    // go straight to hasKill_Items - the same treatment the room's non_damaging_weapons toggle
    // gives them when connected, and for the same reason: parked in obtained_Items they would
    // inflate the new-weapon branch forever with something that can never leave it.
    //
    // Matched case-insensitively against SpawnerManager.NameToWeaponDict, because the Resources
    // paths are lowercased ("randomweapons/glock") while that dictionary is keyed on each
    // prefab's own GameObject.name, whose casing this mod does not control.
    private static readonly string[] OfflineFallbackNewWeapons = { "glock" };

    private static readonly string[] OfflineFallbackEarnedWeapons =
        { "taser", "stungrenade", "stunmine", "propeller" };

    /// <summary>
    /// Which slot data toggle decides whether an always-unlocked weapon is in the roulette.
    /// </summary>
    private enum WeaponGate
    {
        /// <summary>The apworld's non_damaging_weapons option.</summary>
        NonDamaging,

        /// <summary>The apworld's unused_weapons option.</summary>
        Unused,

        /// <summary>The apworld's useless_weapons option.</summary>
        Useless,
    }

    /// <summary>
    /// The weapons that carry no Archipelago check, and the toggle that gates each one.
    /// </summary>
    /// <remarks>
    /// <para>These are never locked. They are not items in the apworld's table at all, so no
    /// unlock for them can ever arrive, and leaving one in unowned_items would make it
    /// permanently unrollable AND unpickable off the floor. Their toggle only decides whether
    /// they start in the roulette: on puts them straight into hasKill_Items - unlocked and
    /// already credited, since there is no check to earn - and off leaves them out of every
    /// list, which keeps them off the roulette while leaving them pickable.</para>
    /// <para>Several spellings each, because the two sides disagree and neither is authoritative
    /// here: the apworld names them only in option docstrings ("Tazer", "Stun Grenade") while
    /// the pools are keyed on the game's own prefab names ("taser", "stungrenade"). Every
    /// candidate goes through ResolveByAnyName, and an entry that resolves to nothing is
    /// reported by name so the right spelling can be read out of the log.</para>
    /// </remarks>
    private static readonly (WeaponGate Gate, string[] Names)[] AlwaysUnlockedWeapons =
    {
        (WeaponGate.NonDamaging, new[] { "propeller" }),
        (WeaponGate.NonDamaging, new[] { "repulsar" }),
        (WeaponGate.NonDamaging, new[] { "stungrenade", "Stun Grenade" }),
        (WeaponGate.NonDamaging, new[] { "stunmine", "Stun Mine" }),
        (WeaponGate.NonDamaging, new[] { "taser", "tazer" }),
        (WeaponGate.Unused, new[] { "bublee" }),
        (WeaponGate.Useless, new[] { "flashlight" }),
    };

    // Every unlock the room has granted, by the name it granted it under, in arrival order.
    //
    // Required rather than convenient. Items start arriving the moment the login succeeds,
    // which is from the Mod Menu with no scene loaded, so SpawnerManager.AllWeapons does not
    // exist yet and there is no prefab to move between lists. Reset() also clears all three
    // lists on every call. This ledger is what survives both, and replaying it is what makes
    // Reset() reproduce the room's state instead of wiping it.
    private readonly List<string> receivedWeaponNames = new();

    // The weapons that carry no Archipelago check, resolved fresh on every Reset(). Kept
    // because the progress counters below cannot be read off the three lists alone: the
    // no-check weapons are seeded straight INTO hasKill_Items (see SeedAlwaysUnlocked and
    // SeedOfflineFallback), so counting that list raw would credit the player with earning a
    // taser they can never send a check for. Holds every entry AlwaysUnlockedWeapons resolves,
    // whatever its toggle says - a gate that is off leaves its weapon out of all three lists,
    // where excluding it costs nothing.
    private HashSet<GameObject> weaponsWithoutChecks = new();

    private bool initialized;

    // Held up while Reset() rebuilds. Every Grant it makes would otherwise dump the whole pool,
    // and Reset() dumps it once itself at the end anyway.
    private bool suppressPoolLog;

    private Dictionary<string, GameObject> nameLookup;
    private Dictionary<string, GameObject> displayNameLookup;
    private Dictionary<string, GameObject> normalizedLookup;

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

        // Every Grant below would otherwise dump the whole pool, and a room's starting
        // inventory replayed through here is dozens of grants in a row. One dump, at the end.
        suppressPoolLog = true;

        obtained_Items.Clear();
        unowned_items.Clear();
        hasKill_Items.Clear();
        nameLookup = null;
        displayNameLookup = null;
        normalizedLookup = null;

        // Resolved before anything is sorted into a list, because it is what decides which
        // weapons are eligible to be locked at all. The lookups it goes through are built off
        // SpawnerManager, not off the pools, so they are available this early.
        Dictionary<GameObject, WeaponGate> alwaysUnlocked = ResolveAlwaysUnlockedWeapons();
        weaponsWithoutChecks = new HashSet<GameObject>(alwaysUnlocked.Keys);

        if (allWeapons != null)
        {
            foreach (GameObject weapon in allWeapons)
            {
                // A null here would later read as a "roll" that silently produces nothing,
                // which looks exactly like a skewed distribution. Keep them out entirely.
                if (weapon == null) continue;

                // Unconditionally, whatever the toggle says. These carry no check, so no
                // unlock for them will ever arrive - locking one would strand it for the
                // whole seed, unrollable and unpickable both.
                if (alwaysUnlocked.ContainsKey(weapon)) continue;

                unowned_items.Add(weapon);
            }
        }

        // Only when there was actually something to build from. Reset() is now reached on
        // connect too, which happens from the Mod Menu with no scene loaded and therefore no
        // weapons - and claiming to be initialized there would make the EnsureInitialized() in
        // PlayerPickup.Awake() skip the real build, leaving the player in a match with an empty
        // pool for the rest of the session.
        initialized = allWeapons != null && allWeapons.Length > 0;

        SeedAlwaysUnlocked(alwaysUnlocked);
        ReplayReceivedItems();

        if (!ArchipelagoClient.Authenticated) SeedOfflineFallback();

        // Last, so it can promote anything the passes above just put in obtained_Items. A kill
        // the room already has a check for outranks "unlocked but never used".
        ReplayEarnedKills();

        suppressPoolLog = false;

        DiagLog.Log("RouletteState.Reset",
            $"{DiagLog.NetRoles()} AllWeapons={(allWeapons == null ? "NULL" : allWeapons.Length.ToString())} " +
            $"authenticated={ArchipelagoClient.Authenticated} received={receivedWeaponNames.Count} " +
            $"checkedLocations={ArchipelagoClient.GetCheckedLocationNames().Count()} " +
            $"alwaysUnlocked={alwaysUnlocked.Count} " +
            $"unowned={unowned_items.Count} obtained={obtained_Items.Count} hasKill={hasKill_Items.Count}");
        LogPool();

        // The first rebuild is where the weapon roster comes into existence, so it is the first
        // moment the weapon goal can be judged at all - a player who reconnects already over the
        // threshold meets it on the frame they enter a match, without another kill.
        //
        // Guarded because Reset() is reached from a Harmony prefix on PlayerPickup.Awake(), and
        // throwing out of that skips the FishNet initialization that follows it. Nothing else in
        // this method is allowed to throw either; see SeedOfflineFallback.
        try
        {
            GoalTracker.Evaluate();
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[RouletteState] the goal check threw after a rebuild: {error}");
        }
    }

    /// <summary>
    /// Resolves <see cref="AlwaysUnlockedWeapons"/> against the weapons this game build actually
    /// has, reporting every entry that matched nothing.
    /// </summary>
    private Dictionary<GameObject, WeaponGate> ResolveAlwaysUnlockedWeapons()
    {
        var resolved = new Dictionary<GameObject, WeaponGate>();

        foreach ((WeaponGate gate, string[] names) in AlwaysUnlockedWeapons)
        {
            GameObject weapon = null;
            foreach (string name in names)
            {
                weapon = ResolveByAnyName(name);
                if (weapon != null) break;
            }

            if (weapon == null)
            {
                // Named rather than counted, because the spellings in this table are the part
                // of it this mod is least sure of - three of these weapons appear nowhere else
                // in the codebase - and the log is how the right one gets found.
                Plugin.BepinLogger.LogWarning(
                    $"[RouletteState] no weapon resolved for '{string.Join("' / '", names)}' " +
                    $"(gated by {gate}); it will be treated as locked like any other weapon.");
                continue;
            }

            resolved[weapon] = gate;
        }

        return resolved;
    }

    /// <summary>
    /// Puts the no-check weapons whose toggle the room turned on straight into hasKill_Items.
    /// </summary>
    /// <remarks>
    /// Into hasKill_Items rather than obtained_Items on purpose: obtained_Items is the "no kill
    /// yet" list that New Weapon Chance draws from, and there is no check to earn with any of
    /// these, so a weapon sitting there would inflate the new-weapon branch forever with
    /// something that can never leave it.
    /// </remarks>
    private void SeedAlwaysUnlocked(Dictionary<GameObject, WeaponGate> alwaysUnlocked)
    {
        foreach (KeyValuePair<GameObject, WeaponGate> entry in alwaysUnlocked)
        {
            // Off means it is in NO list. That keeps it out of the roulette, which is all the
            // option claims to do - it stays pickable off the floor, because IsUnobtainable
            // only refuses what is in unowned_items.
            if (!IsGateOpen(entry.Value)) continue;

            if (!hasKill_Items.Contains(entry.Key)) hasKill_Items.Add(entry.Key);
        }
    }

    /// <summary>Whether the room's slot data turned this group of no-check weapons on.</summary>
    private static bool IsGateOpen(WeaponGate gate)
    {
        ArchipelagoData serverData = ArchipelagoClient.ServerData;
        if (serverData == null) return false;

        switch (gate)
        {
            case WeaponGate.NonDamaging: return serverData.NonDamagingWeapons;
            case WeaponGate.Unused: return serverData.UnusedWeapons;
            case WeaponGate.Useless: return serverData.UselessWeapons;
            default: return false;
        }
    }

    /// <summary>
    /// Re-grants every unlock the room has already sent. Reset() clears the pools, so without
    /// this a pool rebuild would silently take the player's whole multiworld progress away.
    /// </summary>
    private void ReplayReceivedItems()
    {
        foreach (string weaponName in receivedWeaponNames)
        {
            if (!GrantByName(weaponName))
            {
                // Not necessarily a failure: a name that is already granted also answers false,
                // and so does one whose weapon this build does not have. Logged at debug so a
                // long inventory does not bury the pool dump that follows.
                Plugin.BepinLogger.LogDebug(
                    $"[RouletteState] replaying received item '{weaponName}' granted nothing.");
            }
        }
    }

    /// <summary>
    /// Seeds a starting pool for a session with no room behind it, so the roulette still works
    /// offline. Only reached when not authenticated — connected, every unlock comes from the
    /// multiworld, including the ones the room's starting_weapons option precollected.
    /// </summary>
    /// <remarks>
    /// Nothing in here may throw. Reset() is reached from a Harmony prefix on
    /// PlayerPickup.Awake(), which FishNet generates as
    ///     NetworkInitialize___Early(); Awake___UserLogic(); NetworkInitialize__Late();
    /// so throwing skips NetworkInitialize___Early() and that PlayerPickup never registers its
    /// SyncVars (crash-investigation candidate A2). Nothing below indexes unguarded.
    /// </remarks>
    private void SeedOfflineFallback()
    {
        foreach (string starter in OfflineFallbackNewWeapons)
        {
            if (!GrantByName(starter))
            {
                Plugin.BepinLogger.LogWarning(
                    $"[RouletteState] offline fallback weapon '{starter}' did not resolve to a " +
                    "weapon in this build; skipping it.");
            }
        }

        foreach (string starter in OfflineFallbackEarnedWeapons)
        {
            GameObject prefab = ResolveByAnyName(starter);
            if (prefab == null)
            {
                Plugin.BepinLogger.LogWarning(
                    $"[RouletteState] offline fallback weapon '{starter}' did not resolve to a " +
                    "weapon in this build; skipping it.");
                continue;
            }

            MoveToHasKill(prefab);
        }

        if (obtained_Items.Count == 0 && hasKill_Items.Count == 0 && unowned_items.Count > 0)
        {
            Plugin.BepinLogger.LogWarning(
                "[RouletteState] no offline fallback weapon resolved by name; falling back to the " +
                "first entry in the weapon list so the pool is never empty.");
            Grant(unowned_items[0]);
        }
    }

    /// <summary>
    /// Records an unlock the room granted and applies it, now if the pool is up and on the next
    /// <see cref="Reset"/> otherwise.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Items start arriving the instant the login succeeds, which is from
    /// the Mod Menu with no scene loaded and therefore no SpawnerManager to resolve them
    /// against - so the name is banked first and granted second. Must be called on the main
    /// thread; the receipt itself arrives on the Archipelago client's websocket thread.
    /// </remarks>
    /// <returns>True if the weapon moved into the pool during this call.</returns>
    public bool ReceiveWeapon(string weaponName)
    {
        if (string.IsNullOrEmpty(weaponName)) return false;

        receivedWeaponNames.Add(weaponName);

        // Not EnsureInitialized(): out of a match SpawnerManager has no weapons, and building
        // the pool off an empty list would set initialized and leave it that way. The name is
        // already banked, so the replay in Reset() grants it once the pool is real.
        if (!initialized) return false;

        if (GrantByName(weaponName)) return true;

        // Told apart, because the two failures mean opposite things. A name that resolves is a
        // duplicate, which is ordinary - the room replays the whole inventory on every connect.
        // A name that resolves to nothing is a disagreement between this mod and the apworld
        // about what a weapon is called, and the unlock is silently lost every time it happens.
        if (ResolveByAnyName(weaponName) == null)
        {
            Plugin.BepinLogger.LogWarning(
                $"[RouletteState] the room granted '{weaponName}', which is not a weapon in this " +
                "build; nothing was unlocked. The apworld's item name and the game's prefab name " +
                "disagree.");
        }

        return false;
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

    /// <summary>Name-keyed grant — what an Archipelago item receipt calls.</summary>
    /// <remarks>
    /// ResolveByAnyName rather than Lookup, because the names reaching this are the room's item
    /// names, which are neither of the two namespaces the pools are keyed on.
    /// </remarks>
    public bool GrantByName(string weaponName)
    {
        GameObject weapon = ResolveByAnyName(weaponName);
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

    /// <summary>
    /// Credits a first kill to every unlocked weapon that has not earned one yet, in one pass.
    /// Behind the L debug key.
    /// </summary>
    /// <remarks>
    /// <para>GrantAllUnowned's other half: that one unlocks everything, this one marks everything
    /// unlocked as used. Together they put the pool in its finished state in two keypresses,
    /// which is what makes the weapon-goal percentage and its tick testable without playing out
    /// seventy first kills.</para>
    /// <para>Local only - no location check is sent, exactly like the I key it pairs with. The
    /// room is the record of which kills happened (see <see cref="ReplayEarnedKills"/>), so the
    /// next <see cref="Reset"/> puts every weapon this moved back in obtained_Items. Use
    /// /ap_completecheck for a check the room will actually remember.</para>
    /// <para>Not a loop over MarkKillEarned(): that calls LogPool() per weapon, so one keypress
    /// would dump the whole pool dozens of times. One move, one log line.</para>
    /// </remarks>
    /// <returns>How many weapons moved into hasKill_Items.</returns>
    public int MarkAllObtainedKillEarned()
    {
        int moved = 0;
        foreach (GameObject weapon in obtained_Items)
        {
            if (weapon == null) continue;
            if (hasKill_Items.Contains(weapon)) continue;

            hasKill_Items.Add(weapon);
            moved++;
        }

        obtained_Items.Clear();
        LogPool();

        // The share earned has just jumped, and the weapon goal is a share of the roster - so
        // this is one of the moments it can be met, debug key or not.
        GoalTracker.Evaluate();
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
    /// Resolves any name this mod might be handed - a prefab name, a display name, or an
    /// Archipelago item name - to the pool entry it belongs to.
    /// </summary>
    /// <remarks>
    /// The two exact lookups come first and the punctuation-insensitive one last, so an exact
    /// match is never lost to a fuzzy one.
    /// </remarks>
    public GameObject ResolveByAnyName(string weaponName) =>
        Lookup(weaponName) ?? LookupByDisplayName(weaponName) ?? LookupByNormalizedName(weaponName);

    /// <summary>
    /// Lookup with case, spaces, hyphens and underscores all ignored.
    /// </summary>
    /// <remarks>
    /// This is what makes the room's item names land. Archipelago names a weapon the way a
    /// person writes it — "Dual Launcher", "AAA-12", "Hill H15", "Sawed Off" — while the
    /// prefabs squash the same names into "DualLauncher" and "AAA12", so neither exact lookup
    /// can match them. Built over both namespaces, so an item name that happens to be spelled
    /// like the display name still resolves through the same pass.
    /// </remarks>
    public GameObject LookupByNormalizedName(string weaponName)
    {
        string key = Normalize(weaponName);
        if (string.IsNullOrEmpty(key)) return null;

        if (normalizedLookup == null)
        {
            GameObject[] allWeapons = SpawnerManager.AllWeapons;
            if (allWeapons == null) return null;

            normalizedLookup = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            foreach (GameObject weapon in allWeapons)
            {
                if (weapon == null) continue;

                // Prefab name first so that it wins a collision, matching the precedence
                // ResolveByAnyName uses between the two exact lookups.
                Add(weapon.name, weapon);
                Add(weapon.GetComponent<ItemBehaviour>()?.weaponName, weapon);
            }

            void Add(string name, GameObject weapon)
            {
                string normalized = Normalize(name);
                if (string.IsNullOrEmpty(normalized)) return;

                // Silently first-wins, unlike LookupByDisplayName's reported collision:
                // squashing punctuation is expected to make names collide - a prefab and its
                // own display name almost always normalize to the same string - so a warning
                // here would fire for nearly every weapon in the game.
                if (!normalizedLookup.ContainsKey(normalized)) normalizedLookup[normalized] = weapon;
            }
        }

        return normalizedLookup.TryGetValue(key, out GameObject match) ? match : null;
    }

    /// <summary>Lowercases and strips everything that is not a letter or a digit.</summary>
    private static string Normalize(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        var normalized = new System.Text.StringBuilder(name.Length);
        foreach (char character in name)
        {
            if (char.IsLetterOrDigit(character)) normalized.Append(char.ToLowerInvariant(character));
        }

        return normalized.ToString();
    }

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
    /// How many of this build's check-carrying weapons the player has earned the first-kill
    /// check for.
    /// </summary>
    /// <remarks>
    /// hasKill_Items minus the weapons that carry no check, because those are seeded into that
    /// list without a kill ever happening. What is left is exactly the set of first-kill
    /// locations this slot has sent - the ticked entries in the pause-menu weapon list.
    /// </remarks>
    public int EarnedWeaponCount => CountWeaponsWithChecks(hasKill_Items);

    /// <summary>
    /// How many weapons in this build can earn a first-kill check at all - the denominator
    /// <see cref="EarnedWeaponCount"/> is a fraction of.
    /// </summary>
    /// <remarks>
    /// Every weapon is in exactly one of the three lists, so this is the whole weapon roster
    /// less the ones with no check. Zero until the pool has been built, which is what the
    /// caller has to check before dividing: EnsureInitialized only runs once a player object
    /// exists, so out of a match there is no roster to count.
    /// </remarks>
    public int CheckableWeaponCount =>
        CountWeaponsWithChecks(unowned_items)
        + CountWeaponsWithChecks(obtained_Items)
        + CountWeaponsWithChecks(hasKill_Items);

    /// <summary>How many of these weapons have an Archipelago check behind them.</summary>
    private int CountWeaponsWithChecks(List<GameObject> weapons)
    {
        int count = 0;
        foreach (GameObject weapon in weapons)
        {
            if (weapon == null || weaponsWithoutChecks.Contains(weapon)) continue;
            count++;
        }

        return count;
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

        // Before the check goes out rather than after, because it does not depend on the room's
        // answer: the share earned is counted here, and a weapon that just moved into
        // hasKill_Items has moved whatever the socket does next.
        GoalTracker.Evaluate();

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

        // Already earned is still a success for the caller, which is why the no-op case answers
        // the prefab rather than null. MoveToHasKill is what refuses the duplicate - adding it
        // twice would give it two entries and double its odds in a roll.
        MoveToHasKill(prefab);
        LogPool();

        // A check granted by hand counts towards the weapon goal like any other - it is the
        // same move into hasKill_Items, and the room will have the same location recorded.
        GoalTracker.Evaluate();
        return prefab;
    }

    /// <summary>
    /// Re-earns every first kill the room has a check for, after the grants have run.
    /// </summary>
    /// <remarks>
    /// <para>The room is the record, not a list kept here. Every first kill is a location check
    /// and the server remembers which locations this slot has completed, so asking it is the
    /// only source that survives a disconnect, a rejoin, or the game being closed and
    /// reopened - a local ledger would only have covered the current process.</para>
    /// <para>Deliberately not a loop over RecordKill: that sends the check, and these checks
    /// are exactly the ones already sent. This only puts each weapon back in the list its check
    /// says it belongs in.</para>
    /// <para>Location names, not item names. The two differ in this apworld - the locations are
    /// named after the weapon whose first kill they are - so anything that does not resolve is
    /// reported at debug rather than treated as an error.</para>
    /// </remarks>
    private void ReplayEarnedKills()
    {
        foreach (string locationName in ArchipelagoClient.GetCheckedLocationNames())
        {
            GameObject prefab = ResolveByAnyName(locationName);
            if (prefab == null)
            {
                Plugin.BepinLogger.LogDebug(
                    $"[RouletteState] checked location '{locationName}' is not a weapon in this " +
                    "build; no kill credited for it.");
                continue;
            }

            MoveToHasKill(prefab);
        }
    }

    /// <summary>
    /// Puts a weapon in hasKill_Items whatever list it is in now, without sending a check.
    /// </summary>
    /// <remarks>
    /// The rebuild's version of <see cref="MarkKillEarned"/>: same move, but it neither logs the
    /// pool per weapon (Reset dumps it once at the end) nor claims a kill happened. Used both
    /// for the checks the room already has and for the offline weapons that can never earn one.
    /// </remarks>
    private void MoveToHasKill(GameObject prefab)
    {
        if (prefab == null || hasKill_Items.Contains(prefab)) return;

        unowned_items.Remove(prefab);
        obtained_Items.Remove(prefab);
        hasKill_Items.Add(prefab);
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
        // A rebuild makes many changes in a row and dumps the result itself once it is done.
        if (suppressPoolLog) return;

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
