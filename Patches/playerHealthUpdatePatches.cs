using System;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Utils;

namespace Straftapelago.Finnegan_McD.org.Patches;

/*
Everything the mod does once a frame on the local player hangs off PlayerHealth.Update, because
that one method is the only place in the game where the local player's death is observable no
matter what caused it. It starts with `if (!IsOwner) return;`, so it only ever runs on the local
player's own PlayerHealth, and its death block is

    if (health <= 0f && health > -1000f && isKilled) { ...; health = -2000f; }
    else if (health <= 0f && health > -1000f)        { ...; health = -2000f; }

Every path out of that window clamps health to -2000f inside the same call, so a prefix testing
the window sees it true for exactly one Update per death. Shot, melee, explosion, suicide, acid
pool, void: all of them land here, once.

One patch class rather than one per feature: each extra Harmony patch on the same method is
another trampoline, another try/catch and another IsOwner test on every frame, and the three
postfix jobs each begin with their own cheap early-out anyway.
*/

/// <summary>
/// The local player's per-frame hooks. The prefix notices the player dying and hands it to the
/// DeathLink handler to share - a prefix, because vanilla clamps health to -2000f before Update
/// returns and the death window has closed by postfix time. The postfix, after vanilla has
/// finished this frame's own bookkeeping, pumps the three queues that wait on a living local
/// player: a death received from the multiworld, a Health buff, and the Metronome countdown.
/// The IsOwner test is repeated in both because a patch runs regardless of vanilla's own early
/// return.
/// </summary>
[HarmonyPatch(typeof(PlayerHealth), "Update")]
public class PlayerHealthUpdatePatch
{
    static void Prefix(PlayerHealth __instance)
    {
        try
        {
            if (__instance == null || !__instance.IsOwner) return;

            // Vanilla's own death window. The upper bound is what makes this fire
            // once: the lower bound excludes the -2000f the previous frame's death
            // already clamped to, and -8f (what a despawn sets) is inside it.
            if (!(__instance.health <= 0f && __instance.health > -1000f)) return;

            Plugin.ArchipelagoClient?.DeathLinkHandler?.LocalPlayerDied(__instance);
        }
        catch (Exception error)
        {
            // Swallowed: a throw here would abandon the rest of vanilla's death handling, and
            // failing to share a death must not cost the player their ragdoll or their respawn.
            Plugin.BepinLogger.LogError($"[DeathLink] Failed to report a local death{Environment.NewLine}{error}");
        }
    }

    static void Postfix(PlayerHealth __instance)
    {
        if (__instance == null || !__instance.IsOwner) return;

        // Each in its own try/catch, so one feature that throws cannot starve the other two.
        try
        {
            // Polling rather than killing straight from the socket callback, because that
            // callback runs on the Archipelago client's websocket thread and every step of the
            // kill is a main-thread-only Unity call.
            Plugin.ArchipelagoClient?.DeathLinkHandler?.KillPlayer(__instance);
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[DeathLink] Failed to apply a received death{Environment.NewLine}{error}");
        }

        try
        {
            PlayerHealthBuff.Apply(__instance);
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[Health] Failed to apply a Health buff{Environment.NewLine}{error}");
        }

        try
        {
            MetronomeTrap.Tick(__instance);
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[Metronome] Failed to tick the countdown{Environment.NewLine}{error}");
        }
    }
}
