using System;
using System.Collections.Generic;
using HarmonyLib;
using MyceliumNetworking;
using Steamworks;
using Straftapelago.Finnegan_McD.org.Utils;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

/*
Everything metronome-shaped lives in this one file: the trap the room sends, the countdown it
runs, the leaning modifier patches that countdown switches on, and the Made in Heaven buff -
which is the same metronome aimed at the whole lobby instead of at one player, so it will want
this countdown and this "is one running" gate rather than a second pair of its own.

The leaning modifier patches at the bottom are here because the trap is what usually switches
them on. They are not the trap's alone, though: Remove Leaning Modifiers can put them on for
every match or take them off entirely, so they read MetronomeMovement.Active rather than the
countdown itself.
*/

/// <summary>
/// Which way the Metronome trap is holding the player over on the current beat.
/// </summary>
internal enum MetronomeBeatLean
{
    None,
    Left,
    Right,
}

/// <summary>
/// The room's Metronome trap: a countdown in the corner of the screen that beats
/// "tick and tock and" into the kill feed until it runs out, swinging the player left and right
/// in time with it.
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
    /// Which way the trap holds the player over while each <see cref="TickWords"/> entry is the
    /// last one printed - so the lean swings left on "tick", comes back up on the "and", swings
    /// right on "tock", and comes back up again. Same length and same order as TickWords, which
    /// is what lets one index carry both.
    /// </summary>
    private static readonly MetronomeBeatLean[] TickLeans =
    {
        MetronomeBeatLean.Left,
        MetronomeBeatLean.None,
        MetronomeBeatLean.Right,
        MetronomeBeatLean.None,
    };

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
    /// Which way the trap is currently holding the player over. <see cref="MetronomeBeatLean.None"/>
    /// until the first word is printed, which is why a countdown opens upright rather than
    /// already leaning.
    /// </summary>
    private static MetronomeBeatLean beatLean = MetronomeBeatLean.None;

    /// <summary>
    /// <see cref="Time.frameCount"/> of the last frame <see cref="Tick"/> actually spent time on.
    /// </summary>
    private static int lastCountedFrame = -1;

    /// <summary>
    /// What the overlay draws. Zero means no countdown is running and nothing is drawn.
    /// </summary>
    public static float SecondsRemaining => secondsRemaining;

    /// <summary>
    /// Whether the trap is holding the player's lean, and which way.
    /// </summary>
    /// <remarks>
    /// Read together, and only by <see cref="FirstPersonControllerLeanPenaltyPatch"/>: while
    /// <see cref="ForcingLean"/> is true the player's own lean input is thrown away and
    /// <see cref="CurrentBeatLean"/> is what they do instead.
    /// </remarks>
    public static bool ForcingLean => secondsRemaining > 0f;

    /// <inheritdoc cref="ForcingLean"/>
    public static MetronomeBeatLean CurrentBeatLean => beatLean;

    /// <summary>
    /// Whether the countdown is running down THIS frame, which is what the movement patches
    /// below switch on.
    /// </summary>
    /// <remarks>
    /// <para>Not simply <c>SecondsRemaining &gt; 0</c>: a countdown that is held because the
    /// player is dead or between rounds is not counting, and the trap's grip on their movement
    /// should let go for exactly as long as its clock does. So this answers the question
    /// <see cref="Tick"/> already decides every frame, rather than asking it a second way and
    /// risking the two disagreeing.</para>
    /// <para>The previous frame counts too, because Unity does not order Update between two
    /// components: FirstPersonController.Update may well run before PlayerHealth.Update in the
    /// same frame, and a one-frame window is what stops that ordering deciding whether the
    /// patches are on. It also means the effect ends up at most one frame late, which is a
    /// sixtieth of a second nobody can see.</para>
    /// </remarks>
    public static bool IsCountingDown =>
        secondsRemaining > 0f && Time.frameCount - lastCountedFrame <= 1;

    /// <summary>How much held trap is waiting for a Made in Heaven to finish. See <see cref="Receive"/>.</summary>
    private static int pendingSeconds;

    /// <summary>Who sent the first held trap, so the line it eventually starts with names them.</summary>
    private static string pendingSender;

    /// <summary>
    /// Starts a countdown, extends the one already running by the same amount, or holds it back
    /// until a Made in Heaven has finished.
    /// </summary>
    /// <remarks>
    /// <para>Extending rather than restarting is what makes two traps in quick succession worse
    /// than one, instead of the second silently replacing the first. The beat is left where it is
    /// on an extension - the metronome never stopped, so restarting its phase would only make it
    /// stutter.</para>
    /// <para>A Made in Heaven outranks the trap, so one that arrives while a Made in Heaven is
    /// running is not spent against it - it waits, and <see cref="ReleaseHeld"/> starts it when
    /// the buff runs out. Several held traps add up the same way two live ones would, so nothing
    /// is lost by the wait. This is not the same as the trap a Made in Heaven lands ON TOP of,
    /// which is cancelled outright by <see cref="Cancel"/>.</para>
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

        if (MadeInHeavenBuff.Running)
        {
            pendingSeconds += seconds;

            // The first one held is the one that gets named when the queue is released. Later
            // ones only add their seconds - one line cannot credit four different slots, and the
            // first is the one the player was told about first.
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
    /// <see cref="MadeInHeavenBuff"/> as its countdown ends.
    /// </summary>
    public static void ReleaseHeld()
    {
        if (pendingSeconds < 1) return;

        int seconds = pendingSeconds;
        string sender = pendingSender;

        // Cleared before starting, not after: Start writes to the kill feed, and leaving the
        // queue armed across that call is how a re-entrant one would be released twice.
        pendingSeconds = 0;
        pendingSender = null;

        Start(seconds, sender);
    }

    /// <summary>The " from X" on the end of a line, or nothing at all when X is not known.</summary>
    private static string FromClause(string sender) =>
        string.IsNullOrWhiteSpace(sender) ? "" : $" from {sender}";

    /// <summary>
    /// Puts <paramref name="seconds"/> on the clock, starting it if it is not already going.
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

        // The beat starts from silence, so the first word lands one full interval in rather than
        // on top of the start line - and the player starts that interval upright, since nothing
        // has been counted yet to hold them over.
        sinceLastTick = 0f;
        tickIndex = 0;
        beatLean = MetronomeBeatLean.None;

        KillFeed.Write(FeedTag, $"Metronome{from} started - {seconds} seconds of tick tock");
    }

    /// <summary>
    /// Ends the running countdown early, if there is one, and stands the player back up.
    /// </summary>
    /// <remarks>
    /// A Made in Heaven overrules a Metronome, so this exists for
    /// <see cref="MadeInHeavenBuff"/> to call on every client as one activates. It is the same
    /// reset the countdown does when it runs out, minus the "wound down" line - the trap did not
    /// wind down, it was taken away, and the caller says so in its own words.
    /// </remarks>
    /// <returns>Whether there was anything to cancel, so the caller can stay quiet if not.</returns>
    public static bool Cancel()
    {
        if (secondsRemaining <= 0f) return false;

        secondsRemaining = 0f;
        sinceLastTick = 0f;
        tickIndex = 0;

        // Both of these matter beyond tidiness: ForcingLean reads secondsRemaining and the lean
        // patch reads beatLean, so between them this is what lets go of the player's lean.
        beatLean = MetronomeBeatLean.None;
        lastCountedFrame = -1;
        return true;
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

        // The gate is passed, so this frame is one the countdown is genuinely running in. Recorded
        // before the time is spent, so IsCountingDown reads true for the whole of the frame that
        // spends the last of the countdown rather than going false halfway through it.
        lastCountedFrame = Time.frameCount;

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

            // The word and the lean are the same beat, so they are set from the same index in
            // the same step - the player swings over as the word lands, and holds there until
            // the next one. If a long frame covers several beats the last one wins, which is the
            // beat they would be on had every frame been short.
            beatLean = TickLeans[tickIndex];

            // Wrapped here rather than indexed with a modulo, so the counter cannot run away
            // over a long session and is always a legal index on its own.
            tickIndex = (tickIndex + 1) % TickWords.Length;
        }

        if (secondsRemaining > 0f) return;

        secondsRemaining = 0f;
        sinceLastTick = 0f;
        tickIndex = 0;

        // Upright again, and ForcingLean goes false on the same line that zeroes the countdown,
        // so the player has their own lean back from this frame on.
        beatLean = MetronomeBeatLean.None;
        lastCountedFrame = -1;
        KillFeed.Write(FeedTag, "Metronome wound down");
    }
}

