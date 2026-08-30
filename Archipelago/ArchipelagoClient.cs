using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.MessageLog.Messages;
using Archipelago.MultiClient.Net.Packets;
using Straftapelago.Finnegan_McD.org.Utils;

namespace Straftapelago.Finnegan_McD.org.Archipelago;

public class ArchipelagoClient
{
    public const string APVersion = "0.5.0";
    private const string Game = "Straftat";

    public static bool Authenticated;
    private bool attemptingConnection;

    public static ArchipelagoData ServerData = new();
    // Public because the two PlayerHealth.Update patches in deathLinkPatches reach it as
    // Plugin.ArchipelagoClient?.DeathLinkHandler - one to report a local death, one to pump the
    // received queue. Null until a successful login, which is why both of them null-check it.
    public DeathLinkHandler DeathLinkHandler;
    private ArchipelagoSession session;

    /// <summary>
    /// call to connect to an Archipelago session. Connection info should already be set up on ServerData
    /// </summary>
    /// <returns></returns>
    public void Connect()
    {
        if (Authenticated || attemptingConnection) return;

        try
        {
            session = ArchipelagoSessionFactory.CreateSession(ServerData.Uri);
            SetupSession();
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError(e);
        }

        // CreateSession throws on an address it cannot parse, which the catch above logs -
        // but it leaves session null, and TryConnect dereferences it inside a ThreadPool work
        // item where its own try/catch cannot see the throw. Report it here instead.
        if (session == null)
        {
            ArchipelagoConsole.LogMessage($"Could not open a session for '{ServerData.Uri}'. Check the Host field.");
            return;
        }

        // Set before TryConnect, and cleared by HandleConnectResult on both outcomes. Without
        // it the guard above never fired and every press of Connect started another session
        // over the top of the one already in flight.
        attemptingConnection = true;
        TryConnect();
    }

    /// <summary>
    /// add handlers for Archipelago events
    /// </summary>
    private void SetupSession()
    {
        session.MessageLog.OnMessageReceived += message => ArchipelagoConsole.LogMessage(message.ToString());
        session.Items.ItemReceived += OnItemReceived;
        session.Socket.ErrorReceived += OnSessionErrorReceived;
        session.Socket.SocketClosed += OnSessionSocketClosed;
    }

    /// <summary>
    /// attempt to connect to the server with our connection info
    /// </summary>
    private void TryConnect()
    {
        try
        {
            // it's safe to thread this function call but unity notoriously hates threading so do not use excessively
            ThreadPool.QueueUserWorkItem(
                _ => HandleConnectResult(
                    session.TryConnectAndLogin(
                        Game,
                        ServerData.SlotName,
                        ItemsHandlingFlags.IncludeOwnItems,
                        new Version(APVersion),
                        password: ServerData.Password,
                        requestSlotData: ServerData.NeedSlotData
                    )));
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError(e);
            HandleConnectResult(new LoginFailure(e.ToString()));
            attemptingConnection = false;
        }
    }

    /// <summary>
    /// handle the connection result and do things
    /// </summary>
    /// <param name="result"></param>
    private void HandleConnectResult(LoginResult result)
    {
        string outText;
        if (result.Successful)
        {
            var success = (LoginSuccessful)result;

            ServerData.SetupSession(success.SlotData, session.RoomState.Seed);
            Authenticated = true;

            // After SetupSession above, which is what reads death_link out of the slot data the
            // login just returned. Constructed with it rather than toggled afterwards, so the
            // service is subscribed on the server side before the first frame can report a death.
            DeathLinkHandler = new(session.CreateDeathLinkService(), ServerData.SlotName, ServerData.DeathLink);
            session.Locations.CompleteLocationChecksAsync(ServerData.CheckedLocations.ToArray());
            outText = $"Successfully connected to {ServerData.Uri} as {ServerData.SlotName}!";

            ArchipelagoConsole.LogMessage(outText);

            // The room's answer to this slot's YAML, printed once the login that carried it has
            // succeeded. One call per line because each one becomes its own chat message.
            foreach (string line in ServerData.DescribeSlotData())
                ArchipelagoConsole.LogMessage(line);
        }
        else
        {
            var failure = (LoginFailure)result;
            outText = $"Failed to connect to {ServerData.Uri} as {ServerData.SlotName}.";
            outText = failure.Errors.Aggregate(outText, (current, error) => current + $"\n    {error}");

            Plugin.BepinLogger.LogError(outText);

            Authenticated = false;
            Disconnect();

            ArchipelagoConsole.LogMessage(outText);
        }

        attemptingConnection = false;
    }

