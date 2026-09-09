using System;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Utils;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

/// <summary>
/// Whether the local player is currently stunned, which is the one kind of "cannot move" the
/// Metronome countdown runs straight through. The game has no stun flag to read: the taser,
/// the stun grenade and the stun mine all end at <c>PlayerHealth.UnfreezePlayer(seconds)</c>,
/// so the only way to tell a stun from a round-transition freeze is to notice the call that
/// started one.
/// </summary>
internal static class StunWatch
{
    /// <summary>
    /// How long past a stun's own length it still counts as one. The unfreeze is a ServerRpc,
    /// so canMove comes back a round trip after the stun's seconds are up.
    /// </summary>
    private const float UnfreezeGrace = 1.5f;

    /// <summary>When the running stun's own seconds are up. Negative infinity means none.</summary>
    private static float stunEndsAt = float.NegativeInfinity;

    /// <summary>Called from <see cref="PlayerHealthStunPatch"/> as a stun begins.</summary>
    public static void StunStarted(float seconds)
    {
        // Max, not assignment: a shorter stun landing on top of a longer one must not cut the
        // longer one short.
        stunEndsAt = Mathf.Max(stunEndsAt, Time.time + seconds);
    }

    public static bool IsStunned(FirstPersonController controller)
    {
        // Moving again, so whatever stun there was is over. Cleared rather than merely
        // reported, so the grace above cannot be inherited by an unrelated freeze later.
        if (controller != null && controller.canMove)
        {
            stunEndsAt = float.NegativeInfinity;
            return false;
        }

        return Time.time <= stunEndsAt + UnfreezeGrace;
    }
}

/// <summary>
/// Runs the Metronome countdown down, one frame at a time, off the local player's
/// PlayerHealth.Update.
/// </summary>
[HarmonyPatch(typeof(PlayerHealth), "Update")]
public class PlayerHealthMetronomeTickPatch
{
    static void Postfix(PlayerHealth __instance)
    {
        try
        {
            // Repeated here because a postfix still runs after vanilla's own IsOwner return.
            if (__instance == null || !__instance.IsOwner) return;

            MetronomeTrap.Tick(__instance);
        }
        catch (Exception error)
        {
            // A countdown that cannot tick must not abandon the rest of the player's Update.
            Plugin.BepinLogger.LogError($"[Metronome] Failed to tick the countdown{Environment.NewLine}{error}");
        }
    }
}

/// <summary>
/// Notices a stun starting, so the Metronome countdown can carry on through it. Vanilla hands
/// <c>UnfreezePlayer(stunTime)</c> straight to StartCoroutine, so the frame this returns on is
/// the frame the stun begins.
/// </summary>
[HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.UnfreezePlayer))]
public class PlayerHealthStunPatch
{
    static void Postfix(PlayerHealth __instance, float time)
    {
        try
        {
            if (__instance == null || !__instance.IsOwner) return;

            StunWatch.StunStarted(time);
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[Metronome] Failed to record a stun{Environment.NewLine}{error}");
        }
    }
}
