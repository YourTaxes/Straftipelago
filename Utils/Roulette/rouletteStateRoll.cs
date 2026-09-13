using System;
using System.Collections.Generic;
using System.Linq;
using FishNet.Object;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

// The roll itself, its distribution self-test, and the pool dump they both write.
public partial class RouletteState
{
    // Held up while Reset rebuilds. Every Grant it makes would otherwise dump the whole pool,
    // and Reset dumps it once itself at the end.
    private bool suppressPoolLog;

    /// <summary>
    /// Picks which list this roll draws from. Shared with <see cref="SelfTest"/> so the test
    /// can never drift from the code it is checking.
    /// </summary>
    /// <param name="wantNew">
    /// What the New Weapon Chance roll asked for, before the empty-list fallback, so a caller
    /// can report the branch that was rolled as well as the list that was used.
    /// </param>
    private List<GameObject> ChoosePool(out bool wantNew)
    {
        // Range(int, int) is max-exclusive, so this is 1..100 inclusive: at a chance of 40,
        // exactly 40 of the 100 outcomes take the new-weapon branch.
        wantNew = UnityEngine.Random.Range(1, 101) <= ArchipelagoMenu.NewWeaponChance.Value;

        List<GameObject> preferred = wantNew ? obtained_Items : hasKill_Items;
        List<GameObject> other = wantNew ? hasKill_Items : obtained_Items;

        // With one list empty every draw goes to the other one, so the chance only decides
        // anything when both have something in them.
        return preferred.Count > 0 ? preferred : other;
    }

    // The slots the current draw is being made from. Fields rather than locals because SelfTest
    // rebuilds them thousands of times in a row.
    private readonly List<GameObject> individualSlots = new();
    private readonly List<GameObject> groupedSlots = new();

    /// <summary>
    /// Splits a pool into the slots a draw picks between: one each for the weapons that carry an
    /// Archipelago check, and a single shared one for every weapon that carries none. Those are
    /// seeded into hasKill_Items whatever the player has done, so holding a slot each would let a
    /// handful of stun weapons crowd out the whole pool early in a seed.
    /// </summary>
    /// <returns>How many slots the draw picks between.</returns>
    private int BuildSlots(List<GameObject> pool)
    {
        individualSlots.Clear();
        groupedSlots.Clear();

        foreach (GameObject weapon in pool)
        {
            if (weapon == null) continue;

            if (weaponsWithoutChecks.Contains(weapon)) groupedSlots.Add(weapon);
            else individualSlots.Add(weapon);
        }

        return individualSlots.Count + (groupedSlots.Count > 0 ? 1 : 0);
    }

    /// <summary>
    /// One uniform draw over a pool's slots, then a second uniform draw inside the shared slot
    /// when that is the one that came up. Shared with <see cref="SelfTest"/> for the same reason
    /// <see cref="ChoosePool"/> is.
    /// </summary>
    private GameObject DrawFrom(List<GameObject> pool, out bool drewGroup, out int slotCount)
    {
        slotCount = BuildSlots(pool);
        drewGroup = false;

        if (slotCount == 0) return null;

        int slot = UnityEngine.Random.Range(0, slotCount);
        if (slot < individualSlots.Count) return individualSlots[slot];

        drewGroup = true;
        return groupedSlots[UnityEngine.Random.Range(0, groupedSlots.Count)];
    }

    /// <summary>
    /// The roll. New Weapon Chance decides whether this is a weapon the player has never killed
    /// with, then the draw inside that list is uniform over its slots. Destroyed entries are
    /// compacted out first, and reported, because one would otherwise produce a roll that spawns
    /// nothing.
    /// </summary>
    public GameObject Roll(int rollId)
    {
        EnsureInitialized();

        int compactedNulls = obtained_Items.RemoveAll(item => item == null)
            + hasKill_Items.RemoveAll(item => item == null);

        List<GameObject> pool = ChoosePool(out bool wantNew);
        bool drewNew = pool == obtained_Items;

        GameObject prefab = DrawFrom(pool, out bool drewGroup, out int slotCount);
        if (prefab == null)
        {
            DiagLog.RR(rollId, "roll",
                $"slotCount=0 wantNew={wantNew} compactedNulls={compactedNulls} — nothing to roll");
            return null;
        }

        NetworkObject nob = prefab.GetComponent<NetworkObject>();

        // prefabId/collectionId are logged here AND on the server's spawn so the two can be
        // diffed across the two machines' logs, which is the decisive test for whether the
        // peers' SpawnablePrefabs tables agree.
        DiagLog.RR(rollId, "roll",
            $"newChance={ArchipelagoMenu.NewWeaponChance.Value} wantNew={wantNew} " +
            $"drewFrom={(drewNew ? "obtained_Items" : "hasKill_Items")} " +
            $"obtained={obtained_Items.Count} hasKill={hasKill_Items.Count} " +
            $"poolCount={pool.Count} slotCount={slotCount} drewGroup={drewGroup} " +
            $"prefab={prefab.name} " +
            $"prefabId={(nob == null ? "NO-NETWORKOBJECT" : nob.PrefabId.ToString())} " +
            $"collectionId={(nob == null ? "n/a" : nob.SpawnableCollectionId.ToString())} " +
            $"compactedNulls={compactedNulls}");

        return prefab;
    }

