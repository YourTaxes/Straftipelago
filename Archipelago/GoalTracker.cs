using Straftapelago.Finnegan_McD.org.Patches;
using Straftapelago.Finnegan_McD.org.Utils;

namespace Straftapelago.Finnegan_McD.org.Archipelago;

/// <summary>
/// Watches the room's two goal thresholds and reports each one the moment it is met. The
/// apworld's Takes_Complete and Weapons_Complete goals are events, so they have no location id
/// and nothing can be sent for one individually; what the server hears is the StatusUpdate that
/// says this slot has finished, once the win_condition's events are all achieved. Each event is
/// announced in the Archipelago console as it happens. Neither flag is stored - the weapon side
/// counts checks the server remembers and the take side counts the session - so
/// <see cref="Evaluate"/> is safe to call as often as anything likes.
/// </summary>
internal static class GoalTracker
{
    /// <summary>Whether enough takes have been won for the apworld's Takes_Complete event.</summary>
    internal static bool TakesGoalMet { get; private set; }

    /// <summary>Whether enough first-kill checks are in for the apworld's Weapons_Complete event.</summary>
    internal static bool WeaponsGoalMet { get; private set; }

    /// <summary>Latches the StatusUpdate, which is a fire-and-forget packet worth sending once.</summary>
    private static bool reportedToRoom;

    /// <summary>
    /// Re-reads both goals and announces anything that has just been achieved. Called from every
    /// place that can move either number - a take won, a first kill, a pool rebuild - rather
    /// than from a frame loop, so the room hears about a finished world when it finishes.
    /// </summary>
    internal static void Evaluate()
    {
        // A goal is a room's idea, and offline ServerData is only holding the apworld's
        // defaults. Announcing "goal achieved" against those would be meaningless.
        if (!ArchipelagoClient.Authenticated) return;

        ArchipelagoData serverData = ArchipelagoClient.ServerData;
        if (serverData == null) return;

        if (!TakesGoalMet && TakeTracker.TakesWon >= serverData.WinThreshold)
        {
            TakesGoalMet = true;
            ArchipelagoConsole.LogMessage(
                $"Takes_Complete: {TakeTracker.TakesWon} takes won, and the room asked for " +
                $"{serverData.WinThreshold}.");
        }

        if (!WeaponsGoalMet && WeaponsGoalReached(serverData))
        {
            WeaponsGoalMet = true;
            ArchipelagoConsole.LogMessage(
                $"Weapons_Complete: first kills scored with {serverData.WeaponGoalThreshold}% of " +
                "the weapons that carry a check.");
        }

        if (reportedToRoom || !WinConditionMet(serverData.WinCondition)) return;

        reportedToRoom = true;
        ArchipelagoConsole.LogMessage("Goal complete! Telling the room this slot is finished.");
        Plugin.ArchipelagoClient?.SendGoalCompletion();
    }

    /// <summary>
    /// Whether the earned share of the check-carrying weapons has reached the room's threshold.
    /// Cross-multiplied rather than divided, so this agrees exactly with the floored percentage
    /// the pause menu shows and the tick can never appear a weapon early or late.
    /// </summary>
    private static bool WeaponsGoalReached(ArchipelagoData serverData)
    {
        RouletteState roulette = Plugin.RouletteState;
        if (roulette == null) return false;

        int checkable = roulette.CheckableWeaponCount;

        // No roster yet means the pool has not been built - out of a match there is nothing to
        // have earned a share of, and 0 of 0 must not read as "all of them".
        if (checkable <= 0) return false;

        return roulette.EarnedWeaponCount * 100 >= serverData.WeaponGoalThreshold * checkable;
    }

    /// <summary>Whether the events this room's win condition asks for have all been achieved.</summary>
    private static bool WinConditionMet(GoalCondition winCondition)
    {
        switch (winCondition)
        {
            case GoalCondition.WeaponKills: return WeaponsGoalMet;
            case GoalCondition.Wins: return TakesGoalMet;
            case GoalCondition.Both: return TakesGoalMet && WeaponsGoalMet;
            default: return false;
        }
    }

    /// <summary>
    /// Forgets both goals, so the next room is judged on its own thresholds and its own checks.
    /// </summary>
    internal static void Reset()
    {
        TakesGoalMet = false;
        WeaponsGoalMet = false;
        reportedToRoom = false;
    }
}
