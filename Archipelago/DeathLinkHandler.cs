using System;
using System.Collections.Generic;
using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using BepInEx;
using Straftapelago.Finnegan_McD.org.Patches;

namespace Straftapelago.Finnegan_McD.org.Archipelago;

/// <summary>
/// Shares the local player's deaths with the multiworld, and applies the deaths - and the Death
/// traps - the multiworld sends back.
/// </summary>
public class DeathLinkHandler
{
    /// <summary>
    /// What the whole STRAFTAT lobby is told when a death arrives from the multiworld.
    /// {0} is the Archipelago slot that died, {1} is the local player.
    /// </summary>
    private const string BroadcastFormat =
        "{0} died in Archipelago and ruined it for {1} - everybody point and laugh at {1}";

    /// <summary>
    /// What the lobby is told when the death came from a Death trap. {0} is the Archipelago slot
    /// that sent the trap, {1} is the local player: a trap was aimed here on purpose, so the
    /// sender is the one to point at.
    /// </summary>
    private const string TrapBroadcastFormat =
        "{0} sent {1} a death trap; Everybody point and laugh at {0}";

    /// <summary>
    /// Stands in for the sender when a trap arrives without a usable slot name, so the line
    /// still reads as a sentence instead of blaming an empty string.
    /// </summary>
    private const string UnknownTrapSender = "Archipelago";

    /// <summary>
    /// One death waiting to be applied, and where it came from. Traps and received deaths share
    /// this queue: they compete for the same one death the player can usefully be given at a
    /// time, and both need the same wait for a state where a kill is visible. Only the
    /// announcement differs.
    /// </summary>
    private readonly struct PendingDeath
    {
        public PendingDeath(DeathLink link, string trapSender)
        {
            Link = link;
            TrapSender = trapSender;
        }

        /// <summary>The multiworld death that caused this. Null for a trap.</summary>
        public DeathLink Link { get; }

        /// <summary>
        /// The Archipelago slot that sent the Death trap, or null when this is a received death.
        /// Carried per-death rather than read at announce time, because the queue can hold more
        /// than one, from more than one sender.
        /// </summary>
        public string TrapSender { get; }

        public bool IsTrap => TrapSender != null;
    }

    private bool deathLinkEnabled;
    private string slotName;
    private readonly DeathLinkService service;

    /// <summary>
    /// How many local deaths it takes to send one out - the room's deaths_per_link, taken once
    /// at construction because that is the connect that carried the slot data it came from.
    /// </summary>
    private readonly int deathsPerLink;

    /// <summary>
    /// Local deaths counted since the last one was shared. Only ever touched from Unity's main
    /// thread, so unlike <see cref="deathLinks"/> it needs no lock.
    /// </summary>
    private int deathsSinceLastLink;

    /// <summary>Whether the room linked this slot's deaths at all. Shown in the pause overlay.</summary>
    public bool DeathLinkEnabled => deathLinkEnabled;

    /// <summary>
    /// How many local deaths one outgoing death costs. Never below 1, whatever the room said.
    /// </summary>
    public int DeathsPerLink => deathsPerLink;

    /// <summary>
    /// Deaths taken since the last one was shared. Never reaches <see cref="DeathsPerLink"/>:
    /// the death that would make it equal is the one that is sent, and it resets to 0 in the
    /// same call.
    /// </summary>
    public int DeathsTowardNextLink => deathsSinceLastLink;

    /// <summary>How many deaths this session has actually put out into the multiworld.</summary>
    public int DeathLinksSent { get; private set; }

    /// <summary>
    /// Deaths waiting to be applied. Filled on the Archipelago client's websocket thread and
    /// drained on Unity's main thread, so every touch of it is locked: a Queue corrupts its
    /// backing array if an Enqueue lands in the middle of a Dequeue.
    /// </summary>
    private readonly Queue<PendingDeath> deathLinks = new();

    /// <summary>
    /// Set while a death this handler caused is still working its way back to the player, so it
    /// is not sent straight out again as a death of the player's own. <see cref="KillPlayer"/>
    /// goes through a ServerRpc whose logic sets health to -8f, which lands back on this client
    /// a frame or two later and is indistinguishable at PlayerHealth.Update from any other
    /// death.
    /// </summary>
    private bool suppressNextDeath;