    /// <summary>
    /// something went wrong, or we need to properly disconnect from the server. cleanup and re null our session
    /// </summary>
    /// <remarks>
    /// Public because the Mod Menu page's Disconnect button calls it (see
    /// <see cref="ArchipelagoMenu"/>); the failure and socket-closed paths below still use it
    /// the same way they always did.
    /// </remarks>
    public void Disconnect()
    {
        Plugin.BepinLogger.LogDebug("disconnecting from server...");
        session?.Socket.DisconnectAsync();
        session = null;
        locationIdsByName = null;
        Authenticated = false;
    }

    public void SendMessage(string message)
    {
        session.Socket.SendPacketAsync(new SayPacket { Text = message });
    }

    /// <summary>
    /// Location names as the room's datapackage spells them, keyed case-insensitively. Built
    /// on first use and dropped on disconnect, because the next session's datapackage is the
    /// next session's business.
    /// </summary>
    private Dictionary<string, long> locationIdsByName;

    /// <summary>
    /// Turns a location name into the id the server wants, or -1 when there is no session or
    /// the room has no such location.
    /// </summary>
    /// <remarks>
    /// The exact lookup is tried first because it costs nothing. The fallback exists because
    /// the names this mod has to work from are the game's, not the apworld's: the pools are
    /// keyed on prefab names ("glock") while the locations are display names ("Glock"), and
    /// a player typing a weapon into /ap_completecheck will not match the datapackage's
    /// casing either. Only casing is forgiven - a name that differs by spacing or spelling is
    /// a genuine mismatch between the mod and the apworld and should be reported, not guessed
    /// around.
    /// </remarks>
    public long ResolveLocationId(string locationName)
    {
        if (session == null || string.IsNullOrEmpty(locationName)) return -1;

        string trimmed = locationName.Trim();
        long exact = session.Locations.GetLocationIdFromName(Game, trimmed);
        if (exact >= 0) return exact;

        if (locationIdsByName == null)
        {
            locationIdsByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (long locationId in session.Locations.AllLocations)
            {
                string name = session.Locations.GetLocationNameFromId(locationId, Game);
                if (!string.IsNullOrEmpty(name)) locationIdsByName[name] = locationId;
            }
        }

        return locationIdsByName.TryGetValue(trimmed, out long resolved) ? resolved : -1;
    }

    /// <summary>
    /// Tells the room this location has been checked.
    /// </summary>
    /// <remarks>
    /// Async because this is called from the kill path, which is inside a Harmony postfix on
    /// the frame a player died: the send must not park the game thread on the socket. The
    /// server's own acknowledgement comes back through the MessageLog like any other room
    /// message, so nothing here has to wait for it.
    /// </remarks>
    public void SendLocationCheck(long locationId)
    {
        session.Locations.CompleteLocationChecksAsync(new[] { locationId });
    }

    /// <summary>
    /// we received an item so reward it here
    /// </summary>
    /// <param name="helper">item helper which we can grab our item from</param>
    private void OnItemReceived(ReceivedItemsHelper helper)
    {
        var receivedItem = helper.DequeueItem();

        if (helper.Index <= ServerData.Index) return;

        ServerData.Index++;

        // ArchipelagoConsole, not KillFeed: this callback runs on the client's websocket
        // thread, and KillFeed.Write instantiates a chat line through MatchLogs straight
        // away - a Unity API call off the main thread. LogMessage queues instead, and
        // ArchipelagoOverlay.Update drains it on the main thread. It is also where the rest
        // of this class already reports, because a received item is the room talking.
        ArchipelagoConsole.LogMessage(
            $"RECIEVED {receivedItem.ItemDisplayName} (item id {receivedItem.ItemId}, {receivedItem.Flags}) " +
            $"FROM {receivedItem.Player} playing {receivedItem.ItemGame} " +
            $"AT {receivedItem.LocationDisplayName} (location id {receivedItem.LocationId})");
        // TODO reward the item here
        // if items can be received while in an invalid state for actually handling them, they can be placed in a local
        // queue/collection to be handled later
    }

    /// <summary>
    /// something went wrong with our socket connection
    /// </summary>
    /// <param name="e">thrown exception from our socket</param>
    /// <param name="message">message received from the server</param>
    private void OnSessionErrorReceived(Exception e, string message)
    {
        Plugin.BepinLogger.LogError(e);
        ArchipelagoConsole.LogMessage(message);
    }

    /// <summary>
    /// something went wrong closing our connection. disconnect and clean up
    /// </summary>
    /// <param name="reason"></param>
    private void OnSessionSocketClosed(string reason)
    {
        Plugin.BepinLogger.LogError($"Connection to Archipelago lost: {reason}");
        Disconnect();
    }
}