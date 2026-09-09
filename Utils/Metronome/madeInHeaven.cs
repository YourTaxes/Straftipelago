using MyceliumNetworking;
using Straftapelago.Finnegan_McD.org.Patches;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// The room's Made in Heaven buff: it starts a metronome on every machine in the lobby except
/// the activating player's, slowly at first and accelerating as its countdown runs out.
/// The countdown is what drives the beat, and it holds between rounds. The lobby-wide half
/// travels over Mycelium through <see cref="MadeInHeavenNet"/>: the activation starts it
/// everywhere, and at the end of every round the host broadcasts what is left on its own clock
/// for every client to adopt. A second Made in Heaven replaces the first rather than extending
/// it.
/// </summary>
internal static class MadeInHeaven
{
    /// <summary>The tag every line this buff writes is filed under in LogOutput.log.</summary>
    private const string FeedTag = "MadeInHeaven";

    /// <summary>What the activating player says as it goes off.</summary>
    private const string ActivationCry = "I will remake this universe according to my master's plan!";

    /// <summary>What everyone else is told, with the activating player's name in it.</summary>
    private const string RemoteActivationFormat = "{0} has activated the ultimate stand!";

    /// <summary>The name shown when the activating player's own name cannot be resolved.</summary>
    private const string UnknownActivator = "Someone";

    private static float secondsRemaining;

    /// <summary>
    /// How long this Made in Heaven was started for. The denominator of the ramp, so it has to
    /// survive for the whole countdown.
    /// </summary>
    private static float totalSeconds;

    /// <summary>The interval the beat opens on, and the one it accelerates toward.</summary>
    private static float startTickSeconds;

    /// <inheritdoc cref="startTickSeconds"/>
    private static float endTickSeconds;

    /// <summary>
    /// Whether the multiworld handed this one to the player sitting at this machine. The buff's
    /// owner is the one player it does not beat.
    /// </summary>
    private static bool isActivator;

    /// <summary>
    /// Last frame's <c>PauseManager.BetweenRounds</c>, so <see cref="Tick"/> can see the moment
    /// a round ends. That edge is the resync point.
    /// </summary>
    private static bool wasBetweenRounds;

    /// <summary>
    /// What the overlay draws, in the slot the Metronome countdown otherwise uses. Zero means
    /// nothing is running and nothing is drawn.
    /// </summary>
    public static float SecondsRemaining => secondsRemaining;

    /// <summary>Whether a Made in Heaven is running at all, held between rounds included.</summary>
    public static bool Running => secondsRemaining > 0f;

    /// <summary>
    /// How long the current beat should be held: the interval lerped from
    /// <see cref="startTickSeconds"/> to <see cref="endTickSeconds"/> across how far through the
    /// countdown it is. Read fresh on every beat, which is what makes it a ramp.
    /// </summary>
    private static float CurrentInterval
    {
        get
        {
            // A resync from an older build, or an activation that carried no length.
            if (totalSeconds <= 0f) return startTickSeconds;

            // Clamped because secondsRemaining can sit a hair above totalSeconds on the frame
            // it starts, and a hair below zero on the frame it ends.
            float progress = Mathf.Clamp01(1f - secondsRemaining / totalSeconds);
            return Mathf.Lerp(startTickSeconds, endTickSeconds, progress);
        }
    }

    /// <summary>
    /// Takes delivery of one Made in Heaven from the room. Runs only on the machine the
    /// multiworld gave it to, and announces it to the rest of the lobby.
    /// </summary>
    /// <param name="sender">The slot that sent it. Blank or null drops the clause rather than
    /// naming nobody.</param>
    public static void Receive(string sender)
    {
        int seconds = ArchipelagoMenu.MadeInHeavenSeconds.Value;

        // A zero would announce a countdown to the whole lobby that ends on the frame it starts.
        if (seconds < 1)
        {
            Plugin.BepinLogger.LogWarning(
                $"[{FeedTag}] a Made in Heaven arrived but the configured length is {seconds} " +
                "seconds; ignoring it.");
            return;
        }

        float startTick = ArchipelagoMenu.MadeInHeavenStartTickSeconds.Value;
        float endTick = ArchipelagoMenu.MadeInHeavenEndTickSeconds.Value;

        string from = string.IsNullOrWhiteSpace(sender) ? "" : $" from {sender}";
        Plugin.BepinLogger.LogInfo(
            $"[{FeedTag}] activating a Made in Heaven{from} for {seconds}s, beat {startTick}s -> {endTick}s");

        // The one place this is ever set: whoever the multiworld gave the buff to is the one
        // player it does not beat.
        isActivator = true;

        // The activating player's own line and countdown, applied here rather than waiting for
        // the broadcast to come back around. MadeInHeavenNet drops the sender's own copy.
        KillFeed.Write(FeedTag, ActivationCry);
        Begin(seconds, startTick, endTick);

        // The two intervals travel with the length, so the whole lobby swings to one rhythm
        // rather than to whatever each machine has in its own .cfg.
        MadeInHeavenNet.Announce(KillFeed.LocalPlayerName, seconds, startTick, endTick);
    }

