using System;
using Straftapelago.Finnegan_McD.org.Patches;

namespace Straftapelago.Finnegan_McD.org.Archipelago;

/// <summary>
/// Where a completed check leaves this mod for the Archipelago room.
/// </summary>
/// <remarks>
/// Nothing is wired to Archipelago yet, by choice - see the todo list. This is the seam that
/// wiring will go through: <see cref="RouletteState.RecordKill"/> already decides WHEN a check
/// is earned (the first kill with a weapon the player has unlocked, suicides excluded), so all
/// that is left for the real implementation is turning the weapon name into a location id and
/// handing it to <see cref="Plugin.ArchipelagoClient"/>. Until then it announces the check in
/// the kill feed, which is enough to see the rule working in a live match.
/// </remarks>
public static class LocationSender
{
    /// <param name="weapon_name">
    /// The weapon the player just got their first kill with, as the game names it
    /// (ItemBehaviour.weaponName) - which is what the location names will have to be keyed on.
    /// </param>
    public static void Send_Location(string weapon_name)
    {
        try
        {
            // KillFeed rather than Utils.Killfeed: this one writes through MatchLogs when a
            // match is networked and MatchLogsOffline when it is not, while Utils.Killfeed
            // only ever reaches MatchLogsOffline and would queue forever in an online match.
            KillFeed.Write("Archipelago",
                $"{KillFeed.LocalPlayerName} got a kill for the first time with {weapon_name}");
        }
        catch (Exception error)
        {
            // This is reached from the kill path. A failure to announce a check must never
            // cost the kill it came from.
            Plugin.BepinLogger.LogError($"[Archipelago] Send_Location('{weapon_name}') failed: {error}");
        }
    }
}
