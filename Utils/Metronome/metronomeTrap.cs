using Straftapelago.Finnegan_McD.org.Patches;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// The room's Metronome trap: a countdown in the corner of the screen that swings the player
/// left and right on <see cref="MetronomeBeat"/>'s rhythm until it runs out, taking their own
/// leaning off them while it does. Its length and beat interval come from
/// <see cref="ArchipelagoMenu.MetronomeTrapSeconds"/> and
/// <see cref="ArchipelagoMenu.MetronomeTickSeconds"/>, and everything here is main-thread only.
/// </summary>
internal static class MetronomeTrap
{
    /// <summary>The tag every line this trap writes is filed under in LogOutput.log.</summary>
    private const string FeedTag = "Metronome";

    private static float secondsRemaining;

    /// <summary>
    /// What the overlay draws. Zero means no countdown is running and nothing is drawn.
    /// </summary>
    public static float SecondsRemaining => secondsRemaining;

    /// <summary>How much held trap is waiting for a Made in Heaven to finish.</summary>
    private static int pendingSeconds;

    /// <summary>Who sent the first held trap, so the line it eventually starts with names them.</summary>
    private static string pendingSender;

    /// <summary>
    /// Starts a countdown, extends the one already running by the same amount, or holds it back
    /// until a Made in Heaven has finished.
    /// </summary>
    /// <param name="sender">The slot that sent the trap. Blank or null drops the clause rather
    /// than naming nobody.</param>
    public static void Receive(string sender)
    {
        int seconds = ArchipelagoMenu.MetronomeTrapSeconds.Value;

        // A zero here would print a start line for a countdown that ends on the same frame.
        if (seconds < 1)
        {
            Plugin.BepinLogger.LogWarning(
                $"[{FeedTag}] a Metronome arrived but the configured length is {seconds} seconds; ignoring it.");
            return;
        }

        if (MadeInHeaven.Running)
        {
            pendingSeconds += seconds;

            // The first one held is the one that gets named when the queue is released; later
            // ones only add their seconds.
            if (string.IsNullOrWhiteSpace(pendingSender)) pendingSender = sender;

            KillFeed.Write(FeedTag,
                $"A Metronome{FromClause(sender)} is waiting for Made in Heaven to finish - " +
                $"{pendingSeconds}s held");
            return;
        }

        Start(seconds, sender);
    }

    /// <summary>
    /// Starts whatever was held back while a Made in Heaven ran. Called by
    /// <see cref="MadeInHeaven"/> as its countdown ends.
    /// </summary>
    public static void ReleaseHeld()
    {
        if (pendingSeconds < 1) return;

        int seconds = pendingSeconds;
        string sender = pendingSender;

        // Cleared before starting, so a re-entrant call cannot release the queue twice.
        pendingSeconds = 0;
        pendingSender = null;

        Start(seconds, sender);
    }

    /// <summary>The " from X" on the end of a line, or nothing at all when X is not known.</summary>
    private static string FromClause(string sender) =>
        string.IsNullOrWhiteSpace(sender) ? "" : $" from {sender}";

    /// <summary>
    /// Puts <paramref name="seconds"/> on the clock, starting it if it is not already going.
    /// A second trap extends the first rather than restarting it, and leaves the beat's phase
    /// where it is.
    /// </summary>
    private static void Start(int seconds, string sender)
    {
        string from = FromClause(sender);

        if (secondsRemaining > 0f)
        {
            secondsRemaining += seconds;
            KillFeed.Write(FeedTag,
                $"Another Metronome{from} - {seconds} more seconds, {Mathf.CeilToInt(secondsRemaining)} to go");
            return;
        }

        secondsRemaining = seconds;
        MetronomeBeat.Start();

        KillFeed.Write(FeedTag, $"Metronome{from} started - {seconds} seconds of tick tock");
    }

    /// <summary>
    /// Ends the running countdown early and stands the player back up. Called on every client
    /// as a Made in Heaven activates, which outranks the trap.
    /// </summary>
    /// <returns>Whether there was anything to cancel, so the caller can stay quiet if not.</returns>
    public static bool Cancel()
    {
        if (secondsRemaining <= 0f) return false;

        secondsRemaining = 0f;

        // Releases the player's lean, so a Made in Heaven that cancels a trap and then starts
        // its own beat gets a clean phase.
        MetronomeBeat.Stop();
        return true;
    }

    /// <summary>
    /// Spends this frame of the countdown, if the local player is alive and able to act. A
    /// stun is the deliberate exception: the countdown carries on through one.
    /// </summary>
    public static void Tick(PlayerHealth playerHealth)
    {
        // Cheap early-out: this runs every frame and almost none of them have a countdown.
        if (secondsRemaining <= 0f) return;

        if (playerHealth == null || playerHealth.controller == null) return;

        // Dead or dying. Held rather than spent, so the rest of the countdown is waiting when
        // they respawn.
        if (playerHealth.health <= 0f) return;

        // controller.canMove, not playerHealth.canMove: the one on PlayerHealth is set true in
        // the constructor and never written again.
        if (!playerHealth.controller.canMove && !StunWatch.IsStunned(playerHealth.controller)) return;

        float delta = Time.deltaTime;
        secondsRemaining -= delta;

        // The same delta the countdown just spent, at a fixed interval.
        MetronomeBeat.Advance(delta, ArchipelagoMenu.MetronomeTickSeconds.Value);

        if (secondsRemaining > 0f) return;

        secondsRemaining = 0f;

        // Upright again, and the player has their own lean back from this frame on.
        MetronomeBeat.Stop();
        KillFeed.Write(FeedTag, "Metronome wound down");
    }
}
