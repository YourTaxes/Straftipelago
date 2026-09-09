using System;
using System.Collections.Generic;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Utils;

namespace Straftapelago.Finnegan_McD.org.Patches;

/// <summary>
/// When the two leaning patches below may act, and where they report a failure.
/// </summary>
internal static class MetronomeMovement
{
    private static readonly HashSet<string> Reported = new();

    /// <summary>
    /// Whether the leaning modifiers are off right now. The metronome answer is
    /// <see cref="MetronomeBeat.IsBeating"/>, so the effect is on for exactly as long as the
    /// beat is moving, and false for the player who activated a Made in Heaven.
    /// </summary>
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
                // be holding.
                default:
                    return MetronomeBeat.IsBeating;
            }
        }
    }

    /// <summary>
    /// Reports a patch failure once per patch, and never again for the rest of the session.
    /// These run inside FirstPersonController's per-frame methods, so a repeating fault would
    /// otherwise write sixty lines a second into LogOutput.log.
    /// </summary>
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
/// Takes every penalty off leaning - speed, sprint and the free silence - by clearing
/// <c>isLeaning</c> for the length of vanilla's Update, which is the one field all four
/// penalties hang off; and, while a beat is running, writes
/// <c>isLeaningLeft</c>/<c>isLeaningRight</c> from the beat instead of from the player's input,
/// then rebuilds <c>isLeaning</c> from the pair the way vanilla's own last statement does.
/// Both halves are one class because they are two halves of one write and Harmony does not
/// order postfixes against each other; the forced lean is gated on
/// <see cref="MetronomeBeat.Running"/> rather than <see cref="MetronomeMovement.Active"/>,
/// since the dropdown decides what leaning costs and the beat decides whether the player is
/// leaning at all.
/// </summary>
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

            // Everything follows from this one line: vanilla reads it in CalculateMovementInput,
            // HandleFootsteps and both sprint tests before the end of this Update.
            ___isLeaning = false;
            suppressed = true;
        }
        catch (Exception error)
        {
            // A movement effect that cannot be applied must not abandon the rest of the
            // player's Update.
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

                // Written unconditionally, both of them: on a None beat this stands the player
                // back up, and otherwise it throws away whatever their lean keys asked for.
                ___isLeaningLeft = beat == MetronomeBeatLean.Left;
                ___isLeaningRight = beat == MetronomeBeatLean.Right;
            }

            // Nothing was touched, so vanilla's own derivation already stands.
            if (!forcing && !wasSuppressed) return;

            ___isLeaning = ___isLeaningLeft || ___isLeaningRight;
        }
        catch (Exception error)
        {
            MetronomeMovement.ReportOnce(nameof(FirstPersonControllerLeanPenaltyPatch), error);
        }
    }
}

/// <summary>
/// Lets the player lean airborne and while sliding, by telling vanilla's HandleCameraLean it is
/// grounded and not sliding for the length of that one call and putting both back afterwards.
/// </summary>
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
            // the rest of the frame is concerned.
            MetronomeMovement.ReportOnce(nameof(FirstPersonControllerLeanAnywherePatch), error);
        }
    }
}
