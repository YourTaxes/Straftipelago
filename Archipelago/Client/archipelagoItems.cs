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
    /// starts at zero in a fresh process. Opened by the queued action in ApplySlotSettings,
    /// which is the first main-thread frame after the login completed. volatile because it is
    /// written on Unity's main thread and read on the websocket thread.
    /// </summary>
    private volatile bool acceptOneShotItems;

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

        // Already applied. The server replays the whole inventory on every connect, and this
        // watermark is what stops a reconnect re-granting it - which matters most for the items
        // that are not idempotent.
        if (helper.Index <= ServerData.Index) return;

        ServerData.Index++;

        // ArchipelagoConsole, not KillFeed: this callback runs on the websocket thread, and
        // KillFeed.Write instantiates a chat line straight away, which is a Unity call.
        // LogMessage queues instead, and the overlay drains it on the main thread.
        ArchipelagoConsole.LogMessage(
            $"RECIEVED {receivedItem.ItemDisplayName} (item id {receivedItem.ItemId}, {receivedItem.Flags}) " +
            $"FROM {receivedItem.Player} playing {receivedItem.ItemGame} " +
            $"AT {receivedItem.LocationDisplayName} (location id {receivedItem.LocationId})");

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

        // Reported rather than dropped in silence: a trap that arrives and does nothing is
        // otherwise indistinguishable from a broken trap.
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
