using System.Collections.Generic;
using Straftapelago.Finnegan_McD.org.Archipelago;
using Straftapelago.Finnegan_McD.org.Patches;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

// How the run is going, in two boxes that share one set of lines: the paused block above the
// weapon list, and the smaller one kept on screen during play. Both are drawn every repaint,
// so the lines and their measured widths are cached and rebuilt only when a number moves.
internal static partial class OverlayPanels
{
    /// <summary>
    /// Where the live stats box is centred, as a fraction of the screen width. Right of centre,
    /// clear of both the countdown row on the left and the edge of the screen.
    /// </summary>
    private const float StatsPanelCenterFraction = 0.72f;

    /// <summary>The title over both boxes' progress column.</summary>
    private const string ProgressHeader = "Progress";

    /// <summary>The title over the paused box's second column.</summary>
    private const string DeathLinkHeader = "Deathlink";

    /// <summary>
    /// Everything the progress lines are built from, so a change to any of it is one
    /// comparison. The screen height is in it because the measured widths depend on the font
    /// size, which is a fraction of it.
    /// </summary>
    private readonly struct ProgressStamp
    {
        public ProgressStamp(bool connected, int poolVersion, int takesWon, int roundsWon,
            bool takesGoalMet, bool weaponsGoalMet, int screenHeight)
        {
            Connected = connected;
            PoolVersion = poolVersion;
            TakesWon = takesWon;
            RoundsWon = roundsWon;
            TakesGoalMet = takesGoalMet;
            WeaponsGoalMet = weaponsGoalMet;
            ScreenHeight = screenHeight;
        }

        public bool Connected { get; }
        public int PoolVersion { get; }
        public int TakesWon { get; }
        public int RoundsWon { get; }
        public bool TakesGoalMet { get; }
        public bool WeaponsGoalMet { get; }
        public int ScreenHeight { get; }

        public bool Equals(ProgressStamp other) =>
            Connected == other.Connected && PoolVersion == other.PoolVersion
            && TakesWon == other.TakesWon && RoundsWon == other.RoundsWon
            && TakesGoalMet == other.TakesGoalMet && WeaponsGoalMet == other.WeaponsGoalMet
            && ScreenHeight == other.ScreenHeight;

        public static ProgressStamp Now() => new ProgressStamp(
            ArchipelagoClient.Authenticated,
            Plugin.RouletteState?.Version ?? -1,
            TakeTracker.TakesWon,
            TakeTracker.RoundsWon,
            GoalTracker.TakesGoalMet,
            GoalTracker.WeaponsGoalMet,
            Screen.height);
    }

    private static ProgressStamp progressStamp;
    private static bool progressCached;
    private static readonly List<string> progressLines = new();
    private static float progressWidth;

    /// <summary>
    /// The numbers that say how the run is going: takes won, how much of the weapon roster has
    /// been earned, and rounds won. Takes and rounds are this session only, since vanilla
    /// accumulates neither across matches; weapons earned is the seed's progress and survives
    /// restarts. The first two are the room's two goals, so each carries its threshold and takes
    /// a tick once <see cref="GoalTracker"/> says that goal is met.
    /// One method rather than two, because the paused block and the live box have to say the
    /// same thing. Rebuilt only when <see cref="ProgressStamp"/> changes.
    /// </summary>
    /// <param name="width">The widest of the lines and the header, in pixels.</param>
    private static List<string> ProgressLines(out float width)
    {
        ProgressStamp stamp = ProgressStamp.Now();
        if (progressCached && stamp.Equals(progressStamp))
        {
            width = progressWidth;
            return progressLines;
        }

        progressStamp = stamp;
        progressCached = true;
        progressLines.Clear();

        // Offline the values in ServerData are only the apworld's defaults, which no room has
        // agreed to, so the thresholds are left off entirely.
        bool showGoals = stamp.Connected;
        ArchipelagoData serverData = ArchipelagoClient.ServerData;

        AddProgressLine(showGoals && stamp.TakesGoalMet,
            $"Takes won this session: {stamp.TakesWon}"
            + (showGoals ? $" (goal {serverData.WinThreshold})" : ""));

        RouletteState roulette = Plugin.RouletteState;
        int earned = roulette?.EarnedWeaponCount ?? 0;
        int checkable = roulette?.CheckableWeaponCount ?? 0;

        // Zero before the first match: the pool is built off SpawnerManager, which has no
        // weapons until a player object exists, so there is genuinely no roster to be a
        // fraction of yet. Said rather than shown as 0% of 0.
        AddProgressLine(showGoals && stamp.WeaponsGoalMet, checkable > 0
            // Floored, not rounded: 100% has to mean every check is in. GoalTracker compares
            // the same two numbers the same way, so the tick cannot disagree with the number.
            ? $"Weapons earned: {Mathf.FloorToInt(earned * 100f / checkable)}% ({earned}/{checkable})"
              + (showGoals ? $", goal {serverData.WeaponGoalThreshold}%" : "")
            : "Weapons earned: waiting for the first match");

        // Against the room's cap, because Round_1 through Round_N are checks and a round won
        // past N sends nothing. Rounds won has no goal behind it, so it never takes a tick.
        AddProgressLine(false, showGoals
            ? $"Rounds won this session: {stamp.RoundsWon} / {serverData.RoundChecks} checks"
            : $"Rounds won this session: {stamp.RoundsWon}");

        progressWidth = Mathf.Max(
            VanillaSkin.MeasureWidest(progressLines, VanillaSkin.Entry),
            VanillaSkin.MeasureWidth(ProgressHeader, VanillaSkin.Header));

        width = progressWidth;
        return progressLines;
    }