/// <summary>
/// The room's Made in Heaven buff: the apworld's other filler buff, alongside
/// <see cref="PlayerHealthBuff"/>, and the one <c>buff_type</c> defaults to.
/// </summary>
/// <remarks>
/// <para>PARTIAL. What works: the announcement on the activating player's screen, the
/// announcement on everybody else's, cancelling any Metronome trap the activation lands on top
/// of, and a countdown that every machine in the lobby runs together and every machine can see.
/// What is NOT here yet is the effect itself - the accelerating metronome that is supposed to
/// beat for everyone except the activating player, its start/end speed config entries, and the
/// interpolation between them. The countdown is the clock that will drive it.</para>
/// <para>The lobby-wide half goes over Mycelium, through <see cref="MadeInHeavenNet"/>. It has
/// to: nobody else in the match is running an Archipelago client, so the only way their game
/// hears about this is the same P2P channel <see cref="RouletteNet"/> uses for the weapon pool.
/// The length travels with the message rather than each machine reading its own
/// <see cref="ArchipelagoMenu.MadeInHeavenSeconds"/>, so the lobby counts one countdown rather
/// than several of different lengths.</para>
/// <para>Two messages, not one. The activation starts it everywhere; then at the end of every
/// round the host broadcasts what is left on ITS clock and every client adopts that
/// (<see cref="AdoptRemaining"/>). Each machine spends its own copy off its own Time.deltaTime
/// and holds it on its own frames, so without that they drift apart over a long countdown - and
/// a client that missed the activation or joined afterwards would never have started one at
/// all. The host is the arbiter for the same reason it is in the roulette: somebody has to be,
/// and it is the one peer everybody already agrees on.</para>
/// <para>Overriding rather than extending, which is the opposite of what
/// <see cref="MetronomeTrap.Receive"/> does with a second trap: a second Made in Heaven throws
/// the first one away and starts again at full length, whoever sent it.</para>
/// <para>The countdown holds between rounds - <see cref="Tick"/> is gated on
/// <c>PauseManager.BetweenRounds</c>, which vanilla raises in InvokeBeforeSpawn and lowers in
/// InvokeRoundStarted - and it is driven from ArchipelagoOverlay.Update rather than from
/// PlayerHealth.Update the way the trap's is. That is deliberate: this clock belongs to the
/// lobby, not to the local player, so it must keep running while they are dead, spectating or
/// waiting to respawn, and PlayerHealth.Update stops in all three.</para>
/// <para>Main-thread only, like the trap: <c>ArchipelagoClient.ApplyReceivedItem</c> queues
/// <see cref="Receive"/> through <see cref="MainThreadActions"/> rather than calling it on the
/// websocket thread, and the RPC arrives on Mycelium's own main-thread pump.</para>
/// </remarks>
internal static class MadeInHeavenBuff
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
    /// Last frame's <c>PauseManager.BetweenRounds</c>, so <see cref="Tick"/> can see the moment a
    /// round ends rather than only that one has. That edge is the resync point.
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
    /// Takes delivery of one Made in Heaven from the room. Runs only on the machine the
    /// multiworld gave it to.
    /// </summary>
    /// <param name="sender">The slot that sent it, for the line the player is shown. Blank or
    /// null drops the clause rather than naming nobody, the same as
    /// <see cref="MetronomeTrap.Receive"/>.</param>
    public static void Receive(string sender)
    {
        int seconds = ArchipelagoMenu.MadeInHeavenSeconds.Value;

        // Guarded rather than trusted even though the config's AcceptableValueRange starts at 1,
        // for the same reason MetronomeTrap.Receive guards its own: a zero would announce a
        // countdown to the whole lobby that ends on the frame it starts.
        if (seconds < 1)
        {
            Plugin.BepinLogger.LogWarning(
                $"[{FeedTag}] a Made in Heaven arrived but the configured length is {seconds} " +
                "seconds; ignoring it.");
            return;
        }

        string from = string.IsNullOrWhiteSpace(sender) ? "" : $" from {sender}";
        Plugin.BepinLogger.LogInfo($"[{FeedTag}] activating a Made in Heaven{from} for {seconds}s");

        // The activating player's own line, and their own copy of the countdown, applied here
        // rather than waiting for the broadcast to come back around. Mycelium may or may not
        // deliver a broadcast to the sender, and MadeInHeavenNet ignores our own message either
        // way, so this is the one and only place the local half happens.
        KillFeed.Write(FeedTag, ActivationCry);
        Begin(seconds);

        MadeInHeavenNet.Announce(KillFeed.LocalPlayerName, seconds);
    }

    /// <summary>
    /// Starts the countdown on THIS machine, having been told about an activation somewhere in
    /// the lobby. Called from <see cref="MadeInHeavenNet"/> for a remote one.
    /// </summary>
    /// <param name="playerName">Whoever set it off, as the lobby knows them.</param>
    /// <param name="seconds">The length they activated it for, so every machine counts the same.</param>
    public static void ReceiveRemote(string playerName, int seconds)
    {
        if (seconds < 1)
        {
            Plugin.BepinLogger.LogWarning(
                $"[{FeedTag}] a Made in Heaven arrived over the network with a length of " +
                $"{seconds} seconds; ignoring it.");
            return;
        }

        string who = string.IsNullOrWhiteSpace(playerName) ? UnknownActivator : playerName;

        KillFeed.Write(FeedTag, string.Format(RemoteActivationFormat, who));
        Begin(seconds);
    }

    /// <summary>
    /// Takes the host's word for how much is left. Called from <see cref="MadeInHeavenNet"/> on
    /// every client as a round ends.
    /// </summary>
    /// <remarks>
    /// <para>Two jobs in one message. The usual one is trimming drift off a countdown this
    /// machine is already running, which is silent - a correction of a fraction of a second is
    /// not news, and announcing one every round would be.</para>
    /// <para>The other is repair: if this machine has no countdown at all it missed the
    /// activation, or joined the lobby after it, so this is the first it has heard of it and the
    /// rest of its state needs bringing into line the way <see cref="Begin"/> would have. That
    /// case is worth a line, because a timer appearing in the corner with no explanation is
    /// worse than an odd one.</para>
    /// </remarks>
    public static void AdoptRemaining(float seconds)
    {
        // The host only sends this while its own is running, so a zero here would be a message
        // that outlived what it describes. Nothing to sync to.
        if (seconds <= 0f) return;

        bool wasRunning = Running;
        float before = secondsRemaining;
        secondsRemaining = seconds;

        if (wasRunning)
        {
            Plugin.BepinLogger.LogDebug(
                $"[{FeedTag}] resynced to the host: {before:0.00}s -> {seconds:0.00}s");
            return;
        }

        Plugin.BepinLogger.LogInfo(
            $"[{FeedTag}] the host reports a Made in Heaven with {seconds:0.00}s left that this " +
            "machine knew nothing about; adopting it");

        if (MetronomeTrap.Cancel())
        {
            KillFeed.Write(FeedTag, "The metronome stops.");
        }

        KillFeed.Write(FeedTag, "Made in Heaven is already under way.");
    }

    /// <summary>
    /// The half every machine does the same way, wherever the activation came from: throw out
    /// any Metronome trap it lands on, and start the countdown over.
    /// </summary>
    private static void Begin(int seconds)
    {
        // A Made in Heaven outranks a Metronome, so the trap goes - including the lean it was
        // holding the player in, which Cancel releases. Reported only when there was actually
        // one running, so a quiet activation stays quiet.
        if (MetronomeTrap.Cancel())
        {
            KillFeed.Write(FeedTag, "The metronome stops.");
        }

        // Assignment, not addition: a second Made in Heaven replaces the first outright rather
        // than extending it the way a second Metronome extends its trap.
        secondsRemaining = seconds;
    }

    /// <summary>
    /// Spends this frame of the countdown. Called every frame from ArchipelagoOverlay.Update,
    /// which is this mod's one guaranteed main-thread callback and, unlike PlayerHealth.Update,
    /// keeps running while the local player is dead or between spawns.
    /// </summary>
    public static void Tick()
    {
        // The one pause this countdown takes. A public STATIC field, unlike almost everything
        // else on PauseManager: vanilla raises it in InvokeBeforeSpawn and lowers it in
        // InvokeRoundStarted, so it is true for exactly the gap between rounds.
        //
        // Read and latched ahead of every early-out below, because it is the EDGE that matters
        // for the resync: a tracker only updated on the frames that get past those returns would
        // miss the very transition it exists to catch.
        bool betweenRounds = PauseManager.BetweenRounds;
        bool roundJustEnded = betweenRounds && !wasBetweenRounds;
        wasBetweenRounds = betweenRounds;

        // Cheap early-out for the overwhelmingly common case: this runs every frame of the
        // session and almost none of them have a Made in Heaven running. It is also the "if the
        // host still sees it has one ongoing" half of the resync below - with nothing running
        // there is nothing to tell anyone about.
        if (secondsRemaining <= 0f) return;

        // Not in a match at all - the menu, or a scene with no PauseManager up yet. Held rather
        // than spent, the same way it is held between rounds.
        if (PauseManager.Instance == null) return;

        // The host's clock is the lobby's clock. Every machine counts its own copy down off its
        // own Time.deltaTime and holds it on its own frames, so they drift apart over a long
        // countdown - and a client that missed the activation, or joined after it, has no copy at
        // all. The gap between rounds is the right moment to put that right: nothing is being
        // spent while it is open, so a correction cannot land in the middle of anything.
        if (roundJustEnded && MyceliumNetwork.IsHost)
        {
            MadeInHeavenNet.Resync(secondsRemaining);
        }

        if (betweenRounds) return;

        secondsRemaining -= Time.deltaTime;

        if (secondsRemaining > 0f) return;

        secondsRemaining = 0f;
        KillFeed.Write(FeedTag, "Made in Heaven has run its course.");

        // Zeroed BEFORE this call, because ReleaseHeld starts a trap that reads Running to
        // decide whether to hold itself back again - and it would, forever, if this still
        // said a Made in Heaven was going.
        MetronomeTrap.ReleaseHeld();
    }
}

