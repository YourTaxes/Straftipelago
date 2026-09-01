using System;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Utils;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

/// <summary>
/// The room's Metronome trap: a countdown in the corner of the screen that beats
/// "tick and tock and" into the kill feed until it runs out.
/// </summary>
/// <remarks>
/// <para>How long a Metronome runs and how fast it beats are both config entries -
/// <see cref="ArchipelagoMenu.MetronomeTrapSeconds"/> and
/// <see cref="ArchipelagoMenu.MetronomeTickSeconds"/> - rather than numbers the apworld sends,
/// because neither is part of the two repos' slot-data contract. Both are hidden from the Mod
/// Menu page for the same reason the Green Mode tint is: they are tuning knobs, not settings a
/// player is meant to reach for mid-match.</para>
/// <para>Everything here runs on the main thread and nothing locks: the countdown is spent in
/// <see cref="Tick"/> out of PlayerHealth.Update, read in ArchipelagoOverlay's OnGUI, and added
/// to from a <see cref="MainThreadActions"/> action - the received item itself arrives on the
/// Archipelago client's websocket thread and is queued there, the same way every other item is.
/// </para>
/// </remarks>
internal static class MetronomeTrap
{
    /// <summary>The beat, in the order it is printed, repeating.</summary>
    private static readonly string[] TickWords = { "tick", "and", "tock", "and" };

    /// <summary>
    /// Floor on the configured beat interval, so a hand-edited .cfg cannot make the
    /// catch-up loop in <see cref="Tick"/> run away. The config's own
    /// AcceptableValueRange already refuses anything smaller; this is the second belt.
    /// </summary>
    private const float MinimumTickSeconds = 0.05f;

    /// <summary>The tag every line this trap writes is filed under in LogOutput.log.</summary>
    private const string FeedTag = "Metronome";

    private static float secondsRemaining;

    /// <summary>Time spent since the last beat was printed, in the countdown's own time.</summary>
    private static float sinceLastTick;

    /// <summary>Which word of <see cref="TickWords"/> the next beat prints.</summary>
    private static int tickIndex;

    /// <summary>
    /// What the overlay draws. Zero means no countdown is running and nothing is drawn.
    /// </summary>
    public static float SecondsRemaining => secondsRemaining;

    /// <summary>
    /// Starts a countdown, or extends the one already running by the same amount.
    /// </summary>
    /// <remarks>
    /// Extending rather than restarting is what makes two traps in quick succession worse than
    /// one, instead of the second silently replacing the first. The beat is left where it is on
    /// an extension - the metronome never stopped, so restarting its phase would only make it
    /// stutter.
    /// </remarks>
    /// <param name="sender">The slot that sent the trap, for the line the player is shown.
    /// Blank or null drops the clause rather than naming nobody.</param>
    public static void Receive(string sender)
    {
        int seconds = ArchipelagoMenu.MetronomeTrapSeconds.Value;

        // Guarded rather than trusted even though the config's AcceptableValueRange starts at 1:
        // a zero here would print a start line for a countdown that ends on the same frame.
        if (seconds < 1)
        {
            Plugin.BepinLogger.LogWarning(
                $"[{FeedTag}] a Metronome arrived but the configured length is {seconds} seconds; ignoring it.");
            return;
        }

        string from = string.IsNullOrWhiteSpace(sender) ? "" : $" from {sender}";

        if (secondsRemaining > 0f)
        {
            secondsRemaining += seconds;
            KillFeed.Write(FeedTag,
                $"Another Metronome{from} - {seconds} more seconds, {Mathf.CeilToInt(secondsRemaining)} to go");
            return;
        }

        secondsRemaining = seconds;

        // The beat starts from silence, so the first word lands one full interval in rather than
        // on top of the start line.
        sinceLastTick = 0f;
        tickIndex = 0;

        KillFeed.Write(FeedTag, $"Metronome{from} started - {seconds} seconds of tick tock");
    }