    /// <summary>
    /// Subscribes to the death link service and enables the link if the room asked for it.
    /// </summary>
    /// <param name="deathLinkService">The service this handler sends and receives through.</param>
    /// <param name="enableDeathLink">Whether the room turned death link on for this slot.</param>
    /// <param name="deathsPerLinkSetting">The room's deaths_per_link. Anything below 1 is taken
    /// as 1, since "a link every no deaths" has no meaning.</param>
    public DeathLinkHandler(
        DeathLinkService deathLinkService, string name, bool enableDeathLink = false, int deathsPerLinkSetting = 1)
    {
        service = deathLinkService;
        service.OnDeathLinkReceived += DeathLinkReceived;
        slotName = name;
        deathLinkEnabled = enableDeathLink;
        deathsPerLink = Math.Max(1, deathsPerLinkSetting);

        if (deathLinkEnabled)
        {
            service.EnableDeathLink();
        }
    }

    /// <summary>Turns death link on or off.</summary>
    public void ToggleDeathLink()
    {
        deathLinkEnabled = !deathLinkEnabled;

        // Partial progress is dropped either way round. Deaths taken while unlinked were never
        // going to be shared, and carrying a count across the gap would send the next death
        // early off the back of them.
        deathsSinceLastLink = 0;

        if (deathLinkEnabled)
        {
            service.EnableDeathLink();
        }
        else
        {
            service.DisableDeathLink();
        }
    }

    /// <summary>Queues a death that arrived from another world.</summary>
    private void DeathLinkReceived(DeathLink deathLink)
    {
        // Queued rather than acted on: this runs on the Archipelago client's websocket thread,
        // and every step of actually killing the player is a main-thread-only Unity call.
        lock (deathLinks)
        {
            deathLinks.Enqueue(new PendingDeath(deathLink, null));
        }

        Plugin.BepinLogger.LogDebug(deathLink.Cause.IsNullOrWhiteSpace()
            ? $"Received Death Link from: {deathLink.Source}"
            : deathLink.Cause);
    }

    /// <summary>
    /// Queues a death the room inflicted with a Death trap, to be applied like a received one.
    /// Through this queue rather than a kill path of its own, because everything that makes a
    /// received death safe applies to a trap unchanged - above all the
    /// <see cref="suppressNextDeath"/> latch, without which the trap's own death would be
    /// reported back to the multiworld as a fresh one and kill every linked world.
    /// </summary>
    /// <param name="sender">The Archipelago slot that sent the trap, for the line the lobby is
    /// shown. Blank or null falls back to <see cref="UnknownTrapSender"/>.</param>
    public void EnqueueTrapDeath(string sender)
    {
        // Normalized here rather than at announce time so that TrapSender is never null for a
        // trap - that is what PendingDeath.IsTrap reads to tell the two kinds of death apart.
        string trapSender = sender.IsNullOrWhiteSpace() ? UnknownTrapSender : sender;

        lock (deathLinks)
        {
            deathLinks.Enqueue(new PendingDeath(null, trapSender));
        }

        Plugin.BepinLogger.LogDebug($"[DeathLink] queued a Death trap from {trapSender}");
    }

    /// <summary>
    /// Called every frame from <see cref="Patches.PlayerHealthDeathLinkKillPatch"/>. Kills the
    /// local player the way falling out of the map does, if a death is waiting and they are in a
    /// state to receive it.
    /// </summary>
    /// <param name="playerHealth">The local player's PlayerHealth. The patch has already checked
    /// IsOwner.</param>
    public void KillPlayer(PlayerHealth playerHealth)
    {
        try
        {
            // Cheap early-out: this runs every frame, and almost every one of them has nothing
            // waiting.
            lock (deathLinks)
            {
                if (deathLinks.Count < 1) return;
            }

            if (playerHealth == null || playerHealth.controller == null) return;

            // A death waits rather than being spent. Health at or below zero means they are
            // already dying, and canMove is false through the round-transition freezes; killing
            // into either state does nothing visible and the death would be thrown away.
            //
            // controller.canMove, not playerHealth.canMove: the one on PlayerHealth is
            // initialized true in its constructor and never written again. The controller's is
            // also lowered by the taser, so a death arriving mid-stun lands when the stun ends.
            if (playerHealth.health <= 0f || !playerHealth.controller.canMove) return;

            PendingDeath pendingDeath;
            lock (deathLinks)
            {
                // Re-checked inside the lock, because a Dequeue on an empty queue throws.
                if (deathLinks.Count < 1) return;
                pendingDeath = deathLinks.Dequeue();
            }

            DeathLink deathLink = pendingDeath.Link;
            string cause = pendingDeath.IsTrap
                ? $"Received a Death trap from {pendingDeath.TrapSender}"
                : deathLink.Cause.IsNullOrWhiteSpace() ? GetDeathLinkCause(deathLink) : deathLink.Cause;

            Plugin.BepinLogger.LogMessage(cause);

            // Armed before the kill, not after: DespawnObject is a ServerRpc, and on a listen
            // host the server half can run inside this call.
            suppressNextDeath = true;

            // Vanilla's void death, which is what FirstPersonController's own Killz and
            // below-the-map branches do. fellVoid is what makes PlayerHealth.Update print the
            // death to the feed.
            //
            // Settings.Instance.IncreaseSuicidesAmount() is the one line of that sequence left
            // out on purpose: a death another world handed over is not a suicide, so it must not
            // inflate the suicide stat, and calling it would make SuicideDetectPatch print a
            // second, wrong "killed themselves" line over this one.
            playerHealth.fellVoid = true;
            playerHealth.controller.DespawnObject(playerHealth.gameObject);

            Broadcast(pendingDeath.IsTrap
                ? string.Format(TrapBroadcastFormat, pendingDeath.TrapSender, KillFeed.LocalPlayerName)
                : string.Format(BroadcastFormat, deathLink.Source, KillFeed.LocalPlayerName));
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError(e);
        }
    }

