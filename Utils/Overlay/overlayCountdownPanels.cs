using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

// The row of small boxes along the top of the screen, drawn over live gameplay: the two
// countdowns, and the weapon the multiworld unlocked most recently.
internal static partial class OverlayPanels
{
    /// <summary>
    /// One measured string, remeasured only when the text or the screen height changes. Every
    /// box in this row is drawn every repaint, and CalcSize is the expensive part of drawing
    /// one, so each label keeps its own.
    /// </summary>
    private sealed class MeasuredText
    {
        private string text;
        private int screenHeight = -1;
        private float width;

        public float Width(string newText, GUIStyle style)
        {
            if (newText != text || screenHeight != Screen.height)
            {
                text = newText;
                screenHeight = Screen.height;
                width = VanillaSkin.MeasureWidth(newText, style);
            }

            return width;
        }
    }

    /// <summary>
    /// The widest countdown box drawn on the last pass, or zero when none was, so
    /// <see cref="DrawLastWeapon"/> knows where the row currently ends. Written by
    /// <see cref="DrawCountdowns"/>, which is why the overlay calls the two in that order.
    /// </summary>
    private static float countdownRowWidth;

    private const string MadeInHeavenLabel = "Made in Heaven";
    private const string MetronomeLabel = "Metronome";
    private const string LastUnlockedHeader = "Last unlocked";

    private static readonly MeasuredText madeInHeavenLabelWidth = new();
    private static readonly MeasuredText madeInHeavenTimeWidth = new();
    private static readonly MeasuredText metronomeLabelWidth = new();
    private static readonly MeasuredText metronomeTimeWidth = new();
    private static readonly MeasuredText lastUnlockedHeaderWidth = new();
    private static readonly MeasuredText lastUnlockedNameWidth = new();

    /// <summary>The "Ns left" line's last whole-second value, so the string is rebuilt once a second.</summary>
    private static int madeInHeavenSecondsShown = -1;
    private static int metronomeSecondsShown = -1;
    private static string madeInHeavenTimeText = "";
    private static string metronomeTimeText = "";

    /// <summary>
    /// The Metronome and Made in Heaven countdowns, in the top left corner while one is
    /// running. Made in Heaven takes the top slot when it is up, because it outranks the trap.
    /// </summary>
    internal static void DrawCountdowns()
    {
        countdownRowWidth = 0f;

        int slot = 0;
        if (MadeInHeaven.Running)
        {
            countdownRowWidth = Mathf.Max(countdownRowWidth,
                DrawCountdownPanel(slot++, MadeInHeavenLabel, MadeInHeaven.SecondsRemaining,
                    madeInHeavenLabelWidth, madeInHeavenTimeWidth,
                    ref madeInHeavenSecondsShown, ref madeInHeavenTimeText));
        }

        countdownRowWidth = Mathf.Max(countdownRowWidth,
            DrawCountdownPanel(slot, MetronomeLabel, MetronomeTrap.SecondsRemaining,
                metronomeLabelWidth, metronomeTimeWidth,
                ref metronomeSecondsShown, ref metronomeTimeText));
    }

    /// <summary>
    /// One countdown box, <paramref name="slot"/> boxes down from the top left corner. Draws
    /// nothing at all when there is no time on the clock, which is what lets the caller ask for
    /// both and get only the ones that are running.
    /// </summary>
    /// <returns>How wide the box was, or zero when none was drawn.</returns>
    private static float DrawCountdownPanel(int slot, string label, float secondsRemaining,
        MeasuredText labelWidth, MeasuredText timeWidth, ref int secondsShown, ref string timeText)
    {
        if (secondsRemaining <= 0f) return 0f;

        float panelLeft = Screen.width * CountdownPanelLeftFraction;
        float panelPadding = Screen.width * PanelPaddingFraction;
        float entryHeight = Screen.height * EntryHeightFraction;
        float headerHeight = entryHeight * 1.5f;

        // Ceiling rather than rounding, so the last second is shown as 1 for the whole of
        // itself and the box goes away on 0 instead of sitting there reading zero.
        int seconds = Mathf.CeilToInt(secondsRemaining);
        if (seconds != secondsShown)
        {
            secondsShown = seconds;
            timeText = $"{seconds}s left";
        }

        // Sized to the two lines rather than to a fraction of the screen. The label is the
        // wider of the two in every case, so the box does not breathe as the digits drop.
        float entryWidth = Mathf.Max(
            labelWidth.Width(label, VanillaSkin.Header),
            timeWidth.Width(timeText, VanillaSkin.Entry));
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

        return panelWidth;
    }

    /// <summary>
    /// The weapon the multiworld unlocked most recently, in a box beside the countdowns. Drawn
    /// whether or not one is running - what the room last sent is worth reading at any point in
    /// a match - and drawn at all only once there is a weapon to name.
    /// </summary>
    internal static void DrawLastWeapon()
    {
        string weaponName = Plugin.RouletteState?.LastUnlockedWeapon;
        if (string.IsNullOrEmpty(weaponName)) return;

        float panelPadding = Screen.width * PanelPaddingFraction;
        float entryHeight = Screen.height * EntryHeightFraction;
        float headerHeight = entryHeight * 1.5f;

        float entryWidth = Mathf.Max(
            lastUnlockedHeaderWidth.Width(LastUnlockedHeader, VanillaSkin.Header),
            lastUnlockedNameWidth.Width(weaponName, VanillaSkin.Entry));
        float panelWidth = entryWidth + panelPadding * 2f;
        float panelHeight = headerHeight + entryHeight + entryHeight * 0.5f;

        // Where the countdown row ends, plus a gap. With no countdown running that width is
        // zero and this box simply takes the row's own left edge.
        float panelLeft = Screen.width * CountdownPanelLeftFraction
            + (countdownRowWidth > 0f ? countdownRowWidth + Screen.width * ColumnGapFraction : 0f);
        float panelTop = Screen.height * MetronomePanelTopFraction;

        VanillaSkin.Box(new Rect(panelLeft, panelTop, panelWidth, panelHeight), CountdownPanelAlpha);

        float entryLeft = panelLeft + panelPadding;

        VanillaSkin.Label(new Rect(entryLeft, panelTop + entryHeight * 0.25f, entryWidth, headerHeight),
            LastUnlockedHeader, VanillaSkin.Header);
        VanillaSkin.Label(new Rect(entryLeft, panelTop + headerHeight, entryWidth, entryHeight),
            weaponName, VanillaSkin.Entry);
    }
}
