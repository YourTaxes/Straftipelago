using System;
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
/// room. The location names are the weapon names as the apworld spells them - the Straftat
/// apworld gives every weapon one location, named after the weapon - so the only translation
/// needed is name to id, which <see cref="ArchipelagoClient.ResolveLocationId"/> does.
/// </remarks>
public static class LocationSender
{
    /// <param name="weapon_name">
    /// The weapon the player just got their first kill with, as the game names it
    /// (ItemBehaviour.weaponName), which is what the apworld's location names are keyed on.
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

            ArchipelagoClient client = Plugin.ArchipelagoClient;
            if (client == null || !ArchipelagoClient.Authenticated)
            {
                // Announced above and logged here, but not queued: a check earned while
                // offline has no session to belong to, and the pool move that came with it is
                // local state that a later connect does not replay.
                Plugin.BepinLogger.LogWarning(
                    $"[Archipelago] first kill with '{weapon_name}' earned a check, but there is " +
                    "no room to send it to.");
                return LocationSendResult.NotConnected;
            }

            long locationId = client.ResolveLocationId(weapon_name);
            if (locationId < 0)
            {
                // The mod and the apworld disagree about a weapon's name. Worth a loud line:
                // it means that weapon's check can never be sent, and no amount of playing
                // will fix it.
                Plugin.BepinLogger.LogWarning(
                    $"[Archipelago] the room has no location named '{weapon_name}', so its check " +
                    "cannot be sent. The mod's weapon name and the apworld's location name differ.");
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
                $"[Archipelago] sent the check for '{weapon_name}' (location id {locationId}).");
            return LocationSendResult.Sent;
        }
        catch (Exception error)
        {
            // This is reached from the kill path. A failure to send a check must never cost
            // the kill it came from.
            Plugin.BepinLogger.LogError($"[Archipelago] Send_Location('{weapon_name}') failed: {error}");
            return LocationSendResult.Failed;
        }
    }
}
