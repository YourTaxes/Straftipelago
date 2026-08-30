using System;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Utils;

namespace Straftapelago.Finnegan_McD.org.Patches;

/// <summary>
/// The room's Health filler item: sets the local player's health to
/// <see cref="BuffedHealth"/>.
/// </summary>
/// <remarks>
/// <para>Queued rather than applied where it is received, for the same reason a death link is.
/// The item arrives on the Archipelago client's websocket thread; there is no PlayerHealth to
/// write to from there, and touching one would be a Unity call off the main thread.</para>
/// <para>It also has to wait for a player at all. An item can land while the player is on the
/// menu, dead, or between rounds, and a buff spent on a PlayerHealth that is about to be
/// replaced is a buff the player never got - so this holds it until there is a living local
/// player to give it to, the same way <c>DeathLinkHandler.KillPlayer</c> holds a death.</para>
/// </remarks>
internal static class PlayerHealthBuff
{
    /// <summary>
    /// What the room's Health item sets the player to.
    /// </summary>
    /// <remarks>
    /// In the units the field is actually stored in, which are NOT the units the HUD shows. The
    /// display is this number times 25, so a round starts at 4 and reads 100, and this 8 reads
    /// 200 - double health. Writing the displayed number here instead is what put 125000 on
    /// screen. See the matching note on the item in the apworld's items.py.
    /// </remarks>
    private const float BuffedHealth = 8f;

    /// <summary>
    /// Buffs waiting for a living local player. A count rather than a queue of objects: every
    /// one of these is identical, and the only thing worth keeping is how many are owed.
    /// </summary>
    private static int pending;

    public static void Enqueue()
    {
        lock (Gate)
        {
            pending++;
        }

        Plugin.BepinLogger.LogDebug($"[Health] queued a Health buff from the room; {pending} pending");
    }

    private static readonly object Gate = new();

    /// <summary>
    /// Called every frame from <see cref="PlayerHealthBuffPatch"/> with the local player's
    /// health. Spends one pending buff if there is one and the player can use it.
    /// </summary>
    public static void Apply(PlayerHealth playerHealth)
    {
        // Cheap early-out for the overwhelmingly common case: this runs every frame, and almost
        // every one of them has nothing waiting.
        lock (Gate)
        {
            if (pending < 1) return;
        }

        if (playerHealth == null) return;

        // A buff waits rather than being spent. At or below zero the player is already dying or
        // dead, and healing a corpse does nothing the player would ever see - the same reason
        // KillPlayer refuses to kill one.
        if (playerHealth.health <= 0f) return;

        // Nothing to give: they are already at or above what this would set them to. Held, not
        // discarded, so a buff received during an earlier full-health moment still lands after
        // the player takes damage.
        if (playerHealth.health >= BuffedHealth) return;

        lock (Gate)
        {
            if (pending < 1) return;
            pending--;
        }

        float before = playerHealth.health;
        playerHealth.health = BuffedHealth;

        // Logged with both numbers because this is a plain field write on the owning client,
        // not the ServerRpc route the kill path takes. If vanilla clamps it back down or the
        // server overwrites it, these two lines are what shows that.
        Plugin.BepinLogger.LogInfo($"[Health] Archipelago Health buff: {before} -> {playerHealth.health}");
        Killfeed.Write("Archipelago patched you up");
    }
}

/// <summary>
/// Pumps the Health buff queue every frame, so a buff received from the room is applied as soon
/// as the player is in a state to use it.
/// </summary>
/// <remarks>
/// Its own patch class rather than a second job for
/// <see cref="PlayerHealthDeathLinkKillPatch"/>: Harmony is happy to run several postfixes on
/// one method, and a buff has nothing to do with death link beyond needing the same per-frame
/// hook and the same source of the local player's PlayerHealth. A postfix for the same reason
/// that one is - vanilla finishes this frame's own health bookkeeping first.
/// </remarks>
[HarmonyPatch(typeof(PlayerHealth), "Update")]
public class PlayerHealthBuffPatch
{
    static void Postfix(PlayerHealth __instance)
    {
        try
        {
            if (__instance == null || !__instance.IsOwner) return;

            PlayerHealthBuff.Apply(__instance);
        }
        catch (Exception error)
        {
            // Swallowed on purpose, like every other patch on this method: a failed buff must
            // not abandon the rest of the player's Update.
            Plugin.BepinLogger.LogError($"[Health] Failed to apply a Health buff{Environment.NewLine}{error}");
        }
    }
}
