using System;
using System.Collections.Generic;
using HarmonyLib;
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
    /// <see cref="Time.frameCount"/> of the last frame <see cref="Tick"/> actually spent time on.
    /// </summary>
    private static int lastCountedFrame = -1;

    /// <summary>
    /// What the overlay draws. Zero means no countdown is running and nothing is drawn.
    /// </summary>
    public static float SecondsRemaining => secondsRemaining;

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

            // Wrapped here rather than indexed with a modulo, so the counter cannot run away
            // over a long session and is always a legal index on its own.
            tickIndex = (tickIndex + 1) % TickWords.Length;
        }

        if (secondsRemaining > 0f) return;

        secondsRemaining = 0f;
        sinceLastTick = 0f;
        tickIndex = 0;
        lastCountedFrame = -1;
        KillFeed.Write(FeedTag, "Metronome wound down");
    }
}

/// <summary>
/// The room's Made in Heaven buff: the apworld's other filler buff, alongside
/// <see cref="PlayerHealthBuff"/>, and the one <c>buff_type</c> defaults to.
/// </summary>
/// <remarks>
/// <para>SKELETON. The item is recognised, announced and logged; none of the effect below is
/// implemented yet, and receiving one currently changes nothing about the match.</para>
/// <para>What it is meant to do, start a metronome for
/// EVERYONE in the Straftat lobby EXCEPT FOR the receiving player, beating slowly at
/// first and accelerating from there - time speeding up, which is where the name comes from. The
/// pieces it will be built out of are all in this file already: <see cref="MetronomeTrap"/>'s
/// countdown and its <see cref="MetronomeTrap.IsCountingDown"/> gate, which the leaning modifier
/// patches at the bottom read, plus a beat interval that shortens as the countdown runs instead
/// of the fixed <see cref="ArchipelagoMenu.MetronomeTickSeconds"/> the trap uses.</para>
/// <para> a caveat is that there should be in the config, but not seen by the in the modmenu, an option for the starting speed
/// and the ending speed, and the current speed shoud be interpolated from what percentage of the way through the timer it is.
/// the edge case is if a second instance of the buff happens while one is already going, then the max length that is being used for interpolation should be updated to match the new maxlength.</para>
/// <para>The lobby-wide half is the part with no groundwork yet. Nobody else in the match is
/// running an Archipelago client, so their game has to be told over the game's own network the
/// way <see cref="RouletteNet"/> tells it about the weapon pool - a received buff cannot simply
/// be applied on every machine the way a local trap is.</para>
/// <para>Main-thread only, like the trap: <c>ArchipelagoClient.ApplyReceivedItem</c> queues this
/// through <see cref="MainThreadActions"/> rather than calling it on the websocket thread.</para>
/// </remarks>
internal static class MadeInHeavenBuff
{
    /// <summary>The tag every line this buff writes is filed under in LogOutput.log.</summary>
    private const string FeedTag = "MadeInHeaven";

    /// <summary>
    /// Takes delivery of one Made in Heaven from the room.
    /// </summary>
    /// <param name="sender">The slot that sent it, for the line the player is shown. Blank or
    /// null drops the clause rather than naming nobody, the same as
    /// <see cref="MetronomeTrap.Receive"/>.</param>
    public static void Receive(string sender)
    {
        string from = string.IsNullOrWhiteSpace(sender) ? "" : $" from {sender}";

        // Said out loud even though nothing happens yet. A buff that arrives in silence is
        // indistinguishable from one the mod never received, which is the harder thing of the two
        // to debug - and the same reason AllowOneShot reports the ones it drops.
        KillFeed.Write(FeedTag, $"Made in Heaven{from} - not implemented yet, nothing happens");
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

Two patches on FirstPersonController that between them make leaning cost the player nothing:
they move exactly as though they were standing straight, and they make exactly as much noise
doing it. What leaning still does is look like a lean and see round the corner.

Neither reimplements any of vanilla's movement maths. They work by lying to vanilla about one or
two booleans for the length of a single call, so the game's own code computes the unpenalised
answer and everything downstream of it stays consistent by construction.

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
            string setting = ArchipelagoMenu.RemoveLeaningModifiers.Value;

            if (setting == ArchipelagoMenu.LeaningModifiersAlways) return true;
            if (setting == ArchipelagoMenu.LeaningModifiersNever) return false;

            // Metronome mode, and the fallback for anything else the entry could be holding.
            // The config's AcceptableValueList already refuses a fourth value, so this is only
            // ever reached as the middle choice.
            return MetronomeTrap.IsCountingDown;
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
/// from vanilla's Update for the length of that call.
/// </summary>
/// <remarks>
/// <para>The postfix restores the field the same way vanilla's own last statement does, rather
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

    static void Postfix(ref bool ___isLeaning, bool ___isLeaningLeft, bool ___isLeaningRight)
    {
        if (!suppressed) return;
        suppressed = false;

        try
        {
            // Vanilla's own IL_0824, repeated. On a full Update this writes back the value it
            // just wrote; on one that took the early return it is the value it would have.
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
