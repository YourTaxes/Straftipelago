using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Helpers;
using Straftapelago.Finnegan_McD.org.Patches;
using Straftapelago.Finnegan_McD.org.Utils;

namespace Straftapelago.Finnegan_McD.org.Archipelago;

// What the mod does with an item the room sends: weapons go to the roulette pool, the four
// filler items fire once each, and Progressive Lazer counts its own tiers. Every branch defers
// to the main thread, because this all runs on the client's websocket thread.
public partial class ArchipelagoClient
{
    /// <summary>
    /// The name the apworld gives the three-tier lazer. Its item table has one item standing in
    /// for three weapons that exist as locations but never as items, so the mod turns the Nth
    /// copy back into a weapon name.
    /// </summary>
    private const string ProgressiveLazerItem = "Progressive Lazer";

    // Prefab names, in the order the tiers are handed out. HandCanon has one 'n' because that
    // is how the game spells it, and a name that resolves to no weapon grants nothing.
    private static readonly string[] LazerTiers = { "BeamLoad", "HandCanon", "BlankState" };

    /// <summary>
    /// The apworld's filler items: two traps and two buffs. Each is a separate item rather than
    /// one item whose behaviour a setting decides, so the name the room sends already says
    /// which one arrived and nothing here has to read slot data.
    /// </summary>
    private const string DeathTrapItem = "Death";

    private const string MetronomeTrapItem = "Metronome";

    private const string HealthBuffItem = "Health";

    private const string MadeInHeavenBuffItem = "Made in Heaven";

    /// <summary>How many Progressive Lazers this session has already been given.</summary>
    private int lazerTiersReceived;

    /// <summary>
    /// Whether an arriving trap or Health may actually go off, as opposed to being one the room
    /// is only replaying on connect. The four filler items are events, not possessions, so
    /// replaying them would kill the player for a trap that was spent rounds ago; the
    /// <see cref="ArchipelagoData.Index"/> watermark does not cover it, because that counter
    /// starts at zero in a fresh process. Opened by <see cref="EnsureRoom"/> as soon as a
    /// connect proves to be a rejoin of a room whose watermark this process already holds, and
    /// otherwise by the queued action in ApplySlotSettings, which is the first main-thread
    /// frame after the login completed. volatile because it is written on both threads.
    /// </summary>
    private volatile bool acceptOneShotItems;

    /// <summary>
    /// Whether an item arriving now is a new one rather than one the room is replaying on
    /// connect. The same latch <see cref="AllowOneShot"/> gates the traps on, exposed because the
    /// sound that announces an item is one-shot whatever the item is.
    /// </summary>
    internal static bool AcceptingNewItems => Plugin.ArchipelagoClient?.acceptOneShotItems ?? false;

    /// <summary>
    /// What the replay on connect amounted to, for the one chat line that stands in for a line
    /// per item. Counted on the websocket thread, read on the main thread after the replay.
    /// </summary>
    private int replayedAlreadyApplied;
    private int replayedWeapons;
    private int replayedSpentOneShots;

    private void ResetReplayTally()
    {
        replayedAlreadyApplied = 0;
        replayedWeapons = 0;
        replayedSpentOneShots = 0;
    }

    /// <summary>
    /// Prints the replay's tally to the chat, if there was one. Main thread, after the replay.
    /// </summary>
    private void ReportReplayTally()
    {
        int total = replayedAlreadyApplied + replayedWeapons + replayedSpentOneShots;
        if (total == 0) return;

        ArchipelagoConsole.LogMessage(
            $"The room replayed {total} item(s): {replayedWeapons} weapon unlock(s) applied, " +
            $"{replayedSpentOneShots} spent trap(s)/buff(s) skipped, {replayedAlreadyApplied} already applied.");
        ResetReplayTally();
    }

    /// <summary>
    /// Makes the per-room state match the room this connect landed in. A different seed drops
    /// the last room's watermark, ledger and lazer count; the same seed with a watermark already
    /// on it means the process has seen this room's inventory before, so anything above the
    /// watermark is new and a trap among it may go off at once - no need to wait for the
    /// replay to finish. Called from the first item of every connect, on the websocket thread,
    /// and from the login handler for a room that sends no items at all.
    /// </summary>
    private void EnsureRoom(string roomSeed)
    {
        lock (ServerData)
        {
            bool sameRoom = ServerData.EnterRoom(roomSeed);

            if (!sameRoom)
            {
                Plugin.BepinLogger.LogInfo($"[Archipelago] entering room {roomSeed}; per-room state starts fresh.");
                lazerTiersReceived = 0;

                // Ahead of every action the new room's items will queue, so the ledger is empty
                // by the time the first of them is granted.
                MainThreadActions.Enqueue(() => Plugin.RouletteState?.ForgetRoom());
                return;
            }

            if (ServerData.Index > 0 && !acceptOneShotItems)
            {
                Plugin.BepinLogger.LogInfo(
                    $"[Archipelago] rejoined room {roomSeed} with {ServerData.Index} item(s) already applied; " +
                    "anything newer goes off as it arrives.");
                acceptOneShotItems = true;
            }
        }
    }

