using System.Collections.Generic;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Archipelago;
using UnityEngine.SceneManagement;

namespace Straftapelago.Finnegan_McD.org.Patches;

/// <summary>
/// How many takes - and how many rounds - the local player's team has won since the game was
/// launched.
/// </summary>
/// <remarks>
/// <para>Vanilla keeps no such number. A "take" is one fight - the game logs "X won the take"
/// when a team is the last one standing and calls <c>ScoreManager.AddRoundScore</c> - while a
/// "round" is first-to-<c>RoundScoreRequiredToWin</c> takes, which is the thing that ends with
/// the scoreboard screen and a map change. The per-take score lives in the
/// <c>ScoreManager.RoundScore</c> SyncDictionary and is CLEARED the moment a round is won
/// (<c>ScoreManager.ResetRound</c>), so nothing in the game accumulates takes across a session.
/// <c>Settings.IncreaseRoundsWon</c> is not it either: that counts rounds, and is only reached
/// from the end-of-round scoreboard.</para>
/// <para>So this counts them itself, off the one signal every client gets per take: the
/// <c>UpdateMatchPointsHUD</c> ObserversRpc, which the server sends once per resolved take with
/// the whole round-score table, before it resets anything. Snapshot-and-diff rather than
/// "was I the winner", because that RPC's team argument is the ROUND winner (-1 until someone
/// takes the round), not the take winner.</para>
/// </remarks>
internal static class TakeTracker
{
    /// <summary>Takes the local player's team has won this session.</summary>
    internal static int TakesWon { get; private set; }

    /// <summary>
    /// Rounds the local player's team has won this session - the first-to-N-takes wins, each of
    /// which ends with the scoreboard screen and a new map.
    /// </summary>
    /// <remarks>
    /// Free, unlike the takes: the RPC's own team argument IS the round winner, and is -1 on
    /// every take that did not end a round, so no snapshot is involved. Kept in this class
    /// because it is the same session-long, memory-only span as <see cref="TakesWon"/> -
    /// counted from process start, gone when the game closes.
    /// </remarks>
    internal static int RoundsWon { get; private set; }

    /// <summary>What the round-score table said at the previous take. See <see cref="Observe"/>.</summary>
    private static readonly Dictionary<int, int> lastSeenRoundScores = new();

    /// <summary>No team could be resolved for the local player, so this take cannot be judged.</summary>
    private const int NoTeam = int.MinValue;

    /// <summary>
    /// Drops the snapshot on every scene load.
    /// </summary>
    /// <remarks>
    /// A round win is followed by <c>SceneMotor.ChangeNetworkScene</c>, so a new map means the
    /// round scores started over at zero. The decrease test in <see cref="Observe"/> catches
    /// that on its own in every case but one: a room where a single take wins the round leaves
    /// the snapshot at 1 and the first take of the next map also reports 1, which is no decrease
    /// and would swallow the win. Clearing here removes that hole rather than betting on the
    /// room's RoundScoreRequiredToWin being greater than one.
    /// </remarks>
    internal static void Install()
    {
        SceneManager.sceneLoaded += (scene, mode) => lastSeenRoundScores.Clear();
    }

    /// <summary>
    /// Judges one resolved take from the round-score table the server just broadcast.
    /// </summary>
    /// <remarks>
    /// At most one take is credited per call, whatever the arithmetic says. A team's score can
    /// only ever go up by one per take, so the clamp costs nothing in normal play and is what
    /// stops a player who joins a match in progress - and inherits a team that already has
    /// points - from being handed those points as if they had won them.
    /// </remarks>
    /// <param name="roundWinnerTeamId">
    /// The team that just won the ROUND, or -1 when this take did not end one.
    /// </param>
    internal static void Observe(int roundWinnerTeamId, Dictionary<int, int> roundScores)
    {
        if (roundScores == null) return;

        // A round win clears the table, and ResetRound also removes the keys outright, so
        // "went down" has to cover "is no longer there" as well.
        if (WasResetSince(roundScores)) lastSeenRoundScores.Clear();

        int teamId = LocalTeamId();
        if (teamId != NoTeam)
        {
            lastSeenRoundScores.TryGetValue(teamId, out int previous);
            roundScores.TryGetValue(teamId, out int current);

            if (current > previous)
            {
                TakesWon++;
                Plugin.BepinLogger.LogInfo(
                    $"[TakeTracker] team {teamId} won a take; {TakesWon} won this session.");

                // Here rather than on a timer, so a room whose goal is takes hears about a
                // finished world on the take that finished it.
                GoalTracker.Evaluate();
            }

            if (roundWinnerTeamId == teamId)
            {
                RoundsWon++;
                Plugin.BepinLogger.LogInfo(
                    $"[TakeTracker] team {teamId} won a round; {RoundsWon} won this session.");
            }
        }

        lastSeenRoundScores.Clear();
        foreach (KeyValuePair<int, int> teamScore in roundScores)
        {
            lastSeenRoundScores[teamScore.Key] = teamScore.Value;
        }
    }

    /// <summary>True when the round scores were wiped between the snapshot and this table.</summary>
    private static bool WasResetSince(Dictionary<int, int> roundScores)
    {
        foreach (KeyValuePair<int, int> remembered in lastSeenRoundScores)
        {
            roundScores.TryGetValue(remembered.Key, out int current);
            if (current < remembered.Value) return true;
        }

        return false;
    }

    /// <summary>The team the local player is on, or <see cref="NoTeam"/> if that is not up yet.</summary>
    /// <remarks>
    /// The SyncDictionary is read directly rather than through <c>ScoreManager.GetTeamId</c>:
    /// that helper WRITES a default team through SetTeamId for a player it does not know, which
    /// is a server-only mutation, and this runs on every client.
    /// </remarks>
    private static int LocalTeamId()
    {
        ClientInstance client = ClientInstance.Instance;
        if (client == null) return NoTeam;

        ScoreManager scores = ScoreManager.Instance;
        if (scores == null) return NoTeam;

        return scores.PlayerIdToTeamId.TryGetValue(client.PlayerId, out int teamId) ? teamId : NoTeam;
    }
}

/// <summary>
/// The per-take hook. See <see cref="TakeTracker"/> for why this RPC is the one being watched.
/// </summary>
/// <remarks>
/// The weaver-generated RpcLogic method rather than <c>UpdateMatchPointsHUD</c> itself: on a
/// receiving client the public method is only the writer, and its body never runs. Patching the
/// logic method also keeps this working on the host, whose own client connection is an observer
/// of the RPC like any other. Note that vanilla's RpcLogic bails out before touching the HUD
/// when the local player object is missing, so the HUD component is NOT a safe place to hook -
/// this postfix runs either way.
/// </remarks>
[HarmonyPatch(typeof(GameManager), "RpcLogic___UpdateMatchPointsHUD_1259646723")]
public class MatchPointsHudPatch
{
    static void Postfix(int winningTeamId, Dictionary<int, int> roundScores)
    {
        TakeTracker.Observe(winningTeamId, roundScores);
    }
}
