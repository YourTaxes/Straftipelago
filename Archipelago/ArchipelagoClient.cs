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
using Straftapelago.Finnegan_McD.org.Patches;
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
    /// Whether this slot is currently marked ready with the room, toggled by /ap_ready.
    /// </summary>
    /// <remarks>
    /// Tracked here because readiness is a fire-and-forget StatusUpdate packet - the server
    /// keeps the status but never reports it back, so nothing else on this side knows which
    /// way the next toggle should go. Cleared on disconnect, so a new room starts unready.
    /// </remarks>
    private bool ready;

    /// <summary>
    /// call to connect to an Archipelago session. Connection info should already be set up on ServerData
    /// </summary>
    /// <returns></returns>
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
                        // AllItems, not IncludeOwnItems: the starting inventory is a SEPARATE
                        // flag, and the room's starting_weapon option is delivered as a
                        // precollected item. Without it the player connects with an empty
                        // roulette pool and no way to fill it.
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

            // After SetupSession above, which is what reads deathlink out of the slot data the
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
    /// Puts the room's settings into effect, on the main thread.
    /// </summary>
    /// <remarks>
    /// <para>Queued rather than done here because <see cref="HandleConnectResult"/> runs on a
    /// ThreadPool thread and every line of this is a Unity call: assigning GreenMode raises
    /// SettingChanged, whose handler writes a killfeed line and walks every FirstPersonController
    /// in the scene, and Reset() reads SpawnerManager.</para>
    /// <para>Assigning the two Mod Menu entries is what mirrors the room's answer onto the Mod
    /// Menu page and into the .cfg. A BepInEx entry only raises SettingChanged when the value
    /// actually changes, so the tint refresh and its killfeed line happen once, and only when
    /// the room disagrees with what was already set. Both stay editable afterwards - the room
    /// decides them at connect, not for the rest of the session.</para>
    /// <para>Death link is not applied here: <see cref="DeathLinkHandler"/> is constructed with
    /// the value, which subscribes on the server side before any frame can report a death.</para>
    /// </remarks>
    private void ApplySlotSettings()
    {
        MainThreadActions.Enqueue(() =>
        {
            ArchipelagoMenu.GreenMode.Value = ServerData.GreenMode;
            ArchipelagoMenu.NewWeaponChance.Value = ServerData.NewWeaponChance;

            // Assigning .Value updates the config and everything listening to it, but NOT the
            // Mod Menu page: it builds a plugin's option list once and caches it, so a control
            // never re-reads its entry on its own. This is what makes the page agree with what
            // the room just decided.
            ArchipelagoMenu.RefreshDisplayedValues();

            // The three weapon toggles only take effect through a rebuild, and the pool also has
            // to pick up whatever items the login has already delivered - both of which this one
            // call covers.
            Plugin.RouletteState?.Reset();

            // Last of all, and the reason this runs on a frame rather than on the login's own
            // thread: everything the room replayed on connect has arrived and been folded into
            // the pool by now, so a Death or a Health from here on is a new one.
            acceptOneShotItems = true;

            // After the rebuild above, so this sees the checks the room has already recorded for
            // this slot. A player who reconnects having already met the weapon goal has met it
            // now too, and the room should hear so without another kill being needed.
            GoalTracker.Evaluate();
        });
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
        ready = false;

        // The next room has its own thresholds and its own checked locations, and both goals
        // re-derive from those on the next Evaluate - so keeping this session's answers could
        // only ever be wrong.
        GoalTracker.Reset();

        // So a socket that drops and is reconnected goes through the same replay-suppressing
        // open as a first connect, rather than inheriting the last session's answer.
        acceptOneShotItems = false;
    }

    public void SendMessage(string message)
    {
        session.Socket.SendPacketAsync(new SayPacket { Text = message });
    }

    /// <summary>
    /// Flips this slot between ready and not ready with the room, and reports which it now is.
    /// </summary>
    /// <remarks>
    /// The same thing the Archipelago text client's /ready does: a StatusUpdate packet
    /// carrying ClientReady or, to take it back, ClientConnected. It is not a !command, so it
    /// does not go through <see cref="SendMessage"/> and the room prints no reply to it.
    /// </remarks>
    /// <returns>true when this slot is now ready, false when it has just been unreadied.</returns>
    public bool ToggleReady()
    {
        ready = !ready;
        session.SetClientState(ready ? ArchipelagoClientState.ClientReady : ArchipelagoClientState.ClientConnected);
        return ready;
    }

    /// <summary>
    /// Tells the room this slot has finished, which is what completes the world.
    /// </summary>
    /// <remarks>
    /// A StatusUpdate carrying ClientGoal. The apworld's two goals are EVENTS - no location id,
    /// nothing sendable - so this single status is the whole of what the server can be told
    /// about them. Sent by <see cref="GoalTracker"/>, which is also what decides that the room's
    /// win_condition has actually been satisfied; this only puts it on the wire.
    /// </remarks>
    public void SendGoalCompletion()
    {
        if (!Authenticated || session == null) return;

        session.SetGoalAchieved();
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
    /// The names of every location this slot has already checked, as the room spells them.
    /// </summary>
    /// <remarks>
    /// <para>This is the record of which weapons have earned their first kill, and it lives on
    /// the server rather than in this process: one location per weapon, completed the moment
    /// that kill happens. <see cref="Patches.RouletteState.Reset"/> reads it to rebuild
    /// hasKill_Items, which is why a reconnect - or a fresh launch of the game - restores the
    /// player's progress rather than starting the pool over.</para>
    /// <para>Static to match <see cref="Authenticated"/> and <see cref="ServerData"/>, since
    /// the pool reaches everything about the connection that way. Empty rather than null when
    /// there is no session, so a caller offline simply finds nothing checked.</para>
    /// </remarks>
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
            // Materialized inside the try on purpose - the enumeration is where the datapackage
            // is actually touched, so deferring it would move the throw out to the caller, which
            // is a Harmony prefix on PlayerPickup.Awake() that must not see one.
            Plugin.BepinLogger.LogError(
                $"Could not read the checked locations from the room{Environment.NewLine}{e}");
            return Enumerable.Empty<string>();
        }
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
        // Dequeued FIRST, unconditionally, and only then judged. This event fires once per item
        // the helper enqueues, so exactly one dequeue per call is what keeps the queue in step
        // with the callbacks - skipping the dequeue leaves that item at the head of the queue
        // forever and every later receipt pops an older one instead of its own. That is not
        // hypothetical: reconnecting re-sends every item this slot has ever had, all of which
        // the guard below rejects, so one rejected-without-dequeuing item is enough to make the
        // room's "sending Hand Grenade" arrive here as a GodSword for the rest of the session.
        var receivedItem = helper.DequeueItem();

        // Already applied. The server replays the whole inventory on every connect, and this
        // watermark is what stops a reconnect re-granting it - which matters most for the items
        // that are not idempotent: a second Progressive Lazer would unlock a tier that was never
        // earned, and a second Death would kill the player again.
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

        ApplyReceivedItem(receivedItem.ItemDisplayName, DescribeSender(receivedItem.Player));
    }

    /// <summary>
    /// The name to blame for an item, as the lobby should see it.
    /// </summary>
    /// <remarks>
    /// Alias first because that is the name the rest of the room is calling that slot - it is
    /// what the server's own messages use, and it falls back to the slot name on its own when
    /// nobody set one. Name is only reached if the alias is missing outright.
    /// </remarks>
    private static string DescribeSender(PlayerInfo sender)
    {
        if (sender == null) return null;

        return string.IsNullOrWhiteSpace(sender.Alias) ? sender.Name : sender.Alias;
    }

    /// <summary>
    /// The name the apworld gives the three-tier lazer, and the weapons each receipt of it
    /// unlocks in order.
    /// </summary>
    /// <remarks>
    /// The apworld's item table has one "Progressive Lazer" standing in for three weapons that
    /// exist as locations but never as items, so the mod is the half that has to turn the Nth
    /// copy back into a weapon name. Order is the tier order the apworld's own comments give.
    /// </remarks>
    private const string ProgressiveLazerItem = "Progressive Lazer";

    // Prefab names, and in the order the tiers are meant to be handed out - which is NOT the
    // order the apworld's own comments list them in. HandCanon has one 'n': that is how the
    // game spells it, and spelling it "Hand Cannon" here is why the third tier silently granted
    // nothing (the name resolved to no weapon, and Grant answers false for that).
    private static readonly string[] LazerTiers = { "BeamLoad", "HandCanon", "BlankState" };

    /// <summary>The apworld's two filler items: a trap and a buff.</summary>
    private const string DeathTrapItem = "Death";

    private const string HealthBuffItem = "Health";

    /// <summary>How many Progressive Lazers this session has already been given.</summary>
    private int lazerTiersReceived;

    /// <summary>
    /// Whether an arriving Death or Health may actually go off, as opposed to being one the
    /// room is only reminding us we already had.
    /// </summary>
    /// <remarks>
    /// <para>The room replays this slot's ENTIRE inventory on every connect. For a weapon that
    /// is exactly what is wanted - re-granting one it already gave is how the pool gets rebuilt
    /// after a rejoin, and Grant refuses the duplicates. For the two filler items it is not:
    /// they are events, not possessions, and replaying them killed the player and set their
    /// health on connect for a trap and a buff that had already been spent, possibly rounds ago.
    /// The <see cref="ArchipelagoData.Index"/> watermark alone does not cover it, because that
    /// counter starts at zero in a fresh process - so the first connect after launching the
    /// game treats the whole replayed inventory as new.</para>
    /// <para>Opened by the queued action <see cref="ApplySlotSettings"/> runs, which is the
    /// first main-thread frame after the login completed. The replay is delivered on the socket
    /// thread in the same breath as the Connected packet that ended the login, so it is over
    /// long before a frame ticks; anything arriving after that frame genuinely happened while
    /// the player was connected and playing.</para>
    /// <para>volatile because it is written on Unity's main thread and read on the Archipelago
    /// client's websocket thread.</para>
    /// </remarks>
    private volatile bool acceptOneShotItems;

    /// <summary>
    /// Turns one received item into its effect in the game.
    /// </summary>
    /// <remarks>
    /// Every branch defers to the main thread, because this runs on the client's websocket
    /// thread: granting a weapon resolves it against SpawnerManager, and both filler items
    /// touch the local player.
    /// </remarks>
    /// <param name="itemName">The item's display name, which is what the switch below matches.</param>
    /// <param name="sender">The slot that sent it, carried only so a Death trap can name whoever
    /// aimed it here.</param>
    private void ApplyReceivedItem(string itemName, string sender)
    {
        if (string.IsNullOrEmpty(itemName)) return;

        switch (itemName)
        {
            case DeathTrapItem:
                if (!AllowOneShot(itemName)) return;

                // Through the DeathLink handler's own queue rather than a second kill path, so
                // the trap inherits its suppressNextDeath latch - without which the death it
                // causes would be reported straight back out as a fresh death link.
                MainThreadActions.Enqueue(() => DeathLinkHandler?.EnqueueTrapDeath(sender));
                return;

            case HealthBuffItem:
                if (!AllowOneShot(itemName)) return;

                MainThreadActions.Enqueue(PlayerHealthBuff.Enqueue);
                return;

            case ProgressiveLazerItem:
                MainThreadActions.Enqueue(GrantNextLazerTier);
                return;

            default:
                MainThreadActions.Enqueue(() => Plugin.RouletteState?.ReceiveWeapon(itemName));
                return;
        }
    }

    /// <summary>
    /// Whether a filler item that fires once may fire now. See <see cref="acceptOneShotItems"/>.
    /// </summary>
    private bool AllowOneShot(string itemName)
    {
        if (acceptOneShotItems) return true;

        // Reported rather than dropped in silence: "the room sent me a Death and nothing
        // happened" is otherwise indistinguishable from the trap being broken.
        ArchipelagoConsole.LogMessage(
            $"Ignoring the {itemName} the room replayed on connect - it was already spent.");
        return false;
    }

    /// <summary>
    /// Grants the lazer tier this receipt of <see cref="ProgressiveLazerItem"/> stands for.
    /// </summary>
    private void GrantNextLazerTier()
    {
        if (lazerTiersReceived >= LazerTiers.Length)
        {
            // Reported rather than ignored: the apworld puts exactly three of these in the pool,
            // so a fourth means the two halves disagree about how many tiers there are.
            Plugin.BepinLogger.LogWarning(
                $"[Archipelago] received a {lazerTiersReceived + 1}th '{ProgressiveLazerItem}' but " +
                $"only {LazerTiers.Length} lazer tiers exist; ignoring it.");
            return;
        }

        string tier = LazerTiers[lazerTiersReceived];
        lazerTiersReceived++;

        ArchipelagoConsole.LogMessage($"{ProgressiveLazerItem} {lazerTiersReceived} unlocked the {tier}.");
        Plugin.RouletteState?.ReceiveWeapon(tier);
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