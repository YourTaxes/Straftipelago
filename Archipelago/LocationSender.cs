using System;
using UnityEngine;
using Straftapelago.Finnegan_McD.org.Patches;

namespace Straftapelago.Finnegan_McD.org.Archipelago;

/// <summary>
/// What became of a check the mod tried to hand to the room.
/// </summary>
/// <remarks>
/// A return value rather than a thrown exception because the two callers want opposite
/// things from a failure: <see cref="RouletteState.RecordKill"/> is on the kill path and
/// must swallow it, while /ap_completecheck is a command the player typed and has to tell
/// them why nothing happened.
/// </remarks>
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
/// </summary>
/// <remarks>
/// <see cref="RouletteState.RecordKill"/> decides WHEN a check is earned (the first kill with
/// a weapon the player has unlocked, suicides excluded); this decides what that means to the
/// room. The Straftat apworld gives every weapon one location named after the weapon, so the
/// translation is two steps: the weapon's name to the spelling the apworld uses, which
/// <see cref="LocationNameFor"/> does, and that name to an id, which
/// <see cref="ArchipelagoClient.ResolveLocationId"/> does.
/// </remarks>
public static class LocationSender
{
    /// <summary>
    /// The name the room knows a weapon by.
    /// </summary>
    /// <remarks>
    /// The apworld's LOCATION_NAME_TO_ID is keyed on the game's PREFAB names - "AK-K",
    /// "Nugget", "DF_Blister" - and several of those are nothing like what the game displays
    /// for the same weapon ("ak", "serac"). Sending what the player sees is what made
    /// /ap_completecheck ak report that the room had no such location: it is called "AK-K"
    /// there. Anything the pool can resolve therefore goes out under its prefab's name, which
    /// is the one spelling both halves agree on.
    ///
    /// A name the pool cannot resolve is passed through untouched rather than refused here -
    /// the pool is empty until a player object has come up, and ResolveLocationId is a better
    /// judge of what the room has than a pool that may not be built yet.
    /// </remarks>
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
    /// Sends one check by the name the room knows it under, whatever earned it.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="Send_Location"/> so that a check which is not a weapon - the
    /// Round_N round-win checks - does not have to go through <see cref="LocationNameFor"/>, a
    /// weapon-pool lookup that could only ever fail to resolve it. A name reaching here is already
    /// in the apworld's spelling; the only translation left is the id.
    /// </remarks>
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
                // The mod and the apworld disagree about a name. Worth a loud line: it means that
                // check can never be sent, and no amount of playing will fix it. The name is
                // printed because the fix is to make the apworld's LOCATION_NAME_TO_ID agree with
                // whatever this mod sends.
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
