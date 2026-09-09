using System;
using System.Collections.Generic;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Archipelago;
using UnityEngine.SceneManagement;

namespace Straftapelago.Finnegan_McD.org.Patches;

/// <summary>
/// How many takes - and how many rounds - the local player's team has won since the game was
/// launched. Vanilla keeps no such number: a take is one fight, a round is
/// first-to-RoundScoreRequiredToWin takes, and the per-take score is cleared the moment a round
/// is won. So this counts them off the one signal every client gets per take, the
/// UpdateMatchPointsHUD ObserversRpc, by snapshotting and diffing the round-score table - that
/// RPC's team argument is the ROUND winner, not the take winner.
/// </summary>
internal static class TakeTracker
{
    /// <summary>Takes the local player's team has won this session.</summary>
    internal static int TakesWon { get; private set; }

    /// <summary>
    /// Rounds the local player's team has won this session - the first-to-N-takes wins, each of
    /// which ends with the scoreboard screen and a new map. No snapshot is involved: the RPC's
    /// own team argument is the round winner, and is -1 on every take that did not end one.
    /// </summary>
    internal static int RoundsWon { get; private set; }

    /// <summary>
    /// The apworld names its round-win locations Round_1, Round_2, ... Round_N, where N is the
    /// room's round_checks option.
    /// </summary>
    private const string RoundCheckPrefix = "Round_";

    /// <summary>What the round-score table said at the previous take. See <see cref="Observe"/>.</summary>
    private static readonly Dictionary<int, int> lastSeenRoundScores = new();

    /// <summary>No team could be resolved for the local player, so this take cannot be judged.</summary>
    private const int NoTeam = int.MinValue;

    /// <summary>
    /// Drops the snapshot on every scene load, since a new map means the round scores started
    /// over at zero. The decrease test in <see cref="Observe"/> covers that on its own except
    /// where a single take wins the round: the snapshot and the next map's first take both read
    /// 1, which is no decrease and would swallow the win.
    /// </summary>
    internal static void Install()
    {
        SceneManager.sceneLoaded += (scene, mode) => lastSeenRoundScores.Clear();
    }

    /// <summary>
    /// Judges one resolved take from the round-score table the server just broadcast. At most
    /// one take is credited per call, which is what stops a player who joins a match in progress
    /// and inherits a team's existing points from being handed them as wins.
    /// </summary>
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

                SendRoundCheck();
            }
        }

        lastSeenRoundScores.Clear();
        foreach (KeyValuePair<int, int> teamScore in roundScores)
        {
            lastSeenRoundScores[teamScore.Key] = teamScore.Value;
        }
    }

    /// <summary>
    /// Sends the check for the round that was just won, if the room has one for it. Capped at
    /// the room's round_checks, past which there is no location to send. RoundsWon itself keeps
    /// counting past the cap, and the overlay shows it against the cap.
    /// </summary>
    private static void SendRoundCheck()
    {
        ArchipelagoData serverData = ArchipelagoClient.ServerData;
        if (serverData == null) return;

        if (RoundsWon > serverData.RoundChecks)
        {
            // Debug rather than a warning: running out of round checks is an ordinary end state
            // for a long session, not a fault.
            Plugin.BepinLogger.LogDebug(
                $"[TakeTracker] round {RoundsWon} is past the room's {serverData.RoundChecks} " +
                "round checks; nothing to send.");
            return;
        }

        LocationSender.SendByLocationName($"{RoundCheckPrefix}{RoundsWon}", $"round {RoundsWon} won");
    }

    /// <summary>
    /// Sets <see cref="RoundsWon"/> to the highest round check the room already has for this
    /// slot, so a reconnect carries on from there instead of re-sending Round_1. The highest
    /// rather than the count, because that is what the next send has to follow.
    /// </summary>
    internal static void SeedRoundsWonFromRoom()
    {
        int highest = 0;

        foreach (string locationName in ArchipelagoClient.GetCheckedLocationNames())
        {
            if (locationName == null || !locationName.StartsWith(RoundCheckPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string suffix = locationName.Substring(RoundCheckPrefix.Length);
            if (int.TryParse(suffix, out int roundNumber) && roundNumber > highest) highest = roundNumber;
        }

        if (highest <= RoundsWon) return;

        Plugin.BepinLogger.LogInfo(
            $"[TakeTracker] the room already has round checks up to {highest}; continuing from there.");
        RoundsWon = highest;
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

    /// <summary>
    /// The team the local player is on, or <see cref="NoTeam"/> if that is not up yet. The
    /// SyncDictionary is read directly rather than through ScoreManager.GetTeamId, because that
    /// helper writes a default team for a player it does not know, which is a server-only
    /// mutation, and this runs on every client.
    /// </summary>
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
/// The per-take hook. The weaver-generated RpcLogic method rather than UpdateMatchPointsHUD
/// itself, because on a receiving client the public method is only the writer and its body never
/// runs; this also covers the host, whose own client connection observes the RPC like any other.
/// </summary>
[HarmonyPatch(typeof(GameManager), "RpcLogic___UpdateMatchPointsHUD_1259646723")]
public class MatchPointsHudPatch
{
    static void Postfix(int winningTeamId, Dictionary<int, int> roundScores)
    {
        TakeTracker.Observe(winningTeamId, roundScores);
    }
}
