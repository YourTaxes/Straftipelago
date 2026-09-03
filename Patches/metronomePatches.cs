using System;
using System.Collections.Generic;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

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
/// <para>What it is meant to do, from the roadmap in the apworld's README: start a metronome for
/// EVERYONE in the Straftat lobby rather than for the receiving player alone, beating slowly at
/// first and accelerating from there - time speeding up, which is where the name comes from. The
/// pieces it will be built out of are all in this file already: <see cref="MetronomeTrap"/>'s
/// countdown and its <see cref="MetronomeTrap.IsCountingDown"/> gate, which the leaning modifier
/// patches at the bottom read, plus a beat interval that shortens as the countdown runs instead
/// of the fixed <see cref="ArchipelagoMenu.MetronomeTickSeconds"/> the trap uses.</para>
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

Three patches on FirstPersonController that between them take the movement penalties off
leaning. While they are on:

- Sprint flags. isSprinting and isSlideSprinting are recomputed from just "not aiming, holding
  a direction, sprint held" - plus "not crouching" for the plain one - so a lean no longer drops
  the player out of a sprint.

- Lean speed. Every speedFactor branch in vanilla's CalculateMovementInput carries a
  `&& !isLeaning`, which is why leaning bleeds speed away to the global deceleration. The
  sprint-air, air, crouch and sprint branches are run again without that condition, so a lean
  keeps its speed. The re-run happens whether or not the player is actually leaning, so on a
  frame they are not, that branch's Lerp lands a second time on the value vanilla just wrote and
  the ramp up to top speed is quicker. Both halves of that are the effect.

- Lean anywhere. Vanilla's HandleCameraLean only leans while `!isSliding && isGrounded`. Both
  conditions are dropped, so the lean also works airborne and mid-slide.

WHEN they are on is ArchipelagoMenu.RemoveLeaningModifiers, read through MetronomeMovement.Active:
always, never, or - the default - only while a Metronome countdown is running, which is what
makes that trap change how the game plays rather than only how it looks.

All three are confined to the local player with IsOwner: the flags, the speed and the camera
they write are this machine's player's, and none of it should ever reach another player's copy.
*/

/// <summary>
/// When the three leaning patches below may act, and where they report a failure.
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
/// Holds the sprint flags through a lean.
/// </summary>
[HarmonyPatch(typeof(FirstPersonController), "Update")]
public class FirstPersonControllerSprintFlagsPatch
{
    static void Postfix(FirstPersonController __instance, ref bool ___isSprinting,
        ref bool ___isSlideSprinting, bool ___isAiming, bool ___isCrouching, bool ___funcSprint,
        InputAction ___move)
    {
        try
        {
            if (!MetronomeMovement.Active || !__instance.IsOwner) return;

            // Read once and shared by both lines: it is the same value in the same frame.
            bool moving = ___move.ReadValue<Vector2>() != Vector2.zero;

            ___isSprinting = !___isAiming && moving && !___isCrouching && ___funcSprint;
            ___isSlideSprinting = !___isAiming && moving && ___funcSprint;
        }
        catch (Exception error)
        {
            // Swallowed like every other patch in this mod: a movement effect that cannot be
            // applied must not abandon the rest of the player's Update.
            MetronomeMovement.ReportOnce(nameof(FirstPersonControllerSprintFlagsPatch), error);
        }
    }
}