    /// <summary>
    /// Draws many times through the real selection path and reports the spread against what the
    /// odds should be, so the distribution is a number in the log rather than a claim about the
    /// code. Behind the K debug key.
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

            GameObject picked = DrawFrom(pool, out _, out _);
            if (picked == null) continue;

            hits.TryGetValue(picked, out int count);
            hits[picked] = count + 1;
        }

        // What each weapon's share should be. When one list is empty the fallback sends every
        // draw to the other one, whatever the configured chance says.
        double chance = ArchipelagoMenu.NewWeaponChance.Value / 100d;
        double newShare = killCount == 0 ? 1d : newCount == 0 ? 0d : chance;
        double killShare = 1d - newShare;

        double worstDeviation = 0d;
        var report = new System.Text.StringBuilder();

        // Fixed column widths, so a skewed weapon is visible by scanning down a column. The
        // name column is sized to the longest name across both lists.
        const string listColumn = "has kill";
        const string slotColumn = "shared";
        int nameWidth = Math.Max("weapon".Length, LongestName(obtained_Items, LongestName(hasKill_Items, 0)));
        int hitsWidth = Math.Max("hits".Length, iterations.ToString().Length);

        report.AppendLine(
            $"  {"list".PadRight(listColumn.Length)}  {"slot".PadRight(slotColumn.Length)}  " +
            $"{"weapon".PadRight(nameWidth)}  " +
            $"{"hits".PadLeft(hitsWidth)}  {"actual".PadLeft(8)}  {"expected".PadLeft(8)}  {"off by".PadLeft(8)}");

        void ReportList(List<GameObject> pool, string label, double listShare)
        {
            // The same split the draw itself makes, so the expected column describes the code
            // being tested rather than a second reading of it.
            int slotCount = BuildSlots(pool);
            if (slotCount == 0) return;

            // Every weapon in the shared slot splits that one slot's odds between them.
            double groupedShare = groupedSlots.Count == 0
                ? 0d
                : listShare / slotCount / groupedSlots.Count;

            ReportSlot(individualSlots, "own", listShare / slotCount);
            ReportSlot(groupedSlots, slotColumn, groupedShare);

            void ReportSlot(List<GameObject> weapons, string slotLabel, double weaponShare)
            {
                foreach (GameObject weapon in weapons)
                {
                    double expected = weaponShare * iterations;
                    hits.TryGetValue(weapon, out int observed);

                    double deviation = expected > 0d ? Math.Abs(observed - expected) / expected * 100d : 0d;
                    if (deviation > worstDeviation) worstDeviation = deviation;

                    report.AppendLine(
                        $"  {label.PadRight(listColumn.Length)}  {slotLabel.PadRight(slotColumn.Length)}  " +
                        $"{(weapon == null ? "null" : weapon.name).PadRight(nameWidth)}  " +
                        $"{observed.ToString().PadLeft(hitsWidth)}  " +
                        $"{$"{observed / (double)iterations * 100d:F2}%".PadLeft(8)}  " +
                        $"{$"{weaponShare * 100d:F2}%".PadLeft(8)}  " +
                        $"{$"{deviation:F2}%".PadLeft(8)}");
                }
            }
        }

        ReportList(obtained_Items, "no kill", newShare);
        ReportList(hasKill_Items, listColumn, killShare);

        Plugin.BepinLogger.LogInfo(
            $"[RouletteState] distribution self-test: {iterations} draws, " +
            $"New Weapon Chance={ArchipelagoMenu.NewWeaponChance.Value}% " +
            $"over {newCount} no-kill and {killCount} has-kill weapons, " +
            $"{weaponsWithoutChecks.Count} of which carry no check and share one " +
            $"slot.{Environment.NewLine}" +
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
