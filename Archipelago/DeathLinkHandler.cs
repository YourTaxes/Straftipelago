using System;
using System.Collections.Generic;
using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using BepInEx;
using Straftapelago.Finnegan_McD.org.Patches;

namespace Straftapelago.Finnegan_McD.org.Archipelago;

public class DeathLinkHandler
{
    /// <summary>
    /// What the whole STRAFTAT lobby is told when a death arrives from the multiworld.
    /// {0} is the Archipelago slot that died, {1} is the local player.
    /// </summary>
    private const string BroadcastFormat =
        "{0} died in Archipelago and ruined it for {1} - everybody point and laugh at {1}";

    /// <summary>
    /// What the lobby is told when the death came from a Death trap rather than another world.
    /// {0} is the Archipelago slot that sent the trap, {1} is the local player. Separate from
    /// <see cref="BroadcastFormat"/> because a trap was aimed here on purpose - the sender is
    /// the one to point at, not the one who died somewhere else.
    /// </summary>
    private const string TrapBroadcastFormat =
        "{0} sent {1} a death trap; Everybody point and laugh at {0}";

    /// <summary>
    /// Stands in for the sender when a trap arrives without a usable slot name, so the line
    /// still reads as a sentence instead of blaming an empty string.
    /// </summary>
    private const string UnknownTrapSender = "Archipelago";

    /// <summary>
    /// One death waiting to be applied, and where it came from.
    /// </summary>
    /// <remarks>
    /// The two share this queue, and must: they compete for the same one death the player can
    /// usefully be given at a time, and both need the same wait for a state where a kill is
    /// visible. Only the announcement differs.
    /// </remarks>
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
        /// The Archipelago slot that sent the Death trap, or null when this is a received death
        /// rather than a trap. Carried per-death rather than read at announce time because the
        /// queue can hold more than one, from more than one sender.
        /// </summary>
        public string TrapSender { get; }

