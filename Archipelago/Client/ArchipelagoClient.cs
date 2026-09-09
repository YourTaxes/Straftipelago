using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Packets;
using Straftapelago.Finnegan_McD.org.Patches;
using Straftapelago.Finnegan_McD.org.Utils;

namespace Straftapelago.Finnegan_McD.org.Archipelago;

/// <summary>
/// The mod's connection to an Archipelago room: login, disconnect, the room's settings, and
/// the location checks this slot sends. Received items are handled in the other half of this
/// class.
/// </summary>
public partial class ArchipelagoClient
{
    public const string APVersion = "0.5.0";
    private const string Game = "Straftat";

    public static bool Authenticated;
    private bool attemptingConnection;

    public static ArchipelagoData ServerData = new();

    // Public because the two PlayerHealth.Update patches in deathLinkPatches reach it as
    // Plugin.ArchipelagoClient?.DeathLinkHandler.
    public DeathLinkHandler DeathLinkHandler;
    private ArchipelagoSession session;

    /// <summary>
    /// Whether this slot is currently marked ready with the room, toggled by /ap_ready.
    /// </summary>
    private bool ready;

    /// <summary>
    /// Opens a session and logs in. The connection details are already on
    /// <see cref="ServerData"/>, filled in from the Mod Menu login block.
    /// </summary>
    public void Connect()
    {
        if (Authenticated || attemptingConnection) return;

        // Shut before the socket is opened, so the inventory the room is about to replay cannot
        // set off a trap or a buff that was spent long ago. ApplySlotSettings reopens it.
        acceptOneShotItems = false;

        try
        {
            session = ArchipelagoSessionFactory.CreateSession(ServerData.Uri);
            SetupSession();
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError(e);
        }

        // CreateSession throws on an address it cannot parse and leaves session null, and
        // TryConnect dereferences it inside a ThreadPool work item where its own try/catch
        // cannot see the throw.
        if (session == null)
        {
            ArchipelagoConsole.LogMessage($"Could not open a session for '{ServerData.Uri}'. Check the Host field.");
            return;
        }

        // Set before TryConnect and cleared by HandleConnectResult on both outcomes, so that
        // the guard above stops a second Connect starting another session over this one.
        attemptingConnection = true;
        TryConnect();
    }

    /// <summary>Subscribes to the Archipelago events the mod listens for.</summary>
    private void SetupSession()
    {
        session.MessageLog.OnMessageReceived += message => ArchipelagoConsole.LogMessage(message.ToString());
        session.Items.ItemReceived += OnItemReceived;
        session.Socket.ErrorReceived += OnSessionErrorReceived;
        session.Socket.SocketClosed += OnSessionSocketClosed;
    }

