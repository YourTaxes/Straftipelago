using System;
using System.Collections.Generic;
using System.Linq;
using Straftapelago.Finnegan_McD.org.Archipelago;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// The local player's roulette pools. One instance, created in Plugin.Awake and reached
/// through Plugin.RouletteState. It tracks one player's unlocks and nothing else: the roll
/// happens on the peer that owns the grabbing player, and only the chosen prefab crosses the
/// wire, so no peer is ever told what another peer's pool contains.
/// Every weapon lives in exactly one of the three lists. unowned_items is locked - it cannot
/// be rolled and cannot be picked up off the floor. obtained_Items is unlocked but has no kill
/// yet. hasKill_Items is unlocked and already used for a kill, which is also what sends the
/// Archipelago location check.
/// </summary>
public partial class RouletteState
{
    public List<GameObject> obtained_Items = new();
    public List<GameObject> unowned_items = new();

    // A weapon only ever arrives here from obtained_Items through RecordKill, so "is in this
    // list" and "the first-kill check for it has been sent" mean the same thing.
    public List<GameObject> hasKill_Items = new();

    // What the pool falls back to offline. Connected, nothing is seeded from here: the room's
    // starting_weapons option arrives as precollected items through ReceiveWeapon.
    //
    // The Glock goes to obtained_Items, where New Weapon Chance can draw it as a weapon with no
    // kill yet. The stun weapons and the propeller cannot earn a check at all, so they go
    // straight to hasKill_Items rather than inflating the new-weapon branch forever.
    //
    // Matched case-insensitively against SpawnerManager.NameToWeaponDict, whose keys are each
    // prefab's own GameObject.name.
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
    /// The weapons that carry no Archipelago check, and the toggle that gates each one. These
    /// are never locked - no unlock for them can ever arrive - so their toggle only decides
    /// whether they start in the roulette. Each has several spellings because the apworld names
    /// them in prose while the pools are keyed on the game's prefab names.
    /// </summary>
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
    // Items start arriving the moment the login succeeds, which is from the Mod Menu with no
    // scene loaded and therefore no prefabs to move, and Reset clears all three lists - so this
    // ledger is what survives both, and replaying it is what makes Reset reproduce the room's
    // state instead of wiping it.
    private readonly List<string> receivedWeaponNames = new();

    // The weapons that carry no Archipelago check, resolved fresh on every Reset. Kept because
    // the progress counters cannot be read off the three lists alone: these are seeded straight
    // into hasKill_Items, so counting that list raw would credit kills that never happened.
    private HashSet<GameObject> weaponsWithoutChecks = new();

    private bool initialized;

    /// <summary>
    /// Builds the pool once and then never again. This is all PlayerPickup.Awake may call, as
    /// Awake fires for every player object every round.
    /// </summary>
    public void EnsureInitialized()
    {
        if (initialized) return;
        Reset();
    }

    /// <summary>
    /// Full rebuild, every call: resolve the weapon roster, lock everything that carries a
    /// check, seed the no-check weapons, replay the room's items and its recorded kills, and
    /// judge the weapon goal. Reached by the O debug key, on connect, and by the first
    /// <see cref="EnsureInitialized"/>.
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

        // Resolved before anything is sorted into a list, because it decides which weapons are
        // eligible to be locked at all.
        Dictionary<GameObject, WeaponGate> alwaysUnlocked = ResolveAlwaysUnlockedWeapons();
        weaponsWithoutChecks = new HashSet<GameObject>(alwaysUnlocked.Keys);

        if (allWeapons != null)
        {
            foreach (GameObject weapon in allWeapons)
            {
                // A null here would later read as a roll that silently produces nothing.
                if (weapon == null) continue;

                // Unconditionally, whatever the toggle says: these carry no check, so locking
                // one would strand it for the whole seed, unrollable and unpickable both.
                if (alwaysUnlocked.ContainsKey(weapon)) continue;

                unowned_items.Add(weapon);
            }
        }

        // Only when there was actually something to build from. Reset is reached on connect
        // too, which happens from the Mod Menu with no scene loaded and therefore no weapons.
        initialized = allWeapons != null && allWeapons.Length > 0;

        SeedAlwaysUnlocked(alwaysUnlocked);
        ReplayReceivedItems();