        public bool IsTrap => TrapSender != null;
    }

    private bool deathLinkEnabled;
    private string slotName;
    private readonly DeathLinkService service;

    /// <summary>
    /// How many local deaths it takes to send one out - the room's DeathsPerLink option, taken
    /// once at construction because that is the connect that carried the slot data it came from.
    /// </summary>
    private readonly int deathsPerLink;

    /// <summary>
    /// Local deaths counted since the last one was shared. Only ever touched from Unity's main
    /// thread - <see cref="LocalPlayerDied"/> is called out of PlayerHealth.Update and the pause
    /// overlay reads it in OnGUI - so unlike <see cref="deathLinks"/> it needs no lock.
    /// </summary>
    private int deathsSinceLastLink;

    /// <summary>Whether the room linked this slot's deaths at all. Shown in the pause overlay.</summary>
    public bool DeathLinkEnabled => deathLinkEnabled;

    /// <summary>
    /// How many local deaths one outgoing death costs. Never below 1, whatever the room said.
    /// </summary>
    public int DeathsPerLink => deathsPerLink;

    /// <summary>
    /// Deaths taken since the last one was shared, so the overlay can show
    /// <c>DeathsTowardNextLink / DeathsPerLink</c>. Never reaches
    /// <see cref="DeathsPerLink"/>: the death that would make it equal is the one that is sent,
    /// and it resets to 0 in the same call.
    /// </summary>
    public int DeathsTowardNextLink => deathsSinceLastLink;

    /// <summary>How many deaths this session has actually put out into the multiworld.</summary>
    public int DeathLinksSent { get; private set; }

    /// <summary>
    /// Deaths waiting to be applied. Filled on the Archipelago client's websocket thread and
    /// drained on Unity's main thread, so every touch of it is locked - Queue&lt;T&gt; corrupts its
    /// backing array if an Enqueue lands in the middle of a Dequeue. Same reason
    /// <see cref="Utils.MainThreadQueue"/> locks.
    /// </summary>
    private readonly Queue<PendingDeath> deathLinks = new();

    /// <summary>
    /// Set while a death this handler caused is still working its way back to us, so that it is
    /// not immediately sent out again as a death of our own.
    /// </summary>
    /// <remarks>
    /// <see cref="KillPlayer"/> goes through FirstPersonController.DespawnObject, a ServerRpc
    /// whose logic sets health to -8f. That lands back on this client a frame or two later and is
    /// indistinguishable, at PlayerHealth.Update, from any other death - so without this latch a
    /// received death bounces straight back into the multiworld and every linked world dies again.
    /// </remarks>
    private bool suppressNextDeath;

    /// <summary>
    /// instantiates our death link handler, sets up the hook for receiving death links, and enables death link if needed
    /// </summary>
    /// <param name="deathLinkService">The new DeathLinkService that our handler will use to send and
    /// receive death links</param>
    /// <param name="enableDeathLink">Whether we should enable death link or not on startup</param>
    /// <param name="deathsPerLinkSetting">The room's deaths_per_link. Anything below 1 is taken as
    /// 1 - "a link every no deaths" has no meaning, and every-death is what a room that does not
    /// offer the option behaves like. ArchipelagoData clamps it too; this is here so the invariant
    /// holds for any other caller as well.</param>
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

    /// <summary>
    /// enables/disables death link
    /// </summary>
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

    /// <summary>
    /// what happens when we receive a deathLink
    /// </summary>
    /// <param name="deathLink">Received Death Link object to handle</param>
    private void DeathLinkReceived(DeathLink deathLink)
    {
        // Queued rather than acted on: this runs on the Archipelago client's websocket thread,
        // and every step of actually killing the player is a main-thread-only Unity call.
        // PlayerHealthDeathLinkKillPatch drains it.
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
    /// </summary>
    /// <remarks>
    /// Through this queue rather than a kill path of its own, because everything that makes a
    /// received death safe applies to a trap unchanged: the wait for a state where the kill is
    /// visible, and above all the suppressNextDeath latch. Without that latch the trap's own
    /// death would come back around through PlayerHealth.Update and be reported to the
    /// multiworld as a fresh death of ours, killing every linked world for a trap that was only
    /// ever meant for this one.
    /// </remarks>
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
    /// Called every frame from <see cref="Patches.PlayerHealthDeathLinkKillPatch"/> with the local
    /// player's health. Kills them the way falling out of the map does if a death is waiting and
    /// they are in a state to receive it.
    /// </summary>
    /// <param name="playerHealth">The local player's PlayerHealth. The patch has already checked
    /// IsOwner, so this is never somebody else's.</param>
    public void KillPlayer(PlayerHealth playerHealth)
    {
        try
        {
            // Cheap early-out for the overwhelmingly common case: this runs every frame, and
            // almost every one of them has nothing waiting.
            lock (deathLinks)
            {
                if (deathLinks.Count < 1) return;
            }

            if (playerHealth == null || playerHealth.controller == null) return;

            // A death waits rather than being spent. Health at or below zero means they are
            // already dying or dead, and the controller's canMove is false through the freezes
            // PlayerManager.SetPlayerMove puts on the round transitions - killing into either of
            // those states does nothing visible and the death would be silently thrown away.
            //
            // controller.canMove, NOT playerHealth.canMove: the one on PlayerHealth is
            // initialized true in its constructor and no game code ever writes it again, so it
            // would gate on nothing. The controller's is the live one, and it is also lowered by
            // the taser - a death arriving mid-stun therefore lands when the stun ends, which is
            // the right side to err on.
            if (playerHealth.health <= 0f || !playerHealth.controller.canMove) return;

            PendingDeath pendingDeath;
            lock (deathLinks)
            {
                // Re-checked inside the lock rather than trusting the count above. Nothing else
                // dequeues today, but a Dequeue on an empty queue throws, and the guard costs
                // nothing next to the kill it is protecting.
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

            // Vanilla's void death, which is FirstPersonController.OnTriggerEnter's "Killz"
            // branch and its own y < -300f branch, both of which do exactly this. fellVoid is
            // what makes PlayerHealth.Update print the death to the feed.
            //
            // Settings.Instance.IncreaseSuicidesAmount() is the one line of that sequence left
            // out on purpose: a death handed to us by another world is not a suicide, so it must
            // not inflate the player's suicide stat - and calling it would make SuicideDetectPatch
            // print a second, wrong "killed themselves" line over the top of ours.
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
    /// <param name="message">The already-formatted line, since a trap and a received death
    /// blame different things.</param>
    private void Broadcast(string message)
    {
        Plugin.BepinLogger.LogInfo($"[DeathLink] {message}");

        try
        {
            // WriteLog, not KillFeed's WriteLocalLog: this line is meant for the whole lobby.
            // WriteLog is a ServerRpc whose reader checks only IsServer - there is no ownership
            // check - so any client may call it and the server relays it to every observer.
            // MatchLogs is a NetworkBehaviour singleton and is null offline; MatchLogsOffline is
            // the live one there, and local-only is all there is to write to anyway.
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
            // A half-built chat panel must not turn into a swallowed death.
            Plugin.BepinLogger.LogError($"[DeathLink] Could not announce the death to the lobby{Environment.NewLine}{e}");
        }
    }

    /// <summary>
    /// returns message for the player to see when a death link is received without a cause
    /// </summary>
    /// <param name="deathLink">death link object to get relevant info from</param>
    /// <returns></returns>
    private string GetDeathLinkCause(DeathLink deathLink)
    {
        return $"Received death from {deathLink.Source}";
    }

    /// <summary>
    /// Called from <see cref="Patches.PlayerHealthDeathLinkSendPatch"/> on the one frame the local
    /// player's death is visible, whatever caused it.
    /// </summary>
    /// <param name="playerHealth">The local player's PlayerHealth, read for what killed them.</param>
    public void LocalPlayerDied(PlayerHealth playerHealth)
    {
        try
        {
            // The death we caused ourselves, coming back around. Consumed rather than merely
            // tested, so the next real death is shared normally.
            if (suppressNextDeath)
            {
                suppressNextDeath = false;
                return;
            }

            // Counted, not sent, until the room's deaths_per_link is reached. Gated here rather
            // than inside SendDeathLink so that the traps and any future caller that means "send
            // this death now" still do exactly that; this is only the rule for deaths of ours.
            //
            // Ahead of the count, not after it: deaths taken while unlinked must not build up
            // progress that fires the moment DeathLink is switched back on.
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
    /// receives it. The two flags are the same ones PlayerHealth.Update reads for its own feed.
    /// </summary>
    private string DescribeDeath(PlayerHealth playerHealth)
    {
        string playerName = KillFeed.LocalPlayerName;

        if (playerHealth == null) return $"{playerName} died in STRAFTAT";
        if (playerHealth.fellVoid) return $"{playerName} fell into the void";
        if (playerHealth.suicide) return $"{playerName} killed themselves";

        return $"{playerName} was killed in STRAFTAT";
    }

    /// <summary>
    /// called to send a death link to the multiworld
    /// </summary>
    /// <param name="cause">what killed the player, shown by the worlds that receive it. Null falls
    /// back to the bare slot name.</param>
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