    /// <summary>
    /// Spends this frame of the countdown, if the player is in a state to be spending it.
    /// Called every frame from <see cref="PlayerHealthMetronomeTickPatch"/>.
    /// </summary>
    /// <remarks>
    /// <para>The gate is the local player being alive and actionable: a trap that ran down while
    /// they were dead, between rounds, or sitting in a menu would mostly be spent on time they
    /// were never playing. Being in PlayerHealth.Update covers "in a match" on its own - there is
    /// no local PlayerHealth on the menu.</para>
    /// <para>controller.canMove, NOT playerHealth.canMove, for the reason DeathLinkHandler.KillPlayer
    /// gives: the one on PlayerHealth is set true in the constructor and never written again, while
    /// the controller's is the live one that PlayerManager.SetPlayerMove lowers for the round
    /// transitions.</para>
    /// <para>A stun is the deliberate exception. It lowers that same canMove (see
    /// <see cref="StunWatch"/>), and a metronome that stopped every time the player was tased
    /// would reward being stunned - so the countdown carries on through one.</para>
    /// </remarks>
    public static void Tick(PlayerHealth playerHealth)
    {
        // Cheap early-out for the overwhelmingly common case: this runs every frame, and almost
        // none of them have a countdown running.
        if (secondsRemaining <= 0f) return;

        if (playerHealth == null || playerHealth.controller == null) return;

        // Dead or dying. Held rather than spent, so the rest of the countdown is waiting when
        // they respawn.
        if (playerHealth.health <= 0f) return;

        if (!playerHealth.controller.canMove && !StunWatch.IsStunned(playerHealth.controller)) return;

        float delta = Time.deltaTime;
        secondsRemaining -= delta;
        sinceLastTick += delta;

        float tickSeconds = Mathf.Max(MinimumTickSeconds, ArchipelagoMenu.MetronomeTickSeconds.Value);

        // A loop, not a single test: one long frame - a hitch, or the scene load at the start of a
        // round - can cover more than one beat, and the metronome should not lose its place over it.
        // Subtracting the interval rather than zeroing keeps the beat on its own clock instead of
        // drifting a little later every frame.
        while (sinceLastTick >= tickSeconds && secondsRemaining > 0f)
        {
            sinceLastTick -= tickSeconds;
            KillFeed.Write(FeedTag, TickWords[tickIndex]);

            // Wrapped here rather than indexed with a modulo, so the counter cannot run away
            // over a long session and is always a legal index on its own.
            tickIndex = (tickIndex + 1) % TickWords.Length;
        }

        if (secondsRemaining > 0f) return;

        secondsRemaining = 0f;
        sinceLastTick = 0f;
        tickIndex = 0;
        KillFeed.Write(FeedTag, "Metronome wound down");
    }
}

/// <summary>
/// Whether the local player is currently stunned, which is the one kind of "cannot move"
/// the Metronome countdown runs straight through.
/// </summary>
/// <remarks>
/// <para>There is no stun flag in the game to read: the taser, the stun grenade and the stun mine
/// all end at <c>PlayerHealth.UnfreezePlayer(seconds)</c>, which sets the controller's canMove
/// false and starts a coroutine that waits out the seconds and puts it back. So a stun is
/// indistinguishable from a round-transition freeze by state alone, and the only way to tell them
/// apart is to notice the call that started one - which is what
/// <see cref="PlayerHealthStunPatch"/> does.</para>
/// <para>Single-slot rather than a list: overlapping stuns only ever extend the window, and
/// <see cref="Mathf.Max"/> over the expected end is the whole of that.</para>
/// </remarks>
internal static class StunWatch
{
    /// <summary>
    /// How long past a stun's own length it still counts as one.
    /// </summary>
    /// <remarks>
    /// The coroutine's last step is <c>UnfreezePlayerServer()</c>, a ServerRpc, so canMove comes
    /// back a round trip after the stun's seconds are up - without this the countdown would stall
    /// for that gap on every stun. Deliberately short: the window is also closed the moment
    /// canMove returns, so this only matters when a stun ends into some *other* freeze, and 1.5
    /// seconds bounds how much of such a freeze can be miscounted as stun time.
    /// </remarks>
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
        // Moving again, so whatever stun there was is over - including one whose unfreeze came
        // back early. Cleared rather than merely reported, so the grace above cannot be inherited
        // by an unrelated freeze later.
        if (controller != null && controller.canMove)
        {
            stunEndsAt = float.NegativeInfinity;
            return false;
        }

        return Time.time <= stunEndsAt + UnfreezeGrace;
    }
}

/// <summary>
/// Runs the Metronome countdown down, one frame at a time.
/// </summary>
/// <remarks>
/// On PlayerHealth.Update, and a postfix, for the same reasons
/// <see cref="PlayerHealthBuffPatch"/> is: it is the mod's per-frame hook that only exists while
/// there is a local player, it hands over that player's PlayerHealth, and a postfix lets vanilla
/// finish this frame's own bookkeeping first. The IsOwner test is repeated here because a
/// postfix still runs after vanilla's own <c>if (!IsOwner) return;</c>.
/// </remarks>
[HarmonyPatch(typeof(PlayerHealth), "Update")]
public class PlayerHealthMetronomeTickPatch
{
    static void Postfix(PlayerHealth __instance)
    {
        try
        {
            if (__instance == null || !__instance.IsOwner) return;

            MetronomeTrap.Tick(__instance);
        }
        catch (Exception error)
        {
            // Swallowed on purpose, like every other patch on this method: a countdown that
            // cannot tick must not abandon the rest of the player's Update.
            Plugin.BepinLogger.LogError($"[Metronome] Failed to tick the countdown{Environment.NewLine}{error}");
        }
    }
}

/// <summary>
/// Notices a stun starting, so the Metronome countdown can carry on through it.
/// </summary>
/// <remarks>
/// <para>A postfix on the iterator method rather than on the coroutine's MoveNext: vanilla calls
/// <c>UnfreezePlayer(stunTime)</c> and hands the result straight to StartCoroutine in the same
/// breath (in <c>PlayerHealth.RpcLogic___TaserEnemyTarget</c> and <c>Taser</c>'s own), so the
/// frame this returns on is the frame the stun begins, and the seconds are right there in the
/// argument.</para>
/// <para>Both call sites are TargetRpcs aimed at the stunned player's own client, so this is
/// already only ever the local player - the IsOwner test says so rather than assuming it.</para>
/// </remarks>
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
