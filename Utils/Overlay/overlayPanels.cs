using System.Collections.Generic;
using Straftapelago.Finnegan_McD.org.Archipelago;
using Straftapelago.Finnegan_McD.org.Patches;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// The three panels <see cref="ArchipelagoOverlay"/> draws: the countdown boxes, the session
/// progress block, and the unlocked-weapon list. Every size is a fraction of the screen rather
/// than a pixel count, so the panels hold their proportions at any resolution.
/// </summary>
internal static class OverlayPanels
{
    // Shared by the two paused panels, which are stacked: the progress block sits directly
    // above WeaponPanelTopFraction, where the weapon list starts.
    private const float PanelLeftFraction = 0.02f;
    private const float WeaponPanelTopFraction = 0.22f;

    /// <summary>
    /// How far down the countdown boxes sit. Well above where the higher of the two paused
    /// panels starts, since a countdown does not stop for the pause menu.
    /// </summary>
    private const float MetronomePanelTopFraction = 0.02f;

    /// <summary>
    /// How far in the countdown boxes sit, in place of <see cref="PanelLeftFraction"/>. Move
    /// this one number to move both boxes.
    /// </summary>
    private const float CountdownPanelLeftFraction = 0.07f;
    private const float PanelPaddingFraction = 0.006f;
    private const float EntryHeightFraction = 0.022f;

    /// <summary>
    /// How opaque the countdown box's backdrop is. Lower than the other two because it is the
    /// only panel drawn over live gameplay.
    /// </summary>
    private const float CountdownPanelAlpha = 0.75f;

    /// <summary>Gap between the progress box's two columns. Matches the weapon list's own.</summary>
    private const float ColumnGapFraction = 0.008f;

    /// <summary>
    /// The Metronome and Made in Heaven countdowns, in the top left corner while one is
    /// running. Made in Heaven takes the top slot when it is up, because it outranks the trap.
    /// </summary>
    internal static void DrawCountdowns()
    {
        int slot = 0;
        if (MadeInHeaven.Running)
        {
            DrawCountdownPanel(slot++, "Made in Heaven", MadeInHeaven.SecondsRemaining);
        }

        DrawCountdownPanel(slot, "Metronome", MetronomeTrap.SecondsRemaining);
    }

    /// <summary>
    /// One countdown box, <paramref name="slot"/> boxes down from the top left corner. Draws
    /// nothing at all when there is no time on the clock, which is what lets the caller ask for
    /// both and get only the ones that are running.
    /// </summary>
    private static void DrawCountdownPanel(int slot, string label, float secondsRemaining)
    {
        if (secondsRemaining <= 0f) return;

        float panelLeft = Screen.width * CountdownPanelLeftFraction;
        float panelPadding = Screen.width * PanelPaddingFraction;
        float entryHeight = Screen.height * EntryHeightFraction;
        float headerHeight = entryHeight * 1.5f;

        // Ceiling rather than rounding, so the last second is shown as 1 for the whole of
        // itself and the box goes away on 0 instead of sitting there reading zero.
        string timeText = $"{Mathf.CeilToInt(secondsRemaining)}s left";

        // Sized to the two lines rather than to a fraction of the screen. The label is the
        // wider of the two in every case, so the box does not breathe as the digits drop.
        float entryWidth = Mathf.Max(
            VanillaSkin.MeasureWidth(label, VanillaSkin.Header),
            VanillaSkin.MeasureWidth(timeText, VanillaSkin.Entry));
        float panelWidth = entryWidth + panelPadding * 2f;
        float panelHeight = headerHeight + entryHeight + entryHeight * 0.5f;

        // Every box is the same height, so a slot is that height plus a gap.
        float panelTop = Screen.height * MetronomePanelTopFraction
            + slot * (panelHeight + entryHeight * 0.4f);

        VanillaSkin.Box(new Rect(panelLeft, panelTop, panelWidth, panelHeight), CountdownPanelAlpha);

        float entryLeft = panelLeft + panelPadding;

        VanillaSkin.Label(new Rect(entryLeft, panelTop + entryHeight * 0.25f, entryWidth, headerHeight),
            label, VanillaSkin.Header);
        VanillaSkin.Label(new Rect(entryLeft, panelTop + headerHeight, entryWidth, entryHeight),
            timeText, VanillaSkin.Entry);
    }

    /// <summary>
    /// The numbers that say how the run is going, in a block directly above the weapon list.
    /// Left column: takes won, how much of the weapon roster has been earned, and rounds won.
    /// Right column: the DeathLink counter. Takes and rounds are this session only, since
    /// vanilla accumulates neither across matches; weapons earned is the seed's progress and
    /// survives restarts. The first two lines are the room's two goals, so each carries its
    /// threshold and takes a tick once <see cref="GoalTracker"/> says that goal is met.
    /// </summary>
    internal static void DrawSessionProgress()
    {
        float panelLeft = Screen.width * PanelLeftFraction;
        float panelPadding = Screen.width * PanelPaddingFraction;
        float entryHeight = Screen.height * EntryHeightFraction;
        float headerHeight = entryHeight * 1.5f;

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

        List<string> deathLinkLines = DeathLinkLines(showGoals);

        // The tick is folded in here rather than at draw time, because the column is measured
        // against these strings and a tick added afterwards would be a character the box was
        // never sized for.
        var progressLines = new List<string>(lines.Count);
        foreach ((bool achieved, string text) in lines)
        {
            progressLines.Add(achieved ? $"{text} ✓" : text);
        }

        // Each column is as wide as its own widest line, headers included.
        float columnGap = Screen.width * ColumnGapFraction;
        float progressWidth = Mathf.Max(
            VanillaSkin.MeasureWidest(progressLines, VanillaSkin.Entry),
            VanillaSkin.MeasureWidth("Progress", VanillaSkin.Header));
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
            "Progress", VanillaSkin.Header);
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
    /// The second column of the progress box: how close the local player is to sending their
    /// next death out to the multiworld. The counter is the handler's own, so what is shown is
    /// exactly what decides the next send, and the handler only exists from a successful login
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