        if (!ArchipelagoClient.Authenticated) SeedOfflineFallback();

        // Last, so it can promote anything the passes above put in obtained_Items: a kill the
        // room already has a check for outranks "unlocked but never used".
        ReplayEarnedKills();

        suppressPoolLog = false;

        DiagLog.Log("RouletteState.Reset",
            $"{DiagLog.NetRoles()} AllWeapons={(allWeapons == null ? "NULL" : allWeapons.Length.ToString())} " +
            $"authenticated={ArchipelagoClient.Authenticated} received={receivedWeaponNames.Count} " +
            $"checkedLocations={ArchipelagoClient.GetCheckedLocationNames().Count()} " +
            $"alwaysUnlocked={alwaysUnlocked.Count} " +
            $"unowned={unowned_items.Count} obtained={obtained_Items.Count} hasKill={hasKill_Items.Count}");
        LogPool();

        // The first rebuild is the first moment the weapon goal can be judged at all. Guarded
        // because Reset is reached from a Harmony prefix on PlayerPickup.Awake, and throwing
        // out of that skips the FishNet initialization that follows it.
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
    /// Resolves <see cref="AlwaysUnlockedWeapons"/> against the weapons this game build
    /// actually has, reporting every entry that matched nothing by name.
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
    /// Puts the no-check weapons whose toggle the room turned on straight into hasKill_Items,
    /// since there is no check for them to earn. A toggle that is off leaves its weapon in no
    /// list at all, which keeps it off the roulette while leaving it pickable.
    /// </summary>
    private void SeedAlwaysUnlocked(Dictionary<GameObject, WeaponGate> alwaysUnlocked)
    {
        foreach (KeyValuePair<GameObject, WeaponGate> entry in alwaysUnlocked)
        {
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
    /// Re-grants every unlock the room has already sent, since <see cref="Reset"/> clears the
    /// pools.
    /// </summary>
    private void ReplayReceivedItems()
    {
        foreach (string weaponName in receivedWeaponNames)
        {
            if (!GrantByName(weaponName))
            {
                // Not necessarily a failure: an already-granted name answers false too.
                Plugin.BepinLogger.LogDebug(
                    $"[RouletteState] replaying received item '{weaponName}' granted nothing.");
            }
        }
    }

    /// <summary>
    /// Seeds a starting pool for a session with no room behind it, so the roulette still works
    /// offline. Nothing in here may throw, because <see cref="Reset"/> is reached from a
    /// Harmony prefix on PlayerPickup.Awake.
    /// </summary>
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
    /// <see cref="Reset"/> otherwise. Must be called on the main thread.
    /// </summary>
    /// <returns>True if the weapon moved into the pool during this call.</returns>
    public bool ReceiveWeapon(string weaponName)
    {
        if (string.IsNullOrEmpty(weaponName)) return false;

        receivedWeaponNames.Add(weaponName);

        // Not EnsureInitialized: out of a match SpawnerManager has no weapons, and building the
        // pool off an empty list would set initialized and leave it that way. The name is
        // already banked, so the replay in Reset grants it once the pool is real.
        if (!initialized) return false;

        if (GrantByName(weaponName)) return true;

        // A name that resolves is an ordinary duplicate; a name that resolves to nothing is a
        // disagreement between the mod and the apworld, and the unlock is lost every time.
        if (ResolveByAnyName(weaponName) == null)
        {
            Plugin.BepinLogger.LogWarning(
                $"[RouletteState] the room granted '{weaponName}', which is not a weapon in this " +
                "build; nothing was unlocked. The apworld's item name and the game's prefab name " +
                "disagree.");
        }

        return false;
    }

    /// <summary>The single mutation point for the pool.</summary>
    public bool Grant(GameObject weapon)
    {
        if (weapon == null) return false;

        // A duplicate would give that weapon two entries and therefore double its odds. A
        // weapon that already earned its kill is not re-granted either, as that would undo its
        // progress.
        if (obtained_Items.Contains(weapon) || hasKill_Items.Contains(weapon)) return false;

        unowned_items.Remove(weapon);
        obtained_Items.Add(weapon);
        LogPool();
        return true;
    }

    /// <summary>
    /// Name-keyed grant, which is what an Archipelago item receipt calls. Goes through
    /// <see cref="ResolveByAnyName"/> because the room's item names are neither of the two
    /// namespaces the pools are keyed on.
    /// </summary>
    public bool GrantByName(string weaponName)
    {
        GameObject weapon = ResolveByAnyName(weaponName);
        return weapon != null && Grant(weapon);
    }

    /// <summary>
    /// Unlocks everything still locked, in one pass and with one pool dump. Behind the I debug
    /// key.
    /// </summary>
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
    /// Behind the L debug key, and local only - no check is sent, so the next
    /// <see cref="Reset"/> puts every weapon this moved back in obtained_Items.
    /// </summary>
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

        // The share earned has just jumped, and the weapon goal is a share of the roster.
        GoalTracker.Evaluate();
        return moved;
    }

    /// <summary>True when this item is a weapon the player has not unlocked yet.</summary>
    public bool IsUnobtainable(ItemBehaviour item)
    {
        GameObject prefab = ResolvePrefab(item);
        return prefab != null && unowned_items.Contains(prefab);
    }

    /// <summary>
    /// How many of this build's check-carrying weapons the player has earned the first-kill
    /// check for: hasKill_Items minus the weapons that carry no check.
    /// </summary>
    public int EarnedWeaponCount => CountWeaponsWithChecks(hasKill_Items);

    /// <summary>
    /// How many weapons in this build can earn a first-kill check at all, which is the
    /// denominator <see cref="EarnedWeaponCount"/> is a fraction of. Zero until the pool has
    /// been built, which the caller has to check before dividing.
    /// </summary>
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
    /// Credits a kill to the weapon that made it. The first kill with an unlocked weapon moves
    /// it to hasKill_Items and sends the Archipelago location check; every later kill with it
    /// does nothing. Called only from the kill paths, never from a self-kill.
    /// </summary>
    public void RecordKill(string weaponName)
    {
        GameObject prefab = ResolveByAnyName(weaponName);
        if (prefab == null)
        {
            DiagLog.Log("RouletteState.RecordKill",
                $"'{weaponName}' did not resolve to a pool weapon; no check sent");
            return;
        }

        // Remove answers "was it in obtained_Items" and does the move in one step. False covers
        // all three no-op cases: already has a kill, never unlocked, or not a pool weapon.
        if (!obtained_Items.Remove(prefab)) return;

        hasKill_Items.Add(prefab);
        LogPool();

        // Before the check goes out, because the share earned does not depend on the room's
        // answer.
        GoalTracker.Evaluate();

        // The prefab's display name rather than whatever the kill path resolved: the room's
        // locations are named after the weapons the way the game displays them.
        LocationSender.Send_Location(DisplayNameOf(prefab));
    }

    /// <summary>
    /// Moves a weapon into hasKill_Items whatever list it is in now, without caring whether the
    /// player has ever held it, and without sending the check - /ap_completecheck sends that
    /// itself so it can report what the room said.
    /// </summary>
    /// <returns>The pool weapon that was moved, or null when the name is not one.</returns>
    public GameObject MarkKillEarned(string weaponName)
    {
        GameObject prefab = ResolveByAnyName(weaponName);
        if (prefab == null) return null;

        // Already earned is still a success for the caller, which is why the no-op case answers
        // the prefab rather than null.
        MoveToHasKill(prefab);
        LogPool();

        // A check granted by hand counts towards the weapon goal like any other.
        GoalTracker.Evaluate();
        return prefab;
    }

    /// <summary>
    /// Re-earns every first kill the room has a check for, after the grants have run. The room
    /// is the record, so this survives a disconnect, a rejoin, or the game being restarted.
    /// The names are location names, which differ from item names in this apworld.
    /// </summary>
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
    /// Puts a weapon in hasKill_Items whatever list it is in now, without sending a check or
    /// dumping the pool. Used for the checks the room already has and for the offline weapons
    /// that can never earn one.
    /// </summary>
    private void MoveToHasKill(GameObject prefab)
    {
        if (prefab == null || hasKill_Items.Contains(prefab)) return;

        unowned_items.Remove(prefab);
        obtained_Items.Remove(prefab);
        hasKill_Items.Add(prefab);
    }
}
