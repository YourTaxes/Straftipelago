using System.Collections.Generic;
using Straftapelago.Finnegan_McD.org.Archipelago;
using Straftapelago.Finnegan_McD.org.Patches;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

// How the run is going, in two boxes that share one set of lines: the paused block above the
// weapon list, and the smaller one kept on screen during play.
internal static partial class OverlayPanels
{
    /// <summary>
    /// Where the live stats box is centred, as a fraction of the screen width. Right of centre,
    /// clear of both the countdown row on the left and the edge of the screen.
    /// </summary>
    private const float StatsPanelCenterFraction = 0.72f;

    /// <summary>The title over both boxes' progress column.</summary>
    private const string ProgressHeader = "Progress";

    /// <summary>
    /// The numbers that say how the run is going: takes won, how much of the weapon roster has
    /// been earned, and rounds won. Takes and rounds are this session only, since vanilla
    /// accumulates neither across matches; weapons earned is the seed's progress and survives
    /// restarts. The first two are the room's two goals, so each carries its threshold and takes
    /// a tick once <see cref="GoalTracker"/> says that goal is met.
    /// One method rather than two, because the paused block and the live box have to say the
    /// same thing.
    /// </summary>
    private static List<string> ProgressLines()
    {
        // Offline the values in ServerData are only the apworld's defaults, which no room has
        // agreed to, so the thresholds are left off entirely.
        bool showGoals = ArchipelagoClient.Authenticated;
        ArchipelagoData serverData = ArchipelagoClient.ServerData;

        var lines = new List<(bool Achieved, string Text)>
        {
            (showGoals && GoalTracker.TakesGoalMet,
                $"Takes won this session: {TakeTracker.TakesWon}"
                + (showGoals ? $" (goal {serverData.WinThreshold})" : "")),
        };

        RouletteState roulette = Plugin.RouletteState;
        int earned = roulette?.EarnedWeaponCount ?? 0;
        int checkable = roulette?.CheckableWeaponCount ?? 0;

        // Zero before the first match: the pool is built off SpawnerManager, which has no
        // weapons until a player object exists, so there is genuinely no roster to be a
        // fraction of yet. Said rather than shown as 0% of 0.
        lines.Add((showGoals && GoalTracker.WeaponsGoalMet, checkable > 0
            // Floored, not rounded: 100% has to mean every check is in. GoalTracker compares
            // the same two numbers the same way, so the tick cannot disagree with the number.
            ? $"Weapons earned: {Mathf.FloorToInt(earned * 100f / checkable)}% ({earned}/{checkable})"
              + (showGoals ? $", goal {serverData.WeaponGoalThreshold}%" : "")
            : "Weapons earned: waiting for the first match"));

        // Against the room's cap, because Round_1 through Round_N are checks and a round won
        // past N sends nothing. Rounds won has no goal behind it, so it never takes a tick.
        lines.Add((false, showGoals
            ? $"Rounds won this session: {TakeTracker.RoundsWon} / {serverData.RoundChecks} checks"
            : $"Rounds won this session: {TakeTracker.RoundsWon}"));

        // The tick is folded in here rather than at draw time, because the columns are measured
        // against these strings and a tick added afterwards would be a character the box was
        // never sized for.
        var progressLines = new List<string>(lines.Count);
        foreach ((bool achieved, string text) in lines)
        {
            progressLines.Add(achieved ? $"{text} ✓" : text);
        }

        return progressLines;
    }

