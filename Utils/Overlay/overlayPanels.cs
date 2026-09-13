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
    /// <summary>
    /// The list's lines and measurements, rebuilt only when the pools or the screen change.
    /// Building them means a display-name lookup and a CalcSize per weapon, and this panel is
    /// drawn every repaint the pause menu is up.
    /// </summary>
    private static readonly List<string> weaponEntryTexts = new();
    private static int weaponEntriesVersion = -1;
    private static int weaponEntriesScreenHeight = -1;
    private static float weaponEntryWidth;
    private static float weaponHeaderWidth;
    private static string weaponHeader = "";

    private static void RefreshWeaponEntries(RouletteState roulette)
    {
        if (roulette.Version == weaponEntriesVersion && Screen.height == weaponEntriesScreenHeight) return;

        weaponEntriesVersion = roulette.Version;
        weaponEntriesScreenHeight = Screen.height;
        weaponEntryTexts.Clear();

        int firstKillEarned = roulette.obtained_Items.Count;
        AppendWeaponEntries(roulette.obtained_Items, firstKillEarned);
        AppendWeaponEntries(roulette.hasKill_Items, firstKillEarned);

        weaponHeader = $"Unlocked weapons ({weaponEntryTexts.Count})";
        weaponEntryWidth = VanillaSkin.MeasureWidest(weaponEntryTexts, VanillaSkin.Entry);
        weaponHeaderWidth = VanillaSkin.MeasureWidth(weaponHeader, VanillaSkin.Header);
    }

    /// <summary>
    /// One numbered line per weapon, continuing the numbering from whatever is already in the
    /// list, with a tick on every line at or past <paramref name="firstKillEarned"/>.
    /// </summary>
    private static void AppendWeaponEntries(List<GameObject> weapons, int firstKillEarned)
    {
        foreach (GameObject weapon in weapons)
        {
            int position = weaponEntryTexts.Count;

            // U+2713. If a future Unity build's default GUI font does not carry it the entry
            // shows a box, in which case swap this for a plain "*".
            string killMark = position >= firstKillEarned ? " ✓" : "";

            // DisplayNameOf, not weapon.name: the pool is keyed on prefab names, several of
            // which are nothing like what the game calls the weapon on screen.
            weaponEntryTexts.Add(
                $"{position + 1}. {(weapon == null ? "<missing>" : RouletteState.DisplayNameOf(weapon))}{killMark}");
        }
    }

    internal static void DrawObtainedWeapons()
    {
        RouletteState roulette = Plugin.RouletteState;
        if (roulette == null) return;

        RefreshWeaponEntries(roulette);
        List<string> entryTexts = weaponEntryTexts;
        int weaponTotal = entryTexts.Count;

        float panelLeft = Screen.width * PanelLeftFraction;
        float panelTop = Screen.height * WeaponPanelTopFraction;
        float panelPadding = Screen.width * PanelPaddingFraction;
        float entryHeight = Screen.height * EntryHeightFraction;
        float headerHeight = entryHeight * 1.5f;

        // Packed to the width the longest weapon line actually needs, which is what buys a
        // third column inside the same left-half budget.
        float columnGap = Screen.width * ColumnGapFraction;
        float entryWidth = weaponEntryWidth;
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
        bool truncated = weaponTotal > capacity;
        int weaponCount = truncated ? capacity - 1 : weaponTotal;
        int entryCount = weaponCount + (truncated ? 1 : 0);

        int columnCount = Mathf.Max(1, Mathf.CeilToInt(entryCount / (float)entriesPerColumn));
        int rowCount = Mathf.Min(Mathf.Max(entryCount, 1), entriesPerColumn);

        // The header can be wider than a single narrow column, so it gets a say in the panel
        // width rather than being clipped by it, but never past the left-half budget.
        float panelWidth = Mathf.Min(maxPanelWidth, Mathf.Max(
            columnCount * columnStride - columnGap + panelPadding * 2f,
            weaponHeaderWidth + panelPadding * 2f));
        float panelHeight = headerHeight + rowCount * entryHeight + entryHeight * 0.5f;

        VanillaSkin.Box(new Rect(panelLeft, panelTop, panelWidth, panelHeight));

        float firstColumnLeft = panelLeft + panelPadding;
        float firstEntryTop = panelTop + headerHeight;

        VanillaSkin.Label(new Rect(firstColumnLeft, panelTop + entryHeight * 0.25f,
            panelWidth - panelPadding * 2f, headerHeight), weaponHeader, VanillaSkin.Header);

        for (int entry = 0; entry < entryCount; entry++)
        {
            // Fill each column top to bottom before starting the next one, so the numbering
            // reads down a column.
            float entryLeft = firstColumnLeft + entry / entriesPerColumn * columnStride;
            float entryTop = firstEntryTop + entry % entriesPerColumn * entryHeight;

            string text = truncated && entry == entryCount - 1
                ? $"... and {weaponTotal - weaponCount} more"
                : entryTexts[entry];

            VanillaSkin.Label(new Rect(entryLeft, entryTop, entryWidth, entryHeight), text,
                VanillaSkin.Entry);
        }
    }
}