    /// <summary>
    /// The local player's unlocked weapons, under the progress block: the ones still waiting
    /// for their first kill at the top, then the ones that earned their check with a tick.
    /// Confined to the left half of the screen so it cannot cover the pause menu.
    /// </summary>
    internal static void DrawObtainedWeapons()
    {
        RouletteState roulette = Plugin.RouletteState;
        if (roulette == null) return;

        var obtained = new List<GameObject>(roulette.obtained_Items);
        int firstKillEarned = obtained.Count;
        obtained.AddRange(roulette.hasKill_Items);

        float panelLeft = Screen.width * PanelLeftFraction;
        float panelTop = Screen.height * WeaponPanelTopFraction;
        float panelPadding = Screen.width * PanelPaddingFraction;
        float entryHeight = Screen.height * EntryHeightFraction;
        float headerHeight = entryHeight * 1.5f;

        // Every line is built up front, before anything is measured or placed, because the
        // column width comes from the longest of them.
        var entryTexts = new List<string>(obtained.Count);
        for (int i = 0; i < obtained.Count; i++)
        {
            GameObject weapon = obtained[i];

            // U+2713. If a future Unity build's default GUI font does not carry it the entry
            // shows a box, in which case swap this for a plain "*".
            string killMark = i >= firstKillEarned ? " ✓" : "";

            // DisplayNameOf, not weapon.name: the pool is keyed on prefab names, several of
            // which are nothing like what the game calls the weapon on screen.
            entryTexts.Add(
                $"{i + 1}. {(weapon == null ? "<missing>" : RouletteState.DisplayNameOf(weapon))}{killMark}");
        }

        // Packed to the width the longest weapon line actually needs, which is what buys a
        // third column inside the same left-half budget.
        float columnGap = Screen.width * ColumnGapFraction;
        float entryWidth = VanillaSkin.MeasureWidest(entryTexts, VanillaSkin.Entry);
        float columnStride = entryWidth + columnGap;

        float maxPanelWidth = Screen.width * 0.5f - panelLeft;
        float maxColumnsWidth = maxPanelWidth - panelPadding * 2f + columnGap;
        int entriesPerColumn = Mathf.Max(1, (int)((Screen.height * 0.68f - headerHeight) / entryHeight));

        // A single column that overruns the budget on its own is clamped to it rather than
        // dropped: one wide line must not leave the panel with no columns at all.
        columnStride = Mathf.Min(columnStride, maxColumnsWidth);
        entryWidth = columnStride - columnGap;

        int maxColumns = Mathf.Max(1, (int)(maxColumnsWidth / columnStride));

        // Hard cap: with 71 weapons in the game the full list can outgrow even the columns, and
        // a panel that runs off the screen is worse than one that says how much it is hiding.
        // The "... and N more" line costs an entry, so it comes out of the capacity.
        int capacity = entriesPerColumn * maxColumns;
        bool truncated = obtained.Count > capacity;
        int weaponCount = truncated ? capacity - 1 : obtained.Count;
        int entryCount = weaponCount + (truncated ? 1 : 0);

        int columnCount = Mathf.Max(1, Mathf.CeilToInt(entryCount / (float)entriesPerColumn));
        int rowCount = Mathf.Min(Mathf.Max(entryCount, 1), entriesPerColumn);

        string header = $"Unlocked weapons ({obtained.Count})";

        // The header can be wider than a single narrow column, so it gets a say in the panel
        // width rather than being clipped by it, but never past the left-half budget.
        float panelWidth = Mathf.Min(maxPanelWidth, Mathf.Max(
            columnCount * columnStride - columnGap + panelPadding * 2f,
            VanillaSkin.MeasureWidth(header, VanillaSkin.Header) + panelPadding * 2f));
        float panelHeight = headerHeight + rowCount * entryHeight + entryHeight * 0.5f;

        VanillaSkin.Box(new Rect(panelLeft, panelTop, panelWidth, panelHeight));

        float firstColumnLeft = panelLeft + panelPadding;
        float firstEntryTop = panelTop + headerHeight;

        VanillaSkin.Label(new Rect(firstColumnLeft, panelTop + entryHeight * 0.25f,
            panelWidth - panelPadding * 2f, headerHeight), header, VanillaSkin.Header);

        for (int i = 0; i < entryCount; i++)
        {
            // Fill each column top to bottom before starting the next one, so the numbering
            // reads down a column.
            float entryLeft = firstColumnLeft + i / entriesPerColumn * columnStride;
            float entryTop = firstEntryTop + i % entriesPerColumn * entryHeight;

            string text = truncated && i == entryCount - 1
                ? $"... and {obtained.Count - weaponCount} more"
                : entryTexts[i];

            VanillaSkin.Label(new Rect(entryLeft, entryTop, entryWidth, entryHeight), text,
                VanillaSkin.Entry);
        }
    }
}
