using System;
using HarmonyLib;

namespace Straftapelago.Finnegan_McD.org.Patches;

/*
Both halves of DeathLink hang off PlayerHealth.Update, because that one method is
the only place in the game where the local player's death is observable no matter
what caused it.

Why not the kill emitters: killDetectPatches patches KillServer/SendKillLog, and
every one of those runs on the KILLER's machine. Being shot by somebody else
never reaches this client's mod code at all, so they cannot see a local death.
suicideDetectPatches has the opposite problem - Settings.IncreaseSuicidesAmount
only fires for deaths the player caused themselves, which is a third of them.

Why Update is the right funnel: its first three IL instructions are
`if (!IsOwner) return;`, so it only ever runs on the local player's own
PlayerHealth - exactly the "only the current player, not any other in the
Straftat server" requirement - and its death block is

    if (health <= 0f && health > -1000f && isKilled) { ...; health = -2000f; }
    else if (health <= 0f && health > -1000f)        { ...; health = -2000f; }

Every path out of that window clamps health to -2000f inside the same call, so a
prefix testing the window sees it true for exactly one Update per death. Shot,
melee, explosion, suicide, acid pool, void: all of them land here, once.
*/

/// <summary>
/// Notices the local player dying and hands it to the DeathLink handler to share
/// with the multiworld.
/// </summary>
/// <remarks>
/// A prefix, not a postfix: vanilla clamps health to -2000f before Update returns,
/// so by postfix time the death window has already closed and there is nothing
/// left to see. The IsOwner test is repeated here for the same reason - a prefix
/// runs ahead of vanilla's own early return, so this patch does not inherit it.
/// </remarks>
[HarmonyPatch(typeof(PlayerHealth), "Update")]
public class PlayerHealthDeathLinkSendPatch
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
            // Swallowed on purpose. This runs inside the local player's Update, and
            // a throw escaping here would abandon the rest of vanilla's death
            // handling - failing to share a death must not cost the player their
            // kill cam, their ragdoll or their respawn.
            Plugin.BepinLogger.LogError($"[DeathLink] Failed to report a local death{Environment.NewLine}{error}");
        }
    }
}

/// <summary>
/// Pumps the DeathLink handler's queue every frame, so a death received from the
/// multiworld is applied as soon as the player is in a state where it can be.
/// </summary>
/// <remarks>
/// <para>Polling rather than killing straight from the socket callback, because
/// <see cref="Archipelago.DeathLinkHandler.DeathLinkReceived"/> runs on the
/// Archipelago client's websocket thread and every step of the kill is a
/// main-thread-only Unity call. This is the same reason ArchipelagoConsole queues
/// its lines instead of writing them where they arrive.</para>
/// <para>A postfix, so vanilla has finished this frame's own death bookkeeping
/// before we consider imposing a new death on top of it. Everything about WHEN a
/// queued death may be spent lives in KillPlayer; this is only the pump, and the
/// source of the local player's PlayerHealth.</para>
/// </remarks>
[HarmonyPatch(typeof(PlayerHealth), "Update")]
public class PlayerHealthDeathLinkKillPatch
{
    static void Postfix(PlayerHealth __instance)
    {
        try
        {
            if (__instance == null || !__instance.IsOwner) return;

            Plugin.ArchipelagoClient?.DeathLinkHandler?.KillPlayer(__instance);
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[DeathLink] Failed to apply a received death{Environment.NewLine}{error}");
        }
    }
}
