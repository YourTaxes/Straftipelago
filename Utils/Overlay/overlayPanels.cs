using System.Collections.Generic;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// The panels <see cref="ArchipelagoOverlay"/> draws, and the sizes they share. Every size is a
/// fraction of the screen rather than a pixel count, so the panels hold their proportions at any
/// resolution. The countdown row is in overlayCountdownPanels.cs and the progress boxes are in
/// overlayProgressPanel.cs; this file keeps the shared fractions and the weapon list.
/// </summary>
internal static partial class OverlayPanels
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
    /// How opaque a backdrop drawn over live gameplay is. Lower than the paused panels', since
    /// those cover a screen the player is already reading rather than a fight.
    /// </summary>
    private const float CountdownPanelAlpha = 0.75f;

    /// <summary>Gap between the progress box's two columns. Matches the weapon list's own.</summary>
    private const float ColumnGapFraction = 0.008f;

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
