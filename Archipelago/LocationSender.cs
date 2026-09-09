using System;
using UnityEngine;
using Straftapelago.Finnegan_McD.org.Patches;

namespace Straftapelago.Finnegan_McD.org.Archipelago;

/// <summary>
/// What became of a check the mod tried to hand to the room. A return value rather than a
/// thrown exception, because the two callers want opposite things from a failure:
/// <see cref="RouletteState.RecordKill"/> is on the kill path and must swallow it, while
/// /ap_completecheck has to tell the player why nothing happened.
/// </summary>
public enum LocationSendResult
{
    /// <summary>The check went to the room.</summary>
    Sent,

    /// <summary>This session had already sent it; the room does not need it twice.</summary>
    AlreadySent,

    /// <summary>No session to send it over. The check is lost - nothing queues it for later.</summary>
    NotConnected,

    /// <summary>The room's datapackage has no location by that name.</summary>
    UnknownLocation,

    /// <summary>The send itself threw. See the log.</summary>
    Failed,
}

/// <summary>
/// Where a completed check leaves this mod for the Archipelago room.
/// <see cref="RouletteState.RecordKill"/> decides when a check is earned; this decides what that
/// means to the room. The apworld gives every weapon one location named after it, so the
/// translation is two steps: the weapon's name to the spelling the apworld uses
/// (<see cref="LocationNameFor"/>), and that name to an id
/// (<see cref="ArchipelagoClient.ResolveLocationId"/>).
/// </summary>
public static class LocationSender
{
    /// <summary>
    /// The name the room knows a weapon by. The apworld's LOCATION_NAME_TO_ID is keyed on the
    /// game's PREFAB names - "AK-K", "Nugget", "DF_Blister" - several of which are nothing like
    /// what the game displays, so anything the pool can resolve goes out under its prefab's
    /// name. A name the pool cannot resolve is passed through untouched, since the pool is empty
    /// until a player object has come up and ResolveLocationId is the better judge.
    /// </summary>
    private static string LocationNameFor(string weaponName)
    {
        GameObject prefab = Plugin.RouletteState?.ResolveByAnyName(weaponName);
        return prefab == null ? weaponName : prefab.name;
    }

    /// <param name="weapon_name">
    /// The weapon the player just got their first kill with. Either namespace works - the
    /// prefab name or the name the game displays - because <see cref="LocationNameFor"/>
    /// puts it into the apworld's spelling before it is looked up.
    /// </param>
    /// <returns>What happened, for a caller that wants to report it.</returns>
    public static LocationSendResult Send_Location(string weapon_name)
    {
        try
        {
            // KillFeed rather than Utils.Killfeed: this one writes through MatchLogs when a
            // match is networked and MatchLogsOffline when it is not, while Utils.Killfeed
            // only ever reaches MatchLogsOffline and would queue forever in an online match.
            KillFeed.Write("Archipelago",
                $"{KillFeed.LocalPlayerName} got a kill for the first time with {weapon_name}");

            return SendByLocationName(LocationNameFor(weapon_name), $"first kill with '{weapon_name}'");
        }
        catch (Exception error)
        {
            // This is reached from the kill path. A failure to send a check must never cost
            // the kill it came from.
            Plugin.BepinLogger.LogError($"[Archipelago] Send_Location('{weapon_name}') failed: {error}");
            return LocationSendResult.Failed;
        }
    }

    /// <summary>
    /// Sends one check by the name the room knows it under, whatever earned it. A name reaching
    /// here is already in the apworld's spelling - which is what lets the Round_N checks skip
    /// the weapon-pool lookup in <see cref="LocationNameFor"/> - so the only translation left is
    /// the id.
    /// </summary>
    /// <param name="locationName">The location's name as the apworld spells it.</param>
    /// <param name="earnedBy">
    /// What earned the check, for the log lines. Free text - "first kill with 'AK-K'", "round 3
    /// won" - and never parsed.
    /// </param>
    public static LocationSendResult SendByLocationName(string locationName, string earnedBy)
    {
        try
        {
            ArchipelagoClient client = Plugin.ArchipelagoClient;
            if (client == null || !ArchipelagoClient.Authenticated)
            {
                // Logged but not queued: a check earned while offline has no session to belong
                // to, and the local state that came with it is not something a later connect
                // replays.
                Plugin.BepinLogger.LogWarning(
                    $"[Archipelago] {earnedBy} earned a check, but there is no room to send it to.");
                return LocationSendResult.NotConnected;
            }

            long locationId = client.ResolveLocationId(locationName);
            if (locationId < 0)
            {
                // The mod and the apworld disagree about a name, so that check can never be
                // sent. The name is printed because the fix is to make the apworld's
                // LOCATION_NAME_TO_ID agree with what the mod sends.
                Plugin.BepinLogger.LogWarning(
                    $"[Archipelago] the room has no location named '{locationName}', so the check " +
                    $"for {earnedBy} cannot be sent. This mod and the apworld disagree about the " +
                    "location's name.");
                return LocationSendResult.UnknownLocation;
            }

            if (ArchipelagoClient.ServerData.CheckedLocations.Contains(locationId))
            {
                return LocationSendResult.AlreadySent;
            }

            client.SendLocationCheck(locationId);

            // After the send, not before: this list is what a reconnect replays, and a
            // location that never reached the socket has no business in it.
            ArchipelagoClient.ServerData.CheckedLocations.Add(locationId);

            Plugin.BepinLogger.LogInfo(
                $"[Archipelago] sent the check for {earnedBy} as location '{locationName}' (id {locationId}).");
            return LocationSendResult.Sent;
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError(
                $"[Archipelago] SendByLocationName('{locationName}') failed: {error}");
            return LocationSendResult.Failed;
        }
    }
}
