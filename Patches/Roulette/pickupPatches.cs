using System.Diagnostics;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Utils;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

/// <summary>
/// Builds the roulette pool the first time a player object comes up, and times how long that
/// takes. EnsureInitialized, not Reset: Awake fires for every player object every round, and a
/// Reset here would wipe the pool mid-match.
/// </summary>
[HarmonyPatch(typeof(PlayerPickup), "Awake")]
public class PlayerPickupAwakePatch
{
    static void Prefix(PlayerPickup __instance)
    {
        if (DiagnosticFlags.SkipRouletteResetOnAwake)
        {
            DiagLog.Log("PlayerPickup.Awake", "RouletteState init SKIPPED via DiagnosticFlags");
            return;
        }

        Stopwatch sw = Stopwatch.StartNew();
        Plugin.RouletteState.EnsureInitialized();
        sw.Stop();

        DiagLog.Log("PlayerPickup.Awake",
            $"RouletteState.EnsureInitialized() took {sw.Elapsed.TotalMilliseconds:F2}ms " +
            $"obtained={Plugin.RouletteState.obtained_Items.Count} " +
            $"obj={__instance.gameObject.name} {DiagLog.NetRoles()}");
    }
}

/// <summary>
/// Pumps the pending-roll timeout and the debug keys for the local player, once a frame.
/// Vanilla's own Update runs untouched: the roulette carries a Gun, so every weapon field
/// PlayerPickup.Update and RightHandFix read off it resolves.
/// </summary>
[HarmonyPatch(typeof(PlayerPickup), "Update")]
public class PlayerPickupUpdatePatch
{
    static void Prefix(PlayerPickup __instance)
    {
        // Gated on IsOwner because this prefix runs once per PlayerPickup instance per frame,
        // so without it one keypress would act once per player in the match.
        if (!__instance.IsOwner) return;

        PendingRoll.CheckTimeout();
        DebugKeys();
    }

    /// <summary>
    /// The roulette debug keys, which do nothing unless Debug Buttons is ticked on the mod's
    /// Mod Menu page. The entry is read live, so ticking the box takes effect on the next
    /// frame, and null-checked because it does not exist until ArchipelagoMenu.Install has run.
    /// </summary>
    static void DebugKeys()
    {
        if (ArchipelagoMenu.DebugButtons == null || !ArchipelagoMenu.DebugButtons.Value) return;

        RouletteState roulette = Plugin.RouletteState;

        if (Input.GetKeyDown(KeyCode.O))
        {
            roulette.Reset();
            Plugin.BepinLogger.LogInfo($"Reset roulette item lists. unowned_items: {roulette.unowned_items.Count}, obtained_Items: {roulette.obtained_Items.Count}, hasKill_Items: {roulette.hasKill_Items.Count}");
        }

        if (Input.GetKeyDown(KeyCode.P) && roulette.unowned_items.Count > 0)
        {
            int randomIndex = Random.Range(0, roulette.unowned_items.Count);
            GameObject randomItem = roulette.unowned_items[randomIndex];
            if (roulette.Grant(randomItem))
            {
                Plugin.BepinLogger.LogInfo($"Added random item {randomItem.name} to obtained_Items. Remaining unowned_items: {roulette.unowned_items.Count}");
            }
        }

        // Unlocks everything at once, which is what makes the unobtainable-pickup rule testable
        // without playing far enough to earn the weapons.
        if (Input.GetKeyDown(KeyCode.I))
        {
            int moved = roulette.GrantAllUnowned();
            Plugin.BepinLogger.LogInfo($"Moved {moved} weapon(s) from unowned_items to obtained_Items. obtained_Items: {roulette.obtained_Items.Count}, unowned_items: {roulette.unowned_items.Count}");
        }

        // The other half of I: everything unlocked is marked as already used, which is how the
        // weapon-goal percentage gets tested. Local only, so a Reset takes it back.
        if (Input.GetKeyDown(KeyCode.L))
        {
            int earned = roulette.MarkAllObtainedKillEarned();
            Plugin.BepinLogger.LogInfo($"Moved {earned} weapon(s) from obtained_Items to hasKill_Items. hasKill_Items: {roulette.hasKill_Items.Count}, obtained_Items: {roulette.obtained_Items.Count}");
        }

        // Reports the roll distribution as a number in the log rather than as a claim about
        // the code.
        if (Input.GetKeyDown(KeyCode.K))
        {
            roulette.SelfTest(100000);
        }
    }
}