    /// <summary>
    /// Tells every player in the STRAFTAT match what is responsible for this death.
    /// </summary>
    /// <param name="message">The already-formatted line, since a trap and a received death blame
    /// different things.</param>
    private void Broadcast(string message)
    {
        Plugin.BepinLogger.LogInfo($"[DeathLink] {message}");

        try
        {
            // WriteLog, not KillFeed's WriteLocalLog: this line is meant for the whole lobby.
            // WriteLog is a ServerRpc whose reader checks only IsServer, so any client may call
            // it and the server relays it to every observer. MatchLogs is null offline;
            // MatchLogsOffline is the live one there.
            if (MatchLogs.Instance != null)
            {
                MatchLogs.Instance.WriteLog(message);
            }
            else if (MatchLogsOffline.Instance != null)
            {
                MatchLogsOffline.Instance.WriteLog(message);
            }
        }
        catch (Exception e)
        {
            // The player is already dead by now and the line is in the BepInEx log either way.
            Plugin.BepinLogger.LogError($"[DeathLink] Could not announce the death to the lobby{Environment.NewLine}{e}");
        }
    }

    /// <summary>The line shown when a received death link carries no cause of its own.</summary>
    private string GetDeathLinkCause(DeathLink deathLink)
    {
        return $"Received death from {deathLink.Source}";
    }

    /// <summary>
    /// Called from <see cref="Patches.PlayerHealthDeathLinkSendPatch"/> on the one frame the
    /// local player's death is visible, whatever caused it.
    /// </summary>
    public void LocalPlayerDied(PlayerHealth playerHealth)
    {
        try
        {
            // A death this handler caused, coming back around. Consumed rather than merely
            // tested, so the next real death is shared normally.
            if (suppressNextDeath)
            {
                suppressNextDeath = false;
                return;
            }

            // Gated here rather than inside SendDeathLink, so that a trap or any other caller
            // meaning "send this death now" still does exactly that. Ahead of the count, not
            // after it: deaths taken while unlinked must not build up progress that fires the
            // moment death link is switched back on.
            if (!deathLinkEnabled) return;

            deathsSinceLastLink++;

            if (deathsSinceLastLink < deathsPerLink)
            {
                Plugin.BepinLogger.LogDebug(
                    $"[DeathLink] death {deathsSinceLastLink}/{deathsPerLink}; not sharing this one.");
                return;
            }

            // Reset before the send, so a throw out of SendDeathLink cannot leave the counter
            // parked at the threshold and share every death from here on.
            deathsSinceLastLink = 0;
            DeathLinksSent++;

            SendDeathLink(DescribeDeath(playerHealth));
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError(e);
        }
    }

    /// <summary>
    /// Turns the flags vanilla sets on the way into a death into a line for the world that
    /// receives it. These are the same flags PlayerHealth.Update reads for its own feed.
    /// </summary>
    private string DescribeDeath(PlayerHealth playerHealth)
    {
        string playerName = KillFeed.LocalPlayerName;

        if (playerHealth == null) return $"{playerName} died in STRAFTAT";
        if (playerHealth.fellVoid) return $"{playerName} fell into the void";
        if (playerHealth.suicide) return $"{playerName} killed themselves";

        return $"{playerName} was killed in STRAFTAT";
    }

    /// <summary>Sends one death out to the multiworld.</summary>
    /// <param name="cause">What killed the player, shown by the worlds that receive it. Null
    /// falls back to the bare slot name.</param>
    public void SendDeathLink(string cause = null)
    {
        try
        {
            if (!deathLinkEnabled) return;

            Plugin.BepinLogger.LogMessage("sharing your death...");

            var linkToSend = new DeathLink(slotName, cause);

            service.SendDeathLink(linkToSend);
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError(e);
        }
    }
}
