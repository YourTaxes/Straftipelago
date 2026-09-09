using Straftapelago.Finnegan_McD.org.Patches;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// One-slot latch for a roll that has been sent to the host and is waiting on the spawned
/// object to come back. Only ever set on the peer that owns the grabbing player, so a single
/// slot is enough.
/// </summary>
internal static class PendingRoll
{
    // Covers a network round trip rather than a game rule, so it is generous: an expiry
    // should produce the log line below, not a silently swallowed roll.
    private const int TimeoutFrames = 300;

    private static int nextRollId = 1;

    public static bool IsArmed;
    public static int RollId;
    public static PlayerPickup Pickup;
    public static bool RightHand;
    public static ItemBehaviour Roulette;
    public static int ArmedFrame;
    public static string LastStep = "none";

    public static int NextRollId() => nextRollId++;

    public static void Arm(int rollId, PlayerPickup pickup, bool rightHand, ItemBehaviour roulette)
    {
        IsArmed = true;
        RollId = rollId;
        Pickup = pickup;
        RightHand = rightHand;
        Roulette = roulette;
        ArmedFrame = Time.frameCount;
        LastStep = "send";
    }

    public static void Disarm()
    {
        IsArmed = false;
        Pickup = null;
        Roulette = null;
    }

    /// <summary>
    /// Pumped from PlayerPickupUpdatePatch. Without this a lost request would leave the latch
    /// set, and the next unrelated spawn would be mistaken for the answer to it.
    /// </summary>
    public static void CheckTimeout()
    {
        if (!IsArmed || Time.frameCount - ArmedFrame < TimeoutFrames) return;

        DiagLog.RR(RollId, "timeout",
            $"waitedFrames={Time.frameCount - ArmedFrame} lastStepReached={LastStep} — " +
            "no SetSpawnedObject came back. Check the HOST's log for a matching [RR:server-spawn] " +
            "to tell 'never sent' from 'never came back'.");

        // The roulette is still in hand and would stay there forever otherwise.
        if (Roulette != null) GrabPatches.DespawnRoulette(RollId, Roulette);
        Disarm();
    }
}