    /// <summary>Attempts the login itself, off the game thread.</summary>
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
                        // AllItems, not IncludeOwnItems: the starting inventory is a separate
                        // flag, and the room's starting_weapons option is delivered as
                        // precollected items.
                        ItemsHandlingFlags.AllItems,
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
    /// Takes the login's answer: on success, reads the slot data, starts the death link
    /// service and puts the room's settings into effect; on failure, reports every error the
    /// room gave and disconnects.
    /// </summary>
    private void HandleConnectResult(LoginResult result)
    {
        string outText;
        if (result.Successful)
        {
            var success = (LoginSuccessful)result;

            ServerData.SetupSession(success.SlotData, session.RoomState.Seed);
            Authenticated = true;

            // After SetupSession, which is what reads deathlink out of the slot data. Handed
            // the value at construction so the service is subscribed on the server side before
            // the first frame can report a death.
            DeathLinkHandler = new(session.CreateDeathLinkService(), ServerData.SlotName,
                ServerData.DeathLink, ServerData.DeathsPerLink);
            session.Locations.CompleteLocationChecksAsync(ServerData.CheckedLocations.ToArray());
            outText = $"Successfully connected to {ServerData.Uri} as {ServerData.SlotName}!";

            ArchipelagoConsole.LogMessage(outText);

            // The room's answer to this slot's YAML. One call per line, because each becomes
            // its own chat message.
            foreach (string line in ServerData.DescribeSlotData())
                ArchipelagoConsole.LogMessage(line);

            ApplySlotSettings();
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
    /// Puts the room's settings into effect on the main thread: the two Mod Menu entries the
    /// room decides, a pool rebuild, the round counter, and the goal check. Queued because
    /// <see cref="HandleConnectResult"/> runs on a ThreadPool thread and every line of this is
    /// a Unity call.
    /// </summary>
    private void ApplySlotSettings()
    {
        MainThreadActions.Enqueue(() =>
        {
            ArchipelagoMenu.GreenMode.Value = ServerData.GreenMode;
            ArchipelagoMenu.NewWeaponChance.Value = ServerData.NewWeaponChance;

            // Assigning .Value updates the config and everything listening to it, but not the
            // Mod Menu page: it builds a plugin's option list once and caches it.
            ArchipelagoMenu.RefreshDisplayedValues();

            // The three weapon toggles only take effect through a rebuild, and the pool also
            // has to pick up whatever items the login has already delivered.
            Plugin.RouletteState?.Reset();

            // The round counter is memory-only and starts at zero with the process, so a rejoin
            // would otherwise re-send Round_1 on the next round win.
            TakeTracker.SeedRoundsWonFromRoom();

            // Last of all, and the reason this runs on a frame: everything the room replayed on
            // connect has arrived and been folded into the pool by now, so a trap or a Health
            // from here on is a new one.
            acceptOneShotItems = true;

            // After the rebuild, so this sees the checks the room has already recorded for this
            // slot and a player who reconnects having met the weapon goal has met it now too.
            GoalTracker.Evaluate();
        });
    }

    /// <summary>
    /// Closes the connection and drops everything that belonged to it. Called by the Mod Menu
    /// page's Disconnect button and by the failure and socket-closed paths.
    /// </summary>
    public void Disconnect()
    {
        Plugin.BepinLogger.LogDebug("disconnecting from server...");
        session?.Socket.DisconnectAsync();
        session = null;
        locationIdsByName = null;
        Authenticated = false;
        ready = false;

        // The next room has its own thresholds and its own checked locations, and both goals
        // re-derive from those on the next Evaluate.
        GoalTracker.Reset();

        // So a socket that drops and is reconnected goes through the same replay-suppressing
        // open as a first connect.
        acceptOneShotItems = false;
    }

    public void SendMessage(string message)
    {
        session.Socket.SendPacketAsync(new SayPacket { Text = message });
    }

    /// <summary>
    /// Flips this slot between ready and not ready with the room - a StatusUpdate packet, the
    /// same thing the Archipelago text client's /ready sends.
    /// </summary>
    /// <returns>true when this slot is now ready, false when it has just been unreadied.</returns>
    public bool ToggleReady()
    {
        ready = !ready;
        session.SetClientState(ready ? ArchipelagoClientState.ClientReady : ArchipelagoClientState.ClientConnected);
        return ready;
    }

    /// <summary>
    /// Tells the room this slot has finished, which is what completes the world. The apworld's
    /// two goals are events with no location id, so this status is the whole of what the server
    /// can be told about them. <see cref="GoalTracker"/> decides when it is sent.
    /// </summary>
    public void SendGoalCompletion()
    {
        if (!Authenticated || session == null) return;

        session.SetGoalAchieved();
    }

    /// <summary>
    /// Location names as the room's datapackage spells them, keyed case-insensitively. Built on
    /// first use and dropped on disconnect.
    /// </summary>
    private Dictionary<string, long> locationIdsByName;

    /// <summary>
    /// Turns a location name into the id the server wants, or -1 when there is no session or
    /// the room has no such location. The exact lookup is tried first; the case-insensitive
    /// fallback exists because the names the mod works from are the game's, not the apworld's.
    /// Only casing is forgiven - a name that differs by spacing or spelling is a genuine
    /// mismatch and should be reported.
    /// </summary>
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
    /// The names of every location this slot has already checked, as the room spells them. This
    /// is the record of which weapons have earned their first kill, and it lives on the server:
    /// <see cref="RouletteState.Reset"/> reads it to rebuild hasKill_Items, which is what
    /// restores the player's progress across a reconnect or a fresh launch. Empty rather than
    /// null when there is no session.
    /// </summary>
    public static IEnumerable<string> GetCheckedLocationNames()
    {
        ArchipelagoSession currentSession = Plugin.ArchipelagoClient?.session;
        if (currentSession == null) return Enumerable.Empty<string>();

        try
        {
            return currentSession.Locations.AllLocationsChecked
                .Select(locationId => currentSession.Locations.GetLocationNameFromId(locationId, Game))
                .Where(name => !string.IsNullOrEmpty(name))
                .ToArray();
        }
        catch (Exception e)
        {
            // Materialized inside the try, because the enumeration is where the datapackage is
            // actually touched: deferring it would move the throw out to the caller, which is a
            // Harmony prefix on PlayerPickup.Awake.
            Plugin.BepinLogger.LogError(
                $"Could not read the checked locations from the room{Environment.NewLine}{e}");
            return Enumerable.Empty<string>();
        }
    }

    /// <summary>
    /// Tells the room this location has been checked. Async because this is called from the
    /// kill path, inside a Harmony postfix on the frame a player died, so the send must not
    /// park the game thread on the socket.
    /// </summary>
    public void SendLocationCheck(long locationId)
    {
        session.Locations.CompleteLocationChecksAsync(new[] { locationId });
    }

    /// <summary>Reports a socket error to the log and the console.</summary>
    private void OnSessionErrorReceived(Exception e, string message)
    {
        Plugin.BepinLogger.LogError(e);
        ArchipelagoConsole.LogMessage(message);
    }

    /// <summary>Cleans up after the connection drops.</summary>
    private void OnSessionSocketClosed(string reason)
    {
        Plugin.BepinLogger.LogError($"Connection to Archipelago lost: {reason}");
        Disconnect();
    }
}