/// <summary>
/// Tells the rest of the Straftat lobby that a Made in Heaven has gone off.
/// </summary>
/// <remarks>
/// <para>Separate from <see cref="RouletteNet"/> but on the same Mycelium mod id, which is the
/// mod's and not the roulette's - Mycelium routes on (mod id, method name), so two registered
/// objects under one id coexist as long as their method names differ.</para>
/// <para>One message, one direction, no reply: every machine that hears it does exactly the same
/// thing to its own copy of the game, and there is nothing for them to answer. The length rides
/// along so the lobby counts one countdown rather than each machine counting its own
/// <see cref="ArchipelagoMenu.MadeInHeavenSeconds"/>.</para>
/// <para>Unreliable would be wrong here: a dropped activation would leave one player's game out
/// of step with everyone else's for the length of the countdown, with no later message to put it
/// right.</para>
/// </remarks>
internal class MadeInHeavenNet
{
    private static MadeInHeavenNet instance;

    public static void Install()
    {
        if (instance != null) return;

        instance = new MadeInHeavenNet();
        MyceliumNetwork.RegisterNetworkObject(instance, RouletteNet.ModId);
        Plugin.BepinLogger.LogInfo(
            $"[MadeInHeaven] registered CustomRPCs under mod id {RouletteNet.ModId}");
    }

