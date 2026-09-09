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
/// Runs PlayerPickup.Update by hand while the Roulette Item is held, skipping the parts of
/// vanilla's body that dereference the weapon fields the roulette does not have. Also pumps
/// the pending-roll timeout and the debug keys for the local player.
/// </summary>
[HarmonyPatch(typeof(PlayerPickup), "Update")]
public class PlayerPickupUpdatePatch
{
    static bool Prefix(PlayerPickup __instance)
    {
        // Gated on IsOwner because this prefix runs once per PlayerPickup instance per frame,
        // so without it one keypress would act once per player in the match.
        if (__instance.IsOwner)
        {
            PendingRoll.CheckTimeout();
            DebugKeys();
        }

        Traverse trav = Traverse.Create(__instance);
        if (trav.Field("weaponInHand").GetValue<Weapon>() != null) return true;

        GameObject objInHand = trav.Method("sync___get_value_objInHand").GetValue<GameObject>();
        if (objInHand == null) return true;

        ItemBehaviour item = objInHand.GetComponent<ItemBehaviour>();
        if (item == null || item.weaponName != "Roulette Item") return true;

        // weaponInHand IS set (the roulette's assetbundle-bound Gun component), but none of
        // Gun/Weapon's fields are wired up like a real weapon, so vanilla RightHandFix and
        // LeftHandFix would dereference null. Run the IK update on its own instead.
        trav.Method("UpdateIKPoistion").GetValue();

        if (!__instance.IsOwner) return false;

        // RightHandFix/LeftHandFix internally call RightHandDrop/LeftHandDrop, which also
        // dereference weaponInHand.
        trav.Field("dropTimer").SetValue(trav.Field("dropTimer").GetValue<float>() - Time.deltaTime);
        trav.Field("interactTimer").SetValue(trav.Field("interactTimer").GetValue<float>() - Time.deltaTime);

        if (!trav.Method("sync___get_value_hasObjectInHand").GetValue<bool>())
        {
            var pc = trav.Field("playerController").GetValue<FirstPersonController>();
            pc.movementFactor = 1f;
            pc.jumpFactor = 1f;
            pc.maxWallJumps = 1;
            pc.wallJumpFactor = 1f;
        }

        Camera cam = trav.Field("cam").GetValue<Camera>();
        if (cam != null)
        {
            trav.Method("HandleInteractionCheck").GetValue();
            trav.Method("HandleInteractEnvironment").GetValue();
            trav.Method("HandleAboubiGrab").GetValue();

            Animator animator = trav.Field("animator").GetValue<Animator>();
            Animator globalAnimator = trav.Field("globalAnimator").GetValue<Animator>();

            if (item.rightHandAnim == "")
            {
                animator.SetBool("TwoHanded", false);
                animator.SetBool("DoubleHanded", false);
                animator.SetBool("RightHanded", true);
            }
            globalAnimator.SetBool("TwoHanded", false);
            globalAnimator.SetBool("DoubleSingle", false);
            globalAnimator.SetBool("SingleHanded", true);
            globalAnimator.SetBool("LeftHanded", false);
        }

        return false;
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