    /// <summary>
    /// Takes one item off the helper's queue and applies it.
    /// </summary>
    private void OnItemReceived(ReceivedItemsHelper helper)
    {
        // Dequeued FIRST, unconditionally, and only then judged. This event fires once per item
        // the helper enqueues, so exactly one dequeue per call is what keeps the queue in step
        // with the callbacks: skipping it would leave that item at the head forever and every
        // later receipt would pop an older one instead of its own.
        var receivedItem = helper.DequeueItem();

        // RoomState is filled from the RoomInfo packet, which precedes every item, so the seed
        // is known by now even when the login handler has not run yet.
        ArchipelagoSession currentSession = session;
        if (currentSession != null) EnsureRoom(currentSession.RoomState.Seed);

        // Already applied. The server replays the whole inventory on every connect, and this
        // watermark is what stops a reconnect re-granting it - which matters most for the items
        // that are not idempotent.
        if (helper.Index <= ServerData.Index)
        {
            replayedAlreadyApplied++;
            return;
        }

        ServerData.Index++;

        string line = $"RECIEVED {receivedItem.ItemDisplayName} (item id {receivedItem.ItemId}, {receivedItem.Flags}) " +
            $"FROM {receivedItem.Player} playing {receivedItem.ItemGame} " +
            $"AT {receivedItem.LocationDisplayName} (location id {receivedItem.LocationId})";

        // ArchipelagoConsole, not KillFeed: this callback runs on the websocket thread, and
        // KillFeed.Write instantiates a chat line straight away, which is a Unity call.
        // LogMessage queues instead, and the overlay drains it on the main thread. A replayed
        // item goes to the log only; the tally reports the replay as one chat line.
        if (acceptOneShotItems) ArchipelagoConsole.LogMessage(line);
        else Plugin.BepinLogger.LogInfo(line);

        ApplyReceivedItem(receivedItem.ItemDisplayName, DescribeSender(receivedItem.Player));
    }

    /// <summary>
    /// The name to blame for an item: the alias, which is what the rest of the room calls that
    /// slot, falling back to the slot name only when no alias is set at all.
    /// </summary>
    private static string DescribeSender(PlayerInfo sender)
    {
        if (sender == null) return null;

        return string.IsNullOrWhiteSpace(sender.Alias) ? sender.Name : sender.Alias;
    }

    /// <summary>
    /// Turns one received item into its effect in the game.
    /// </summary>
    /// <param name="itemName">The item's display name, which is what the switch below matches.</param>
    /// <param name="sender">The slot that sent it, carried so a trap or buff can name whoever
    /// aimed it here.</param>
    private void ApplyReceivedItem(string itemName, string sender)
    {
        if (string.IsNullOrEmpty(itemName)) return;

        // Every item, whatever the branch below makes of it. Queued because the cue touches
        // Unity, and it judges the connect replay for itself.
        MainThreadActions.Enqueue(ItemReceivedSound.Play);

        switch (itemName)
        {
            case DeathTrapItem:
                if (!AllowOneShot(itemName)) return;

                // Through the DeathLink handler's own queue rather than a second kill path, so
                // the trap inherits its suppressNextDeath latch and the death it causes is not
                // reported straight back out as a fresh death link.
                MainThreadActions.Enqueue(() => DeathLinkHandler?.EnqueueTrapDeath(sender));
                return;

            case MetronomeTrapItem:
                if (!AllowOneShot(itemName)) return;

                MainThreadActions.Enqueue(() => MetronomeTrap.Receive(sender));
                return;

            case HealthBuffItem:
                if (!AllowOneShot(itemName)) return;

                MainThreadActions.Enqueue(PlayerHealthBuff.Enqueue);
                return;

            case MadeInHeavenBuffItem:
                if (!AllowOneShot(itemName)) return;

                MainThreadActions.Enqueue(() => MadeInHeaven.Receive(sender));
                return;

            case ProgressiveLazerItem:
                if (!acceptOneShotItems) replayedWeapons++;
                MainThreadActions.Enqueue(GrantNextLazerTier);
                return;

            default:
                if (!acceptOneShotItems) replayedWeapons++;
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

        // Logged rather than dropped in silence, and tallied for the chat: a trap that arrives
        // and does nothing is otherwise indistinguishable from a broken trap.
        replayedSpentOneShots++;
        Plugin.BepinLogger.LogInfo(
            $"[Archipelago] ignoring the {itemName} the room replayed on connect - it was already spent.");
        return false;
    }

    /// <summary>
    /// Grants the lazer tier this receipt of <see cref="ProgressiveLazerItem"/> stands for.
    /// </summary>
    private void GrantNextLazerTier()
    {
        if (lazerTiersReceived >= LazerTiers.Length)
        {
            // Reported rather than ignored: the apworld puts exactly three of these in the
            // pool, so a fourth means the two halves disagree about how many tiers there are.
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
}