    /// <summary>
    /// Broadcasts an activation to the lobby. Called on the machine the multiworld gave the buff
    /// to, straight after it has applied its own half.
    /// </summary>
    public static void Announce(string playerName, int seconds)
    {
        if (instance == null)
        {
            Plugin.BepinLogger.LogError(
                "[MadeInHeaven] MadeInHeavenNet.Install() never ran; the lobby will not hear about this");
            return;
        }

        if (!MyceliumNetwork.InLobby)
        {
            // Offline or solo. The local half has already happened, so this is a note rather
            // than a failure - there is simply nobody to tell.
            Plugin.BepinLogger.LogInfo(
                "[MadeInHeaven] not in a Steam lobby, so the activation stays on this machine");
            return;
        }

        MyceliumNetwork.RPC(RouletteNet.ModId, nameof(ClientMadeInHeavenActivated),
            ReliableType.Reliable, playerName, seconds);
    }

    /// <summary>
    /// Runs on every machine in the lobby. The sender's own copy is dropped, because
    /// <see cref="MadeInHeavenBuff.Receive"/> has already done its half with the line that
    /// belongs to whoever set it off.
    /// </summary>
    [CustomRPC]
    public void ClientMadeInHeavenActivated(string playerName, int seconds, RPCInfo info)
    {
        try
        {
            // A Mycelium broadcast DOES come back to its sender - RPCMasked walks
            // MyceliumNetwork.Players and sends to each, and that array includes the local
            // player. So this check is load-bearing, not defensive: without it the activating
            // player would be told they have activated the ultimate stand, on top of having
            // already said so themselves in Receive.
            if (info.SenderSteamID == SteamUser.GetSteamID()) return;

            Plugin.BepinLogger.LogInfo(
                $"[MadeInHeaven] {playerName} ({info.SenderSteamID}) activated one for {seconds}s");

            MadeInHeavenBuff.ReceiveRemote(playerName, seconds);
        }
        catch (Exception error)
        {
            // Swallowed rather than thrown back into Mycelium's message pump, which is draining
            // every other mod's RPCs in the same loop.
            Plugin.BepinLogger.LogError(
                $"[MadeInHeaven] failed to apply a received activation{Environment.NewLine}{error}");
        }
    }

