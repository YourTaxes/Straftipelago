using System;
using System.Collections.Generic;
using HarmonyLib;
using MyceliumNetworking;
using Steamworks;
using Straftapelago.Finnegan_McD.org.Utils;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

/*
Everything metronome-shaped lives in this one file: the rhythm itself, the trap the room sends,
the Made in Heaven buff - which is the same rhythm aimed at the whole lobby except the player who
activated it, accelerating as it goes - and the leaning modifier patches the two of them switch on.

MetronomeBeat is the rhythm and the only beat loop here. The trap drives it at a fixed interval
and Made in Heaven at one that shortens as its countdown runs; neither owns the phase, so the two
cannot drift apart, and there is one implementation of the swing rather than two. They never
overlap - a Made in Heaven cancels a trap already running, and a trap that arrives during one is
queued until it ends - so a single shared phase is correct and not merely convenient.

The beat is a lean and nothing else. It used to print "tick"/"and"/"tock"/"and" to the kill feed;
those words survive only as the names of the four phases, because at the speeds Made in Heaven
reaches near its end the feed would be unreadable.

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
/// The metronome's rhythm: the thing that actually swings the player left and right, and the one
/// beat loop in the mod.
/// </summary>
/// <remarks>
/// <para>Two things drive this and they differ only in the interval they pass to
/// <see cref="Advance"/>: <see cref="MetronomeTrap"/> passes the fixed
/// <see cref="ArchipelagoMenu.MetronomeTickSeconds"/>, and <see cref="MadeInHeaven"/> passes an
/// interval that shortens as its countdown runs. Neither owns the phase, so neither can drift
/// from the other's idea of the rhythm, and there is only one place the swing is implemented.</para>
/// <para>They never overlap, which is what makes a single shared phase correct rather than
/// merely convenient: a Made in Heaven cancels any trap already running, and a trap that arrives
/// during one is queued until it ends. See <see cref="MetronomeTrap.Receive"/>.</para>
/// <para>The beat is a LEAN and nothing else. It used to print "tick", "and", "tock", "and" to
/// the kill feed as it went; those words survive only as the names of the four phases below,
/// because at the speeds a Made in Heaven reaches near its end the feed would be unreadable.</para>
/// </remarks>
internal static class MetronomeBeat
{
    /// <summary>
    /// The four phases, in order, repeating: "tick" swings left, "and" comes back up, "tock"
    /// swings right, "and" comes back up.
    /// </summary>
    private static readonly MetronomeBeatLean[] BeatLeans =
    {
        MetronomeBeatLean.Left,
        MetronomeBeatLean.None,
        MetronomeBeatLean.Right,
        MetronomeBeatLean.None,
    };

    /// <summary>
    /// Floor on the interval a driver may ask for, so a hand-edited .cfg cannot make the catch-up
    /// loop in <see cref="Advance"/> run away. Every config entry that feeds this has an
    /// AcceptableValueRange that already refuses anything smaller; this is the second belt.
    /// </summary>
    public const float MinimumInterval = 0.05f;

    /// <summary>Which phase the next beat moves to.</summary>
    private static int beatIndex;

    /// <summary>Time spent since the last beat, in the driving countdown's own time.</summary>
    private static float sinceLastBeat;

    private static MetronomeBeatLean currentLean = MetronomeBeatLean.None;

    /// <summary><see cref="Time.frameCount"/> of the last frame <see cref="Advance"/> ran on.</summary>
    private static int lastBeatFrame = -1;

    /// <summary>
    /// Whether a driver currently owns the player's lean, and which way it is holding them.
    /// </summary>
    /// <remarks>
    /// Read together, and only by <see cref="FirstPersonControllerLeanPenaltyPatch"/>: while
    /// <see cref="Running"/> is true the player's own lean input is thrown away and
    /// <see cref="CurrentLean"/> is what they do instead. Note this stays true while a countdown
    /// is merely HELD - dead, between rounds - so the player keeps the pose the last beat put them
    /// in rather than straightening up out of step with the rhythm.
    /// </remarks>
    public static bool Running { get; private set; }

    /// <inheritdoc cref="Running"/>
    public static MetronomeBeatLean CurrentLean => currentLean;

    /// <summary>
    /// Whether the beat is actually moving THIS frame, which is what
    /// <see cref="MetronomeMovement.Active"/> switches the leaning modifiers on.
    /// </summary>
    /// <remarks>
    /// Not the same question as <see cref="Running"/>: a countdown held because the player is dead
    /// or between rounds is still holding their pose but is not spending time, and the modifiers
    /// should let go for exactly as long as its clock does.
    ///
    /// The previous frame counts too, because Unity does not order Update between components -
    /// FirstPersonController.Update may run before PlayerHealth.Update (the trap's driver) or
    /// before ArchipelagoOverlay.Update (Made in Heaven's), and a one-frame window is what stops
    /// that ordering deciding whether the modifiers are on.
    /// </remarks>
    public static bool IsBeating => Running && Time.frameCount - lastBeatFrame <= 1;

    /// <summary>Takes the lean, from upright and at the top of the rhythm.</summary>
    public static void Start()
    {
        Running = true;
        beatIndex = 0;
        sinceLastBeat = 0f;

        // Upright to begin with: nothing has been beaten yet, so the first swing lands one full
        // interval in rather than the moment the countdown starts.
        currentLean = MetronomeBeatLean.None;
        lastBeatFrame = -1;
    }

    /// <summary>Gives the lean back, standing the player up.</summary>
    public static void Stop()
    {
        Running = false;
        beatIndex = 0;
        sinceLastBeat = 0f;
        currentLean = MetronomeBeatLean.None;
        lastBeatFrame = -1;
    }

    /// <summary>
    /// Spends <paramref name="delta"/> of a driving countdown against the rhythm, swinging the
    /// player as each beat lands.
    /// </summary>
    /// <param name="delta">Time the driver actually spent this frame - not Time.deltaTime, which
    /// would keep the beat moving on frames the countdown itself was held.</param>
    /// <param name="intervalSeconds">How long this beat lasts. Re-read every call rather than
    /// stored, which is what lets Made in Heaven shorten it as it goes.</param>
    public static void Advance(float delta, float intervalSeconds)
    {
        if (!Running) return;

        sinceLastBeat += delta;
        lastBeatFrame = Time.frameCount;

        float interval = Mathf.Max(MinimumInterval, intervalSeconds);

        // A loop, not a single test: one long frame - a hitch, or the scene load at the start of a
        // round - can cover more than one beat, and the rhythm should not lose its place over it.
        // Subtracting the interval rather than zeroing keeps the beat on its own clock instead of
        // drifting a little later every frame. If several beats land in one frame the last one
        // wins, which is the phase the player would be in had every frame been short.
        while (sinceLastBeat >= interval)
        {
            sinceLastBeat -= interval;
            currentLean = BeatLeans[beatIndex];

            // Wrapped here rather than indexed with a modulo, so the counter cannot run away over
            // a long session and is always a legal index on its own.
            beatIndex = (beatIndex + 1) % BeatLeans.Length;
        }
    }
}

/// <summary>
/// The room's Metronome trap: a countdown in the corner of the screen that swings the player
/// left and right on <see cref="MetronomeBeat"/>'s rhythm until it runs out.
/// </summary>
/// <remarks>
/// <para>How long a Metronome runs and how fast it beats are both config entries -
/// <see cref="ArchipelagoMenu.MetronomeTrapSeconds"/> and
/// <see cref="ArchipelagoMenu.MetronomeTickSeconds"/> - rather than numbers the apworld sends,
/// because neither is part of the two repos' slot-data contract. Both are hidden from the Mod
/// Menu page for the same reason the Green Mode tint is: they are tuning knobs, not settings a
/// player is meant to reach for mid-match.</para>
/// <para>The beat is a LEAN, not a line in the kill feed: the trap takes the player's leaning off
/// them and swings it left, upright, right, upright in time with itself, and their own lean keys
/// do nothing until it winds down. Only the start, extension and wound-down lines are written.
/// See <see cref="MetronomeBeat"/>, which is the rhythm, and
/// <see cref="FirstPersonControllerLeanPenaltyPatch"/>, which applies it.</para>
/// <para>Everything here runs on the main thread and nothing locks: the countdown is spent in
/// <see cref="Tick"/> out of PlayerHealth.Update, read in ArchipelagoOverlay's OnGUI, and added
/// to from a <see cref="MainThreadActions"/> action - the received item itself arrives on the
/// Archipelago client's websocket thread and is queued there, the same way every other item is.
/// </para>
/// </remarks>
internal static class MetronomeTrap
{
    /// <summary>The tag every line this trap writes is filed under in LogOutput.log.</summary>
    private const string FeedTag = "Metronome";

    private static float secondsRemaining;

    /// <summary>
    /// What the overlay draws. Zero means no countdown is running and nothing is drawn.
    /// </summary>
    public static float SecondsRemaining => secondsRemaining;

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

        if (MadeInHeaven.Running)
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
    /// <see cref="MadeInHeaven"/> as its countdown ends.
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
        MetronomeBeat.Start();

        KillFeed.Write(FeedTag, $"Metronome{from} started - {seconds} seconds of tick tock");
    }

    /// <summary>
    /// Ends the running countdown early, if there is one, and stands the player back up.
    /// </summary>
    /// <remarks>
    /// A Made in Heaven overrules a Metronome, so this exists for
    /// <see cref="MadeInHeaven"/> to call on every client as one activates. It is the same
    /// reset the countdown does when it runs out, minus the "wound down" line - the trap did not
    /// wind down, it was taken away, and the caller says so in its own words.
    /// </remarks>
    /// <returns>Whether there was anything to cancel, so the caller can stay quiet if not.</returns>
    public static bool Cancel()
    {
        if (secondsRemaining <= 0f) return false;

        secondsRemaining = 0f;

        // Not merely tidiness: this is what lets go of the player's lean, so a Made in Heaven that
        // cancels a trap and then starts its own beat gets a clean phase rather than the trap's.
        MetronomeBeat.Stop();
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

        float delta = Time.deltaTime;
        secondsRemaining -= delta;

        // The same delta the countdown just spent, not Time.deltaTime read again: on a frame the
        // gate above had refused, the countdown would not have moved and neither should the beat.
        // A fixed interval, unlike Made in Heaven's, which shortens as it goes.
        MetronomeBeat.Advance(delta, ArchipelagoMenu.MetronomeTickSeconds.Value);

        if (secondsRemaining > 0f) return;

        secondsRemaining = 0f;

        // Upright again, and Running goes false with it, so the player has their own lean back
        // from this frame on.
        MetronomeBeat.Stop();
        KillFeed.Write(FeedTag, "Metronome wound down");
    }
}

/// <summary>
/// The room's Made in Heaven buff: the apworld's other filler buff, alongside
/// <see cref="PlayerHealthBuff"/>, and the one <c>buff_type</c> defaults to.
/// </summary>
/// <remarks>
/// <para>Time speeding up, which is where the name comes from. It starts a metronome on every
/// machine in the lobby EXCEPT the activating player's, slowly at first and accelerating from
/// there, and the countdown every machine can see in the corner is the clock that drives it: the
/// beat interval is interpolated from <see cref="ArchipelagoMenu.MadeInHeavenStartTickSeconds"/>
/// to <see cref="ArchipelagoMenu.MadeInHeavenEndTickSeconds"/> across how far through the
/// countdown it is. See <see cref="CurrentInterval"/>.</para>
/// <para>The player it spares is the one the multiworld gave it to, which is what makes it a buff
/// rather than a trap: everyone they are fighting is thrown left and right harder and harder while
/// they are left alone. The beat itself is <see cref="MetronomeBeat"/>'s, the same rhythm the
/// Metronome trap swings on, driven at a different interval.</para>
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
    /// How long this Made in Heaven was started for. The denominator of the ramp - see
    /// <see cref="CurrentInterval"/> - so it has to survive for the whole countdown.
    /// </summary>
    private static float totalSeconds;

    /// <summary>The interval the beat opens on, and the one it accelerates toward.</summary>
    private static float startTickSeconds;

    /// <inheritdoc cref="startTickSeconds"/>
    private static float endTickSeconds;

    /// <summary>
    /// Whether the multiworld handed this one to the player sitting at THIS machine.
    /// </summary>
    /// <remarks>
    /// The whole of "everyone except the activating player". It is the buff's owner who is spared
    /// the metronome - they get the announcement and the countdown to watch, and nothing else -
    /// while every other machine in the lobby beats. Set true only in <see cref="Receive"/>, which
    /// is the one path the Archipelago client can reach.
    /// </remarks>
    private static bool isActivator;

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
    /// How long the current beat should be held, given how far through the countdown it is.
    /// </summary>
    /// <remarks>
    /// <para>The acceleration, and the whole of it. Progress runs 0 at the start to 1 at the end,
    /// and the interval is lerped from <see cref="startTickSeconds"/> to
    /// <see cref="endTickSeconds"/> across it - so the swing gets faster and faster as the
    /// countdown runs out. Read fresh on every beat rather than stored, which is what makes it a
    /// ramp rather than a fixed rate chosen at activation.</para>
    /// <para>Time speeding up is where the name comes from, and the apworld's own BuffType
    /// docstring describes it the same way: "starts the metronome slow and speeds it up from
    /// there".</para>
    /// </remarks>
    private static float CurrentInterval
    {
        get
        {
            // A resync from a host running an older build, or an activation that somehow carried
            // no length. Falls back to the opening interval rather than dividing by zero.
            if (totalSeconds <= 0f) return startTickSeconds;

            // Clamped because secondsRemaining can sit a hair above totalSeconds on the frame it
            // starts, and a hair below zero on the frame it ends.
            float progress = Mathf.Clamp01(1f - secondsRemaining / totalSeconds);
            return Mathf.Lerp(startTickSeconds, endTickSeconds, progress);
        }
    }

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

        float startTick = ArchipelagoMenu.MadeInHeavenStartTickSeconds.Value;
        float endTick = ArchipelagoMenu.MadeInHeavenEndTickSeconds.Value;

        string from = string.IsNullOrWhiteSpace(sender) ? "" : $" from {sender}";
        Plugin.BepinLogger.LogInfo(
            $"[{FeedTag}] activating a Made in Heaven{from} for {seconds}s, beat {startTick}s -> {endTick}s");

        // The one place this is ever set. Whoever the multiworld gave the buff to is the one
        // player it does not beat, and Receive is the only path the Archipelago client reaches.
        isActivator = true;

        // The activating player's own line, and their own copy of the countdown, applied here
        // rather than waiting for the broadcast to come back around. Mycelium delivers a
        // broadcast to its sender as well, and MadeInHeavenNet drops our own copy, so this is
        // the one and only place the local half happens.
        KillFeed.Write(FeedTag, ActivationCry);
        Begin(seconds, startTick, endTick);

        // The two intervals travel with the length for the same reason it does: the beat takes
        // hold of people's leaning, so the lobby has to be swinging to one rhythm rather than to
        // whatever each machine has in its own .cfg.
        MadeInHeavenNet.Announce(KillFeed.LocalPlayerName, seconds, startTick, endTick);
    }

    /// <summary>
    /// Starts the countdown on THIS machine, having been told about an activation somewhere in
    /// the lobby. Called from <see cref="MadeInHeavenNet"/> for a remote one.
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
    public static void AdoptRemaining(float seconds, float total, float startTick, float endTick)
    {
        // The host only sends this while its own is running, so a zero here would be a message
        // that outlived what it describes. Nothing to sync to.
        if (seconds <= 0f) return;

        bool wasRunning = Running;
        float before = secondsRemaining;

        secondsRemaining = seconds;

        // The ramp comes with it. A client correcting its clock but keeping its own idea of the
        // total would end up somewhere else on the curve than everyone it is syncing with, and a
        // client repairing from nothing has no total at all to interpolate against.
        totalSeconds = total;
        startTickSeconds = startTick;
        endTickSeconds = endTick;

        if (wasRunning)
        {
            // isActivator is deliberately left alone here. It is the host's CLOCK that is
            // authoritative, not its idea of who owns the buff - overwriting it would turn the
            // activating player into one of the beaten the first time a round ended.
            Plugin.BepinLogger.LogDebug(
                $"[{FeedTag}] resynced to the host: {before:0.00}s -> {seconds:0.00}s");
            return;
        }

        Plugin.BepinLogger.LogInfo(
            $"[{FeedTag}] the host reports a Made in Heaven with {seconds:0.00}s left that this " +
            "machine knew nothing about; adopting it");

        // Nothing was running, so this machine cannot be the one that activated it - an activator
        // would have gone through Receive. It is one of the beaten, and starts beating now.
        isActivator = false;

        if (MetronomeTrap.Cancel())
        {
            KillFeed.Write(FeedTag, "The metronome stops.");
        }

        MetronomeBeat.Start();
        KillFeed.Write(FeedTag, "Made in Heaven is already under way.");
    }

    /// <summary>
    /// The half every machine does the same way, wherever the activation came from: throw out any
    /// Metronome trap it lands on, start the countdown over, and take up the beat unless this is
    /// the activating player's machine.
    /// </summary>
    private static void Begin(int seconds, float startTick, float endTick)
    {
        // A Made in Heaven outranks a Metronome, so the trap goes - including the lean it was
        // holding the player in, which Cancel releases. Reported only when there was actually
        // one running, so a quiet activation stays quiet. Note this is the trap the buff lands ON
        // TOP of; one that arrives DURING it is queued instead - see MetronomeTrap.Receive.
        if (MetronomeTrap.Cancel())
        {
            KillFeed.Write(FeedTag, "The metronome stops.");
        }

        // Assignment, not addition: a second Made in Heaven replaces the first outright rather
        // than extending it the way a second Metronome extends its trap. The ramp is replaced
        // wholesale with it, so the new one runs its own curve from the top.
        secondsRemaining = seconds;
        totalSeconds = seconds;
        startTickSeconds = startTick;
        endTickSeconds = endTick;

        // "Everyone except the activating player", in one line. Started rather than left alone
        // even if a beat was already going, so a second Made in Heaven restarts the rhythm from
        // upright along with the clock.
        if (isActivator)
        {
            MetronomeBeat.Stop();
            return;
        }

        MetronomeBeat.Start();
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
            MadeInHeavenNet.Resync(secondsRemaining, totalSeconds, startTickSeconds, endTickSeconds);
        }

        if (betweenRounds) return;

        float delta = Time.deltaTime;
        secondsRemaining -= delta;

        // The beat, on the interval this moment in the countdown calls for - which is what makes
        // it accelerate. Skipped entirely on the activating player's machine, where no beat was
        // ever started; MetronomeBeat.Advance would refuse anyway, but not asking says why.
        //
        // The same delta the countdown just spent, so a frame held between rounds holds the beat
        // with it and the rhythm cannot run on while the clock is stopped.
        if (!isActivator)
        {
            MetronomeBeat.Advance(delta, CurrentInterval);
        }

        if (secondsRemaining > 0f) return;

        secondsRemaining = 0f;

        // Upright again, and the player has their own lean back from this frame on. Cleared
        // before ReleaseHeld so a queued trap starting on this same frame gets a clean rhythm.
        MetronomeBeat.Stop();
        isActivator = false;

        KillFeed.Write(FeedTag, "The Heaven plan has completed.");

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
    public static void Announce(string playerName, int seconds, float startTick, float endTick)
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
            ReliableType.Reliable, playerName, seconds, startTick, endTick);
    }

    /// <summary>
    /// Runs on every machine in the lobby. The sender's own copy is dropped, because
    /// <see cref="MadeInHeaven.Receive"/> has already done its half with the line that
    /// belongs to whoever set it off.
    /// </summary>
    [CustomRPC]
    public void ClientMadeInHeavenActivated(string playerName, int seconds, float startTick,
        float endTick, RPCInfo info)
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
                $"[MadeInHeaven] {playerName} ({info.SenderSteamID}) activated one for {seconds}s, " +
                $"beat {startTick}s -> {endTick}s");

            MadeInHeaven.ReceiveRemote(playerName, seconds, startTick, endTick);
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
    /// round ends - see <see cref="MadeInHeaven.Tick"/> for why that moment.
    /// </summary>
    /// <remarks>
    /// <para>Floats, sent as floats: Mycelium serializes System.Single natively, so there is no
    /// reason to round the remainder to whole seconds and re-introduce error while correcting for
    /// it.</para>
    /// <para>The whole shape of the countdown goes, not just what is left of it. A client
    /// correcting its clock while keeping its own idea of the total would sit at a different point
    /// on the acceleration ramp than the machines it is syncing with, and one repairing from
    /// nothing has no total to interpolate against at all.</para>
    /// </remarks>
    public static void Resync(float secondsRemaining, float totalSeconds, float startTick, float endTick)
    {
        if (instance == null || !MyceliumNetwork.InLobby) return;

        MyceliumNetwork.RPC(RouletteNet.ModId, nameof(ClientMadeInHeavenResync),
            ReliableType.Reliable, secondsRemaining, totalSeconds, startTick, endTick);
    }

    /// <summary>
    /// Runs on every machine in the lobby. Takes the host's remaining time as the truth.
    /// </summary>
    [CustomRPC]
    public void ClientMadeInHeavenResync(float secondsRemaining, float totalSeconds,
        float startTick, float endTick, RPCInfo info)
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

            MadeInHeaven.AdoptRemaining(secondsRemaining, totalSeconds, startTick, endTick);
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
    /// <para>The metronome answer is <see cref="MetronomeBeat.IsBeating"/> rather than "a
    /// countdown exists", so the effect is on for exactly as long as the beat is moving - one held
    /// because the player is dead or between rounds hands the game back to vanilla until they are
    /// playing again.</para>
    /// <para>The BEAT, not either countdown, so this covers a Made in Heaven as well as a
    /// Metronome trap - and answers false for the player who activated the Made in Heaven, who has
    /// no beat of their own and so keeps vanilla leaning along with vanilla's penalties.</para>
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
                    return MetronomeBeat.IsBeating;
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
/// <para>Overwriting the pair is also the whole of "the player cannot lean by hand while a
/// metronome has them". Vanilla's lean input has already been read and turned into those two
/// booleans by the time this runs, so replacing them discards it; nothing else carries it forward,
/// and the toggle latches behind it only ever feed the same two booleans.</para>
/// <para>The forced lean is gated on <see cref="MetronomeBeat.Running"/>, NOT on
/// <see cref="MetronomeMovement.Active"/>: the dropdown decides whether leaning costs the player
/// anything, and the beat decides whether they are leaning. They are independent, so a player who
/// set the dropdown to Never gets swung about AND pays vanilla's full price for it, which is the
/// harsher trap and the honest reading of the setting.</para>
/// <para>Running rather than <see cref="MetronomeBeat.IsBeating"/>, which is what the movement
/// half reads: a countdown held because the player is dead or between rounds has its beat frozen
/// too, and letting them straighten up in the meantime would put the lean out of step with the
/// phase it stopped on.</para>
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
            bool forcing = MetronomeBeat.Running && __instance != null && __instance.IsOwner;

            if (forcing)
            {
                MetronomeBeatLean beat = MetronomeBeat.CurrentLean;

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