/// <summary>
/// Stops a lean bleeding off the player's speed.
/// </summary>
[HarmonyPatch(typeof(FirstPersonController), "CalculateMovementInput")]
public class FirstPersonControllerLeanSpeedPatch
{
    static void Postfix(FirstPersonController __instance, SlopeSlide ___slopeSlideScript,
        Slope ___slopeScript, CharacterController ___characterController, bool ___funcSprint,
        InputAction ___move, ref float ___speedFactor, bool ___isCrouching, bool ___isSprinting,
        float ___sprintAirSpeed, float ___sprintAirAcceleration, float ___airSpeed,
        float ___airAcceleration, float ___crouchSpeed, float ___crouchAcceleration,
        float ___sprintSpeed, float ___sprintAcceleration)
    {
        try
        {
            if (!MetronomeMovement.Active || !__instance.IsOwner) return;

            // Vanilla's own outer condition: crouch-sliding up a slope is its one case that
            // skips the whole speedFactor block, and this must not put speed back into it.
            if (___slopeSlideScript.isCrouchSlopeSliding && ___slopeScript.uphill) return;

            if (!___characterController.isGrounded && ___funcSprint && ___move.ReadValue<Vector2>().magnitude != 0f)
            {
                ___speedFactor = Mathf.Round(Mathf.Lerp(___speedFactor, ___sprintAirSpeed,
                    ___sprintAirAcceleration * Time.deltaTime) * 100f) * 0.01f;
            }
            else if (!___characterController.isGrounded && ___move.ReadValue<Vector2>().magnitude != 0f)
            {
                ___speedFactor = Mathf.Round(Mathf.Lerp(___speedFactor, ___airSpeed,
                    ___airAcceleration * Time.deltaTime) * 100f) * 0.01f;
            }
            else if (___isCrouching && ___move.ReadValue<Vector2>().magnitude != 0f && ___characterController.isGrounded)
            {
                ___speedFactor = Mathf.Round(Mathf.Lerp(___speedFactor, ___crouchSpeed,
                    ___crouchAcceleration * Time.deltaTime) * 100f) * 0.01f;
            }
            else if (___isSprinting && ___move.ReadValue<Vector2>().magnitude != 0f)
            {
                ___speedFactor = Mathf.Round(Mathf.Lerp(___speedFactor, ___sprintSpeed,
                    ___sprintAcceleration * Time.deltaTime) * 100f) * 0.01f;
            }
        }
        catch (Exception error)
        {
            MetronomeMovement.ReportOnce(nameof(FirstPersonControllerLeanSpeedPatch), error);
        }
    }
}

/// <summary>
/// Lets the player lean airborne and while sliding.
/// </summary>
/// <remarks>
/// <para>A full-method replacement, which <c>StraftatModAttribute.Documentation</c> warns
/// against and which this mod already does in six other places - but the vanilla method has no
/// seam to hook: the two conditions being dropped sit inside the same <c>if</c> as the call that
/// does the work, so there is nothing to postfix into. It is at least a replacement that stands
/// down completely whenever <see cref="MetronomeMovement.Active"/> is false, which the other six
/// do not.</para>
/// <para>CameraLean is private, so it is reached through a delegate built on first use and
/// cached - this runs every frame, and resolving it each time would compile a dynamic method
/// sixty times a second for the same call.</para>
/// </remarks>
[HarmonyPatch(typeof(FirstPersonController), "HandleCameraLean")]
public class FirstPersonControllerLeanAnywherePatch
{
    /// <summary>
    /// Vanilla's private <c>CameraLean(Quaternion)</c>. Null until first use, and left null if
    /// it cannot be resolved - in which case this patch stands aside and vanilla runs.
    /// </summary>
    private static Action<FirstPersonController, Quaternion> cameraLean;

    static bool Prefix(FirstPersonController __instance, bool ___isLeaningRight,
        bool ___isLeaningLeft, float ___leanLimit)
    {
        try
        {
            if (!MetronomeMovement.Active || !__instance.IsOwner) return true;

            cameraLean ??= AccessTools.MethodDelegate<Action<FirstPersonController, Quaternion>>(
                AccessTools.Method(typeof(FirstPersonController), "CameraLean"));

            // Vanilla's own three cases, with its `!isSliding && isGrounded` guard on the first
            // two dropped - that omission is the whole of this patch.
            if (___isLeaningRight)
            {
                cameraLean(__instance, Quaternion.Euler(0f, 0f, -___leanLimit));
                return false;
            }

            if (___isLeaningLeft)
            {
                cameraLean(__instance, Quaternion.Euler(0f, 0f, ___leanLimit));
                return false;
            }

            cameraLean(__instance, Quaternion.Euler(0f, 0f, 0f));
            return false;
        }
        catch (Exception error)
        {
            // True, not false: a replacement that threw has done nothing, so vanilla must still
            // get its turn or the camera would simply stop levelling out.
            MetronomeMovement.ReportOnce(nameof(FirstPersonControllerLeanAnywherePatch), error);
            return true;
        }
    }
}
