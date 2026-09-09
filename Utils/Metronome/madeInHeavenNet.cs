using System;
using MyceliumNetworking;
using Steamworks;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// Tells the rest of the Straftat lobby that a Made in Heaven has gone off, and carries the
/// host's resync of a running one. Registered on the mod's Mycelium id, the same one
/// <see cref="RouletteNet"/> uses; Mycelium routes on (mod id, method name).
/// </summary>
internal class MadeInHeavenNet
{
    private static MadeInHeavenNet instance;

    public static void Install()
    {
        if (instance != null) return;

        instance = new MadeInHeavenNet();
        MyceliumNetwork.RegisterNetworkObject(instance, RouletteNet.ModId);
        Plugin.BepinLogger.LogInfo(
            $"[MadeInHeaven] registered CustomRPCs under mod id {RouletteNet.ModId}");
    }

    /// <summary>
    /// Broadcasts an activation to the lobby, reliably. Called on the machine the multiworld
    /// gave the buff to, straight after it has applied its own half.
    /// </summary>
    public static void Announce(string playerName, int seconds, float startTick, float endTick)
    {
        if (instance == null)
        {
            Plugin.BepinLogger.LogError(
                "[MadeInHeaven] MadeInHeavenNet.Install() never ran; the lobby will not hear about this");
            return;
        }

        if (!MyceliumNetwork.InLobby)
        {
            // Offline or solo. The local half has already happened, so there is simply nobody
            // to tell.
            Plugin.BepinLogger.LogInfo(
                "[MadeInHeaven] not in a Steam lobby, so the activation stays on this machine");
            return;
        }

        MyceliumNetwork.RPC(RouletteNet.ModId, nameof(ClientMadeInHeavenActivated),
            ReliableType.Reliable, playerName, seconds, startTick, endTick);
    }

    /// <summary>
    /// Runs on every machine in the lobby. The sender's own copy is dropped, because
    /// <see cref="MadeInHeaven.Receive"/> has already done its half.
    /// </summary>
    [CustomRPC]
    public void ClientMadeInHeavenActivated(string playerName, int seconds, float startTick,
        float endTick, RPCInfo info)
    {
        try
        {
            // A Mycelium broadcast comes back to its sender as well, so this check is
            // load-bearing rather than defensive.
            if (info.SenderSteamID == SteamUser.GetSteamID()) return;

            Plugin.BepinLogger.LogInfo(
                $"[MadeInHeaven] {playerName} ({info.SenderSteamID}) activated one for {seconds}s, " +
                $"beat {startTick}s -> {endTick}s");

            MadeInHeaven.ReceiveRemote(playerName, seconds, startTick, endTick);
        }
        catch (Exception error)
        {
            // Swallowed rather than thrown back into Mycelium's message pump, which is draining
            // every other mod's RPCs in the same loop.
            Plugin.BepinLogger.LogError(
                $"[MadeInHeaven] failed to apply a received activation{Environment.NewLine}{error}");
        }
    }

    /// <summary>
    /// Broadcasts the whole shape of a running Made in Heaven - what is left, its total, and
    /// both ends of its ramp. Sent by the host only, as a round ends.
    /// </summary>
    public static void Resync(float secondsRemaining, float totalSeconds, float startTick, float endTick)
    {
        if (instance == null || !MyceliumNetwork.InLobby) return;

        MyceliumNetwork.RPC(RouletteNet.ModId, nameof(ClientMadeInHeavenResync),
            ReliableType.Reliable, secondsRemaining, totalSeconds, startTick, endTick);
    }

    /// <summary>
    /// Runs on every machine in the lobby. Takes the host's remaining time as the truth, and
    /// only the host's.
    /// </summary>
    [CustomRPC]
    public void ClientMadeInHeavenResync(float secondsRemaining, float totalSeconds,
        float startTick, float endTick, RPCInfo info)
    {
        try
        {
            // The host's own copy of its own broadcast; it is the source of truth.
            if (info.SenderSteamID == SteamUser.GetSteamID()) return;

            // Only the host may say what the time is, or a client whose clock had drifted could
            // hand its drift to the whole lobby.
            if (info.SenderSteamID != MyceliumNetwork.LobbyHost)
            {
                Plugin.BepinLogger.LogWarning(
                    $"[MadeInHeaven] ignoring a resync from {info.SenderSteamID}, who is not the " +
                    $"lobby host ({MyceliumNetwork.LobbyHost})");
                return;
            }

            MadeInHeaven.AdoptRemaining(secondsRemaining, totalSeconds, startTick, endTick);
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError(
                $"[MadeInHeaven] failed to apply a resync{Environment.NewLine}{error}");
        }
    }
}