    /// <summary>
    /// Starts the countdown on this machine after an activation somewhere else in the lobby.
    /// </summary>
    /// <param name="playerName">Whoever set it off, as the lobby knows them.</param>
    /// <param name="seconds">The length they activated it for, so every machine counts the same.</param>
    /// <param name="startTick">Their beat interval at the start, so the lobby swings in unison.</param>
    /// <param name="endTick">Their beat interval at the end, likewise.</param>
    public static void ReceiveRemote(string playerName, int seconds, float startTick, float endTick)
    {
        if (seconds < 1)
        {
            Plugin.BepinLogger.LogWarning(
                $"[{FeedTag}] a Made in Heaven arrived over the network with a length of " +
                $"{seconds} seconds; ignoring it.");
            return;
        }

        string who = string.IsNullOrWhiteSpace(playerName) ? UnknownActivator : playerName;

        // Somebody else's buff, so this machine is one of the ones it beats.
        isActivator = false;

        KillFeed.Write(FeedTag, string.Format(RemoteActivationFormat, who));
        Begin(seconds, startTick, endTick);
    }

    /// <summary>
    /// Takes the host's word for how much is left, called on every client as a round ends.
    /// Trimming drift off a running countdown is silent; adopting one this machine knew nothing
    /// about is announced.
    /// </summary>
    public static void AdoptRemaining(float seconds, float total, float startTick, float endTick)
    {
        // The host only sends this while its own is running, so a zero describes nothing.
        if (seconds <= 0f) return;

        bool wasRunning = Running;
        float before = secondsRemaining;

        secondsRemaining = seconds;

        // The ramp comes with it, so every machine sits at the same point on the curve.
        totalSeconds = total;
        startTickSeconds = startTick;
        endTickSeconds = endTick;

        if (wasRunning)
        {
            // isActivator is left alone: it is the host's CLOCK that is authoritative, not its
            // idea of who owns the buff.
            Plugin.BepinLogger.LogDebug(
                $"[{FeedTag}] resynced to the host: {before:0.00}s -> {seconds:0.00}s");
            return;
        }

        Plugin.BepinLogger.LogInfo(
            $"[{FeedTag}] the host reports a Made in Heaven with {seconds:0.00}s left that this " +
            "machine knew nothing about; adopting it");

        // Nothing was running, so this machine cannot be the one that activated it.
        isActivator = false;

        if (MetronomeTrap.Cancel())
        {
            KillFeed.Write(FeedTag, "The metronome stops.");
        }

        MetronomeBeat.Start();
        KillFeed.Write(FeedTag, "Made in Heaven is already under way.");
    }

    /// <summary>
    /// The half every machine does the same way: throw out any Metronome trap it lands on,
    /// start the countdown over, and take up the beat unless this is the activating player's
    /// machine.
    /// </summary>
    private static void Begin(int seconds, float startTick, float endTick)
    {
        // A Made in Heaven outranks a Metronome, so the trap goes, along with the lean it was
        // holding the player in. A trap that arrives DURING one is queued instead.
        if (MetronomeTrap.Cancel())
        {
            KillFeed.Write(FeedTag, "The metronome stops.");
        }

        // Assignment, not addition: a second Made in Heaven replaces the first outright, ramp
        // and all.
        secondsRemaining = seconds;
        totalSeconds = seconds;
        startTickSeconds = startTick;
        endTickSeconds = endTick;

        // "Everyone except the activating player", in one line. Started rather than left alone,
        // so a second Made in Heaven restarts the rhythm from upright along with the clock.
        if (isActivator)
        {
            MetronomeBeat.Stop();
            return;
        }

        MetronomeBeat.Start();
    }

    /// <summary>
    /// Spends this frame of the countdown. Called from ArchipelagoOverlay.Update, which keeps
    /// running while the local player is dead or between spawns.
    /// </summary>
    public static void Tick()
    {
        // Read and latched ahead of every early-out below, because it is the EDGE that matters
        // for the resync.
        bool betweenRounds = PauseManager.BetweenRounds;
        bool roundJustEnded = betweenRounds && !wasBetweenRounds;
        wasBetweenRounds = betweenRounds;

        // Cheap early-out: this runs every frame of the session and almost none of them have a
        // Made in Heaven running.
        if (secondsRemaining <= 0f) return;

        // Not in a match at all. Held rather than spent, the same way it is held between rounds.
        if (PauseManager.Instance == null) return;

        // The host's clock is the lobby's clock: every machine counts its own copy down off its
        // own Time.deltaTime, and the gap between rounds is when a correction can land safely.
        if (roundJustEnded && MyceliumNetwork.IsHost)
        {
            MadeInHeavenNet.Resync(secondsRemaining, totalSeconds, startTickSeconds, endTickSeconds);
        }

        if (betweenRounds) return;

        float delta = Time.deltaTime;
        secondsRemaining -= delta;

        // The beat, on the interval this moment in the countdown calls for, which is what makes
        // it accelerate. Skipped on the activating player's machine, where no beat was started.
        if (!isActivator)
        {
            MetronomeBeat.Advance(delta, CurrentInterval);
        }

        if (secondsRemaining > 0f) return;

        secondsRemaining = 0f;

        // Upright again, and the player has their own lean back from this frame on.
        MetronomeBeat.Stop();
        isActivator = false;

        KillFeed.Write(FeedTag, "The Heaven plan has completed.");

        // Zeroed BEFORE this call, because ReleaseHeld reads Running to decide whether to hold
        // the queued trap back again.
        MetronomeTrap.ReleaseHeld();
    }
}