    /// <summary>
    /// The tick is folded in here rather than at draw time, because the columns are measured
    /// against these strings and a tick added afterwards would be a character the box was
    /// never sized for.
    /// </summary>
    private static void AddProgressLine(bool achieved, string text)
    {
        progressLines.Add(achieved ? $"{text} ✓" : text);
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

        List<string> lines = ProgressLines(out float progressColumnWidth);
        List<string> deathLinkLines = DeathLinkLines(out float deathLinkWidth);

        // Capped to the same left-half budget the weapon list keeps to, so neither panel can
        // cover the pause menu. Shrunk in proportion when the two together overrun it.
        float columnGap = Screen.width * ColumnGapFraction;
        float columnsBudget = Screen.width * 0.5f - panelLeft - panelPadding * 2f - columnGap;
        if (progressColumnWidth + deathLinkWidth > columnsBudget)
        {
            float scale = columnsBudget / (progressColumnWidth + deathLinkWidth);
            progressColumnWidth *= scale;
            deathLinkWidth *= scale;
        }

        float panelWidth = progressColumnWidth + deathLinkWidth + columnGap + panelPadding * 2f;

        // The taller column decides the height, so neither can run out of the box.
        int rowCount = Mathf.Max(lines.Count, deathLinkLines.Count);
        float panelHeight = headerHeight + rowCount * entryHeight + entryHeight * 0.5f;

        // Bottom-anchored to the weapon list rather than given a top of its own, so the gap
        // between the two blocks stays put whatever this one contains.
        float panelTop = Screen.height * WeaponPanelTopFraction - panelHeight - entryHeight * 0.5f;

        VanillaSkin.Box(new Rect(panelLeft, panelTop, panelWidth, panelHeight));

        float entryLeft = panelLeft + panelPadding;
        float deathLinkLeft = entryLeft + progressColumnWidth + columnGap;
        float headerTop = panelTop + entryHeight * 0.25f;
        float firstEntryTop = panelTop + headerHeight;

        VanillaSkin.Label(new Rect(entryLeft, headerTop, progressColumnWidth, headerHeight),
            ProgressHeader, VanillaSkin.Header);
        VanillaSkin.Label(new Rect(deathLinkLeft, headerTop, deathLinkWidth, headerHeight),
            DeathLinkHeader, VanillaSkin.Header);

        for (int row = 0; row < lines.Count; row++)
        {
            VanillaSkin.Label(new Rect(entryLeft, firstEntryTop + row * entryHeight, progressColumnWidth, entryHeight),
                lines[row], VanillaSkin.Entry);
        }

        for (int row = 0; row < deathLinkLines.Count; row++)
        {
            VanillaSkin.Label(new Rect(deathLinkLeft, firstEntryTop + row * entryHeight, deathLinkWidth, entryHeight),
                deathLinkLines[row], VanillaSkin.Entry);
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
        List<string> lines = ProgressLines(out float entryWidth);

        float panelPadding = Screen.width * PanelPaddingFraction;
        float entryHeight = Screen.height * EntryHeightFraction;
        float headerHeight = entryHeight * 1.5f;

        float panelWidth = entryWidth + panelPadding * 2f;
        float panelHeight = headerHeight + lines.Count * entryHeight + entryHeight * 0.5f;

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

        for (int row = 0; row < lines.Count; row++)
        {
            VanillaSkin.Label(new Rect(entryLeft, firstEntryTop + row * entryHeight, entryWidth, entryHeight),
                lines[row], VanillaSkin.Entry);
        }
    }

    /// <summary>
    /// The counters the DeathLink column is built from. The handler's three numbers plus
    /// whether there is a handler at all, so a reconnect or a toggle rebuilds the lines too.
    /// </summary>
    private readonly struct DeathLinkStamp
    {
        public DeathLinkStamp(bool connected, bool enabled, int perLink, int towardNext, int sent, int screenHeight)
        {
            Connected = connected;
            Enabled = enabled;
            PerLink = perLink;
            TowardNext = towardNext;
            Sent = sent;
            ScreenHeight = screenHeight;
        }

        public bool Connected { get; }
        public bool Enabled { get; }
        public int PerLink { get; }
        public int TowardNext { get; }
        public int Sent { get; }
        public int ScreenHeight { get; }

        public bool Equals(DeathLinkStamp other) =>
            Connected == other.Connected && Enabled == other.Enabled && PerLink == other.PerLink
            && TowardNext == other.TowardNext && Sent == other.Sent && ScreenHeight == other.ScreenHeight;

        public static DeathLinkStamp Now(DeathLinkHandler handler)
        {
            bool connected = ArchipelagoClient.Authenticated && handler != null;
            return new DeathLinkStamp(
                connected,
                connected && handler.DeathLinkEnabled,
                connected ? handler.DeathsPerLink : 0,
                connected ? handler.DeathsTowardNextLink : 0,
                connected ? handler.DeathLinksSent : 0,
                Screen.height);
        }
    }

    private static DeathLinkStamp deathLinkStamp;
    private static bool deathLinkCached;
    private static readonly List<string> deathLinkLinesCache = new();
    private static float deathLinkWidthCache;

    /// <summary>
    /// The second column of the paused progress box: how close the local player is to sending
    /// their next death out to the multiworld. The counter is the handler's own, so what is shown
    /// is exactly what decides the next send, and the handler only exists from a successful login
    /// onwards. Rebuilt only when <see cref="DeathLinkStamp"/> changes.
    /// </summary>
    /// <param name="width">The widest of the lines and the header, in pixels.</param>
    private static List<string> DeathLinkLines(out float width)
    {
        DeathLinkStamp stamp = DeathLinkStamp.Now(Plugin.ArchipelagoClient?.DeathLinkHandler);
        if (deathLinkCached && stamp.Equals(deathLinkStamp))
        {
            width = deathLinkWidthCache;
            return deathLinkLinesCache;
        }

        deathLinkStamp = stamp;
        deathLinkCached = true;
        deathLinkLinesCache.Clear();

        if (!stamp.Connected)
        {
            // Not "off": offline the value in ServerData is only the apworld's default, or the
            // last room's answer still sitting there.
            deathLinkLinesCache.Add("Not connected to a room");
        }
        else if (!stamp.Enabled)
        {
            deathLinkLinesCache.Add("Off for this slot");
        }
        else
        {
            deathLinkLinesCache.Add(stamp.PerLink == 1
                ? "On: every death is shared"
                : $"On: 1 death shared per {stamp.PerLink}");

            // Shown as a fraction so it reads the same way as the weapons line beside it. The
            // death that would complete the fraction is the one that is sent, so it resets to 0
            // rather than ever displaying full.
            deathLinkLinesCache.Add($"Deaths toward next link: {stamp.TowardNext} / {stamp.PerLink}");
            deathLinkLinesCache.Add($"Deaths shared this session: {stamp.Sent}");
        }

        deathLinkWidthCache = Mathf.Max(
            VanillaSkin.MeasureWidest(deathLinkLinesCache, VanillaSkin.Entry),
            VanillaSkin.MeasureWidth(DeathLinkHeader, VanillaSkin.Header));

        width = deathLinkWidthCache;
        return deathLinkLinesCache;
    }
}