    /// <summary>
    /// Broadcasts how much of a running Made in Heaven is left. Sent by the host only, as a
    /// round ends - see <see cref="MadeInHeavenBuff.Tick"/> for why that moment.
    /// </summary>
    /// <remarks>
    /// A float, sent as one: Mycelium serializes System.Single natively, so there is no reason to
    /// round the remainder to whole seconds and re-introduce error while correcting for it.
    /// </remarks>
    public static void Resync(float secondsRemaining)
    {
        if (instance == null || !MyceliumNetwork.InLobby) return;

        MyceliumNetwork.RPC(RouletteNet.ModId, nameof(ClientMadeInHeavenResync),
            ReliableType.Reliable, secondsRemaining);
    }

    /// <summary>
    /// Runs on every machine in the lobby. Takes the host's remaining time as the truth.
    /// </summary>
    [CustomRPC]
    public void ClientMadeInHeavenResync(float secondsRemaining, RPCInfo info)
    {
        try
        {
            // The host's own copy of its own broadcast. It is the source of truth, so there is
            // nothing here for it to adopt.
            if (info.SenderSteamID == SteamUser.GetSteamID()) return;

            // Only the host may say what the time is. Mycelium delivers to whoever is listening,
            // so without this a client whose own clock had drifted could hand its drift to the
            // whole lobby - and two clients disagreeing would then fight over it every round.
            if (info.SenderSteamID != MyceliumNetwork.LobbyHost)
            {
                Plugin.BepinLogger.LogWarning(
                    $"[MadeInHeaven] ignoring a resync from {info.SenderSteamID}, who is not the " +
                    $"lobby host ({MyceliumNetwork.LobbyHost})");
                return;
            }

            MadeInHeavenBuff.AdoptRemaining(secondsRemaining);
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError(
                $"[MadeInHeaven] failed to apply a resync{Environment.NewLine}{error}");
        }
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

/*
=====================================================================================
The leaning modifier patches
=====================================================================================

Two patches on FirstPersonController, carrying both halves of what a Metronome does to the
player's body:

- Leaning costs nothing. They move exactly as though they were standing straight, and make
  exactly as much noise doing it. What leaning still does is look like a lean and see round the
  corner - and it now also works airborne and mid-slide.

- While a Metronome is counting, the beat leans FOR them. The lean swings left as "tick" lands
  and holds there, comes up on the "and", swings right on "tock", comes up again on the next
  "and", and repeats for as long as the countdown does. Their own lean keys do nothing until it
  winds down. The two halves are gated separately - see the notes on
  FirstPersonControllerLeanPenaltyPatch - because the dropdown decides what leaning COSTS and the
  trap decides whether they are LEANING.

Neither patch reimplements any of vanilla's movement maths. They work by lying to vanilla about
one or two booleans for the length of a single call, so the game's own code computes the
unpenalised answer and everything downstream of it stays consistent by construction.

Vanilla hangs every leaning penalty off ONE field, `isLeaning`, which it recomputes at the very
end of each Update as `isLeaningLeft || isLeaningRight`. Every read of it inside that same Update
is a penalty, and all four are meant to go:

    @818   CalculateMovementInput()   every speedFactor branch is guarded `&& !isLeaning`, so a
                                      lean drops through them all to the global deceleration -
                                      that is the speed loss
    @892   HandleFootsteps()          returns early on `isLeaning || isCrouching` - that is the
                                      free silence, and a player moving at full sprint speed has
                                      not earned it
    @1655  isSprinting      = !isLeaning && !isAiming && moving && !isCrouching && funcSprint
    @1717  isSlideSprinting = !isLeaning && !isAiming && moving && funcSprint
    @2084  isLeaning = isLeaningLeft || isLeaningRight

So clearing `isLeaning` for the length of Update is the whole of the first patch, and vanilla
rebuilds the field from the two directional flags before the method returns. Those four reads are
exhaustive: a search of the whole assembly finds no other reader of `isLeaning` inside Update's
call tree, and the only readers anywhere else are FPArms.Update (the viewmodel), which runs
outside the window entirely.

Footstep timing comes out right on its own, because it is driven by playerSpeed and
movementFactor - which CalculateMovementInput has already computed without the penalty by the
time HandleFootsteps runs - so the steps land at the cadence of the speed actually being moved at.

The camera still leans. That runs off isLeaningLeft/isLeaningRight, which are never touched, and
which are also what HandleAnimation, HandleCameraLean and CameraLean read - CameraLean only ever
writes leanCamera.localRotation, so leaning has no effect on the player's position or collider in
the first place.

WHERE you may lean is the one thing not on `isLeaning`: HandleCameraLean gates its two lean cases
on `!isSliding && isGrounded`, so the second patch clears those for the length of that one call
and puts them back.

WHEN all this is on is ArchipelagoMenu.RemoveLeaningModifiers, read through
MetronomeMovement.Active: always, never, or - the default - only while a Metronome countdown is
running, which is what makes that trap change how the game plays rather than only how it looks.

Both are confined to the local player with IsOwner: the state they touch is this machine's
player's, and neither should ever reach another player's copy.
*/

/// <summary>
/// When the two leaning patches below may act, and where they report a failure.
/// </summary>
internal static class MetronomeMovement
{
    private static readonly HashSet<string> Reported = new();

    /// <summary>
    /// Whether the leaning modifiers are off right now.
    /// </summary>
    /// <remarks>
    /// The metronome answer is <see cref="MetronomeTrap.IsCountingDown"/> rather than "a
    /// countdown exists", so the effect is on for exactly as long as the clock is moving - a
    /// countdown held because the player is dead or between rounds hands the game back to
    /// vanilla until they are playing again.
    /// </remarks>
    public static bool Active
    {
        get
        {
            switch (ArchipelagoMenu.RemoveLeaningModifiers.Value)
            {
                case LeaningModifierRemoval.Always:
                    return true;

                case LeaningModifierRemoval.Never:
                    return false;

                // OnlyWhileInMetronomeMode, and the fallback for anything else the entry could
                // be holding - BepInEx parses the enum out of the .cfg and falls back to the
                // default itself, so a fourth value never reaches this.
                default:
                    return MetronomeTrap.IsCountingDown;
            }
        }
    }

    /// <summary>
    /// Reports a patch failure once per patch, and never again for the rest of the session.
    /// </summary>
    /// <remarks>
    /// Once, not once per frame: these run inside FirstPersonController's per-frame methods, so
    /// a fault that repeats - a field renamed by a game update, most likely - would otherwise
    /// write sixty lines a second into LogOutput.log and bury everything else in there.
    /// </remarks>
    public static void ReportOnce(string patchName, Exception error)
    {
        if (!Reported.Add(patchName)) return;

        Plugin.BepinLogger.LogError(
            $"[Metronome] {patchName} failed and will report nothing further this session. " +
            "Some or all of the leaning modifier removal is missing." +
            $"{Environment.NewLine}{error}");
    }
}

/// <summary>
/// Takes every penalty off leaning - speed, sprint and the free silence - by hiding the lean
/// from vanilla's Update for the length of that call, and, while a Metronome is running, decides
/// which way the player leans instead of letting them.
/// </summary>
/// <remarks>
/// <para>Both jobs live in one patch class because they are two halves of one write. Vanilla
/// works out isLeaningLeft/isLeaningRight from the player's input at IL_073C-IL_0814 and then
/// derives isLeaning from the pair at IL_0824, all at the very end of Update. The postfix
/// overwrites that pair with the beat and then redoes the derivation, in that order. Split across
/// two postfixes it would be a coin toss: Harmony does not order postfixes on the same method
/// against each other, so the derivation could run against the input lean rather than the
/// beat.</para>
/// <para>Overwriting the pair is also the whole of "the player cannot lean by hand during the
/// trap". Vanilla's lean input has already been read and turned into those two booleans by the
/// time this runs, so replacing them discards it; nothing else carries it forward, and the toggle
/// latches behind it only ever feed the same two booleans.</para>
/// <para>The forced lean is gated on <see cref="MetronomeTrap.ForcingLean"/>, NOT on
/// <see cref="MetronomeMovement.Active"/>: the dropdown decides whether leaning costs the player
/// anything, and the trap decides whether they are leaning. They are independent, so a player
/// who set the dropdown to Never gets swung about AND pays vanilla's full price for it, which is
/// the harsher trap and the honest reading of the setting.</para>
/// <para>ForcingLean rather than <see cref="MetronomeTrap.IsCountingDown"/>, which is what the
/// movement half reads: a countdown held because the player is dead or between rounds has its
/// beat frozen too, and letting them straighten up in the meantime would put the lean out of step
/// with the word last printed.</para>
/// <para>The postfix restores isLeaning the same way vanilla's own last statement does, rather
/// than putting back what the prefix saved. That is not belt and braces: vanilla's Update has an
/// early return at IL_0816, taken when the player cannot move - frozen between rounds, stunned,
/// dead - and it sits BEFORE the statement that rebuilds isLeaning. Without a postfix of our own
/// a single frozen frame would leave the field false until the next frame that ran to the end,
/// and the arms would be drawn out of a lean while the player held one.</para>
/// <para>The latch, rather than testing <see cref="MetronomeMovement.Active"/> a second time:
/// the postfix must undo exactly what the prefix did, and re-asking a question whose answer can
/// change - the countdown ending, the dropdown being moved - is how a field gets left
/// clobbered.</para>
/// <para>A plain static latch is safe here: only the local player's controller passes the gate,
/// and Update is not reentrant.</para>
/// </remarks>
[HarmonyPatch(typeof(FirstPersonController), "Update")]
public class FirstPersonControllerLeanPenaltyPatch
{
    private static bool suppressed;

    static void Prefix(FirstPersonController __instance, ref bool ___isLeaning)
    {
        suppressed = false;

        try
        {
            if (!MetronomeMovement.Active || !__instance.IsOwner) return;

            // Everything follows from this one line. See the block comment above for the four
            // places vanilla reads it before the end of this Update.
            ___isLeaning = false;
            suppressed = true;
        }
        catch (Exception error)
        {
            // Swallowed like every other patch in this mod: a movement effect that cannot be
            // applied must not abandon the rest of the player's Update.
            MetronomeMovement.ReportOnce(nameof(FirstPersonControllerLeanPenaltyPatch), error);
        }
    }

    static void Postfix(FirstPersonController __instance, ref bool ___isLeaning,
        ref bool ___isLeaningLeft, ref bool ___isLeaningRight)
    {
        bool wasSuppressed = suppressed;
        suppressed = false;

        try
        {
            // Asked once and reused, so the beat cannot change between the two writes and leave
            // the player leaning both ways or neither.
            bool forcing = MetronomeTrap.ForcingLean && __instance != null && __instance.IsOwner;

            if (forcing)
            {
                MetronomeBeatLean beat = MetronomeTrap.CurrentBeatLean;

                // Written unconditionally, both of them: on a None beat this is what stands the
                // player back up, and on either of the others it is what throws away whatever
                // their own lean keys asked for this frame.
                ___isLeaningLeft = beat == MetronomeBeatLean.Left;
                ___isLeaningRight = beat == MetronomeBeatLean.Right;
            }

            // Nothing was touched, so vanilla's own IL_0824 already stands.
            if (!forcing && !wasSuppressed) return;

            // Vanilla's own IL_0824, repeated - and it has to be repeated after a forced lean,
            // because vanilla derived it from the input lean this just replaced. On a plain
            // suppressed frame it writes back the value vanilla wrote; on one that took the
            // early return it is the value vanilla would have written.
            ___isLeaning = ___isLeaningLeft || ___isLeaningRight;
        }
        catch (Exception error)
        {
            MetronomeMovement.ReportOnce(nameof(FirstPersonControllerLeanPenaltyPatch), error);
        }
    }
}

/// <summary>
/// Lets the player lean airborne and while sliding, by hiding both of those states from
/// vanilla's HandleCameraLean for the length of that call.
/// </summary>
/// <remarks>
/// Vanilla leans only while `!isSliding &amp;&amp; isGrounded`; with those two saying "on the
/// ground, not sliding" it takes its normal lean path, and the values go back before anything
/// else can read them. HandleCameraLean neither writes them nor calls anything that reads them -
/// its one call out is CameraLean, which reads only isLeaningLeft/isLeaningRight and writes only
/// leanCamera.localRotation - so the window is exactly this method and the swap cannot be
/// observed from outside it.
/// </remarks>
[HarmonyPatch(typeof(FirstPersonController), "HandleCameraLean")]
public class FirstPersonControllerLeanAnywherePatch
{
    private static bool swapped;
    private static bool wasSliding;
    private static bool wasGrounded;

    static void Prefix(FirstPersonController __instance, ref bool ___isSliding, ref bool ___isGrounded)
    {
        swapped = false;

        try
        {
            if (!MetronomeMovement.Active || !__instance.IsOwner) return;

            wasSliding = ___isSliding;
            wasGrounded = ___isGrounded;

            ___isSliding = false;
            ___isGrounded = true;
            swapped = true;
        }
        catch (Exception error)
        {
            MetronomeMovement.ReportOnce(nameof(FirstPersonControllerLeanAnywherePatch), error);
        }
    }

    static void Postfix(ref bool ___isSliding, ref bool ___isGrounded)
    {
        if (!swapped) return;
        swapped = false;

        try
        {
            ___isSliding = wasSliding;
            ___isGrounded = wasGrounded;
        }
        catch (Exception error)
        {
            // A failure here leaves the player permanently grounded and never sliding as far as
            // the rest of the frame is concerned, so this one is worth the log line on its own.
            MetronomeMovement.ReportOnce(nameof(FirstPersonControllerLeanAnywherePatch), error);
        }
    }
}