    /// <summary>
    /// The paused block, directly above the weapon list. Left column: <see cref="ProgressLines"/>.
    /// Right column: the DeathLink counter.
    /// </summary>
    internal static void DrawSessionProgress()
    {
        float panelLeft = Screen.width * PanelLeftFraction;
        float panelPadding = Screen.width * PanelPaddingFraction;
        float entryHeight = Screen.height * EntryHeightFraction;
        float headerHeight = entryHeight * 1.5f;

        List<string> progressLines = ProgressLines();
        List<string> deathLinkLines = DeathLinkLines(ArchipelagoClient.Authenticated);

        // Each column is as wide as its own widest line, headers included.
        float columnGap = Screen.width * ColumnGapFraction;
        float progressWidth = Mathf.Max(
            VanillaSkin.MeasureWidest(progressLines, VanillaSkin.Entry),
            VanillaSkin.MeasureWidth(ProgressHeader, VanillaSkin.Header));
        float deathLinkWidth = Mathf.Max(
            VanillaSkin.MeasureWidest(deathLinkLines, VanillaSkin.Entry),
            VanillaSkin.MeasureWidth("Deathlink", VanillaSkin.Header));

        // Capped to the same left-half budget the weapon list keeps to, so neither panel can
        // cover the pause menu. Shrunk in proportion when the two together overrun it.
        float columnsBudget = Screen.width * 0.5f - panelLeft - panelPadding * 2f - columnGap;
        if (progressWidth + deathLinkWidth > columnsBudget)
        {
            float scale = columnsBudget / (progressWidth + deathLinkWidth);
            progressWidth *= scale;
            deathLinkWidth *= scale;
        }

        float panelWidth = progressWidth + deathLinkWidth + columnGap + panelPadding * 2f;

        // The taller column decides the height, so neither can run out of the box.
        int rowCount = Mathf.Max(progressLines.Count, deathLinkLines.Count);
        float panelHeight = headerHeight + rowCount * entryHeight + entryHeight * 0.5f;

        // Bottom-anchored to the weapon list rather than given a top of its own, so the gap
        // between the two blocks stays put whatever this one contains.
        float panelTop = Screen.height * WeaponPanelTopFraction - panelHeight - entryHeight * 0.5f;

        VanillaSkin.Box(new Rect(panelLeft, panelTop, panelWidth, panelHeight));

        float entryLeft = panelLeft + panelPadding;
        float deathLinkLeft = entryLeft + progressWidth + columnGap;
        float headerTop = panelTop + entryHeight * 0.25f;
        float firstEntryTop = panelTop + headerHeight;

        VanillaSkin.Label(new Rect(entryLeft, headerTop, progressWidth, headerHeight),
            ProgressHeader, VanillaSkin.Header);
        VanillaSkin.Label(new Rect(deathLinkLeft, headerTop, deathLinkWidth, headerHeight),
            "Deathlink", VanillaSkin.Header);

        for (int i = 0; i < progressLines.Count; i++)
        {
            VanillaSkin.Label(new Rect(entryLeft, firstEntryTop + i * entryHeight, progressWidth, entryHeight),
                progressLines[i], VanillaSkin.Entry);
        }

        for (int i = 0; i < deathLinkLines.Count; i++)
        {
            VanillaSkin.Label(new Rect(deathLinkLeft, firstEntryTop + i * entryHeight, deathLinkWidth, entryHeight),
                deathLinkLines[i], VanillaSkin.Entry);
        }
    }

    /// <summary>
    /// The same progress lines while the game is not paused, in one box along the top of the
    /// screen and right of centre. Exactly <see cref="ProgressLines"/>, so it cannot drift from
    /// what the pause menu says; the DeathLink column is left to the paused block, which has the
    /// room to spell it out.
    /// </summary>
    internal static void DrawLiveStats()
    {
        List<string> progressLines = ProgressLines();

        float panelPadding = Screen.width * PanelPaddingFraction;
        float entryHeight = Screen.height * EntryHeightFraction;
        float headerHeight = entryHeight * 1.5f;

        float entryWidth = Mathf.Max(
            VanillaSkin.MeasureWidest(progressLines, VanillaSkin.Entry),
            VanillaSkin.MeasureWidth(ProgressHeader, VanillaSkin.Header));
        float panelWidth = entryWidth + panelPadding * 2f;
        float panelHeight = headerHeight + progressLines.Count * entryHeight + entryHeight * 0.5f;

        // Centred on its fraction, then held inside the right half of the screen: clear of the
        // countdown row whatever the lines say, and never running off the right edge.
        float panelLeft = Mathf.Clamp(
            Screen.width * StatsPanelCenterFraction - panelWidth * 0.5f,
            Screen.width * 0.5f,
            Screen.width - panelWidth - panelPadding);
        float panelTop = Screen.height * MetronomePanelTopFraction;

        VanillaSkin.Box(new Rect(panelLeft, panelTop, panelWidth, panelHeight), CountdownPanelAlpha);

        float entryLeft = panelLeft + panelPadding;
        float firstEntryTop = panelTop + headerHeight;

        VanillaSkin.Label(new Rect(entryLeft, panelTop + entryHeight * 0.25f, entryWidth, headerHeight),
            ProgressHeader, VanillaSkin.Header);

        for (int i = 0; i < progressLines.Count; i++)
        {
            VanillaSkin.Label(new Rect(entryLeft, firstEntryTop + i * entryHeight, entryWidth, entryHeight),
                progressLines[i], VanillaSkin.Entry);
        }
    }

    /// <summary>
    /// The second column of the paused progress box: how close the local player is to sending
    /// their next death out to the multiworld. The counter is the handler's own, so what is shown
    /// is exactly what decides the next send, and the handler only exists from a successful login
    /// onwards.
    /// </summary>
    private static List<string> DeathLinkLines(bool connected)
    {
        var lines = new List<string>();

        DeathLinkHandler handler = Plugin.ArchipelagoClient?.DeathLinkHandler;

        if (!connected || handler == null)
        {
            // Not "off": offline the value in ServerData is only the apworld's default, or the
            // last room's answer still sitting there.
            lines.Add("Not connected to a room");
            return lines;
        }

        if (!handler.DeathLinkEnabled)
        {
            lines.Add("Off for this slot");
            return lines;
        }

        int perLink = handler.DeathsPerLink;

        lines.Add(perLink == 1
            ? "On: every death is shared"
            : $"On: 1 death shared per {perLink}");

        // Shown as a fraction so it reads the same way as the weapons line beside it. The death
        // that would complete the fraction is the one that is sent, so it resets to 0 rather
        // than ever displaying full.
        lines.Add($"Deaths toward next link: {handler.DeathsTowardNextLink} / {perLink}");
        lines.Add($"Deaths shared this session: {handler.DeathLinksSent}");

        return lines;
    }
}
