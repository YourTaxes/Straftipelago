using System.Collections;
using System.Collections.Generic;
using FishNet.Object;
using HarmonyLib;
using MyceliumNetworking;
using Steamworks;
using Straftapelago.Finnegan_McD.org.Patches;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// Carries a roulette roll from the player who made it to the host, and the resulting weapon's
/// object id back again, over Mycelium's Steam-P2P RPCs. Nothing else is ever sent - a peer's
/// unlocked-weapon pool stays entirely on its own machine. Mycelium is used because a client
/// may only invoke a ServerRpc on a NetworkObject it owns, and no vanilla ServerRpc that spawns
/// an arbitrary prefab lives on anything a client owns.
/// </summary>
internal class RouletteNet
{
    /// <summary>
    /// Mycelium routes RPCs by mod id so that two mods with same-named methods cannot call
    /// into each other. Arbitrary but must stay stable and unique to this mod.
    /// </summary>
    public const uint ModId = 0x53A7A901;

    /// <summary>
    /// How long a client waits for FishNet to deliver the spawned weapon after Mycelium has
    /// already said it exists. The two travel over different transports (Steam messaging vs
    /// FishySteamworks), so their arrival order is not guaranteed and the Mycelium reply can
    /// legitimately land first.
    /// </summary>
    private const int MaxWaitFrames = 180;

    private static RouletteNet instance;
    private static CoroutineRunner runner;

    public static void Install()
    {
        if (instance != null) return;
        instance = new RouletteNet();
        MyceliumNetwork.RegisterNetworkObject(instance, ModId);
        Plugin.BepinLogger.LogInfo($"[RouletteNet] registered CustomRPCs under mod id {ModId}");
    }

    /// <summary>Sends the roll to the host. Called on the peer that owns the grabbing player.</summary>
    public static bool RequestSpawn(int rollId, string weaponName, bool rightHand)
    {
        if (instance == null)
        {
            Plugin.BepinLogger.LogError($"[RR:send #{rollId}] RouletteNet.Install() never ran");
            return false;
        }
        if (!MyceliumNetwork.InLobby)
        {
            Plugin.BepinLogger.LogError($"[RR:send #{rollId}] not in a Steam lobby; cannot reach the host");
            return false;
        }

        MyceliumNetwork.RPCTarget(ModId, nameof(ServerSpawnRolledWeapon), MyceliumNetwork.LobbyHost,
            ReliableType.Reliable, rollId, weaponName, rightHand);
        return true;
    }

    // ---------------------------------------------------------------------
    // Host side
    // ---------------------------------------------------------------------

    /// <summary>
    /// Runs on the host. Resolves the requesting player, spawns the one weapon they rolled, and
    /// tells them its object id. The host never learns anything about that player's pool beyond
    /// this single weapon.
    /// </summary>
    [CustomRPC]
    public void ServerSpawnRolledWeapon(int rollId, string weaponName, bool rightHand, RPCInfo info)
    {
        try
        {
            if (!FishNet.InstanceFinder.IsServer)
            {
                DiagLog.Log("RR:server-spawn", $"#{rollId} ignored — this peer is not the server");
                return;
            }

            PlayerPickup pp = ResolvePickup(info.SenderSteamID.m_SteamID, out string who);
            // Name resolution ONLY, never a pool-membership test: this is the host answering for
            // someone else's roll, and the host's own lists say nothing about what that player
            // has unlocked.
            GameObject prefab = Plugin.RouletteState?.Lookup(weaponName);
            NetworkObject prefabNob = prefab == null ? null : prefab.GetComponent<NetworkObject>();

            // prefabId is logged here and on the requester's [RR:roll] line so the two can be
            // diffed across the two machines' logs. The weapon travels as a NAME, so a mismatch
            // means the peers disagree about the weapon list itself.
            DiagLog.Log("RR:server-spawn",
                $"#{rollId} requestedBy={info.SenderSteamID} resolvedPlayer={who} " +
                $"weaponName={weaponName} resolvedPrefab={DiagLog.Describe(prefab)} " +
                $"prefabId={(prefabNob == null ? "n/a" : prefabNob.PrefabId.ToString())} " +
                $"rightHand={rightHand}");

            if (pp == null || prefab == null) return;

            Transform player = pp.transform;
            GameObject spawned = Object.Instantiate(prefab,
                player.position + player.forward * 2f, Quaternion.identity);

            // Ownership goes to the requesting client, matching what the game does for any
            // picked-up item. Without it the weapon's own ServerRpcs (RemoveAmmo, KillServer)
            // would be called by a client that does not own it.
            FishNet.InstanceFinder.ServerManager.Spawn(spawned, pp.Owner);

            NetworkObject spawnedNob = spawned.GetComponent<NetworkObject>();
            if (spawnedNob == null)
            {
                Plugin.BepinLogger.LogError(
                    $"[RR:server-spawn #{rollId}] '{spawned.name}' has no NetworkObject; cannot tell the client about it");
                return;
            }

            DiagLog.Log("RR:server-spawn",
                $"#{rollId} spawned={spawned.name} spawnedObjectId={spawnedNob.ObjectId} " +
                $"owner={(pp.Owner == null ? "null" : pp.Owner.ClientId.ToString())} " +
                $"position={spawned.transform.position}");

            MyceliumNetwork.RPCTarget(ModId, nameof(ClientEquipRolledWeapon), info.SenderSteamID,
                ReliableType.Reliable, rollId, spawnedNob.ObjectId, rightHand);
        }
        catch (System.Exception e)
        {
            Plugin.BepinLogger.LogError($"[RR:server-spawn #{rollId}] THREW{System.Environment.NewLine}{e}");
        }
    }

    /// <summary>
    /// Steam id to that player's PlayerPickup, via the game's own player registry.
    /// <paramref name="detail"/> carries why a lookup failed, because every step here can
    /// legitimately be empty for a few frames around a spawn.
    /// </summary>
    private static PlayerPickup ResolvePickup(ulong steamId, out string detail)
    {
        Dictionary<int, ClientInstance> instances = ClientInstance.playerInstances;
        if (instances == null)
        {
            detail = "ClientInstance.playerInstances is null";
            return null;
        }

        foreach (KeyValuePair<int, ClientInstance> pair in instances)
        {
            ClientInstance client = pair.Value;
            if (client == null || client.PlayerSteamID != steamId) continue;

            PlayerManager spawner = client.PlayerSpawner;
            if (spawner == null)
            {
                detail = $"ClientInstance {pair.Key} has no PlayerSpawner";
                return null;
            }

            // SpawnedObject is private, hence Traverse; it is the player root that
            // PlayerManager.SpawnPlayer() assigns, and PlayerPickup lives on it.
            GameObject player = Traverse.Create(spawner).Field("SpawnedObject").GetValue<GameObject>();
            if (player == null)
            {
                detail = $"PlayerManager for client {pair.Key} has no SpawnedObject yet";
                return null;
            }

            PlayerPickup pickup = player.GetComponent<PlayerPickup>();
            detail = pickup == null
                ? $"player object '{player.name}' has no PlayerPickup"
                : $"clientId={pair.Key} player={player.name}";
            return pickup;
        }

        detail = $"no ClientInstance with PlayerSteamID={steamId} among {instances.Count} known players";
        return null;
    }

    // ---------------------------------------------------------------------
    // Requester side
    // ---------------------------------------------------------------------

    /// <summary>Runs back on the peer that rolled. The weapon now exists; put it in their hand.</summary>
    [CustomRPC]
    public void ClientEquipRolledWeapon(int rollId, int objectId, bool rightHand)
    {
        try
        {
            DiagLog.RR(rollId, "setspawned",
                $"spawnedObjectId={objectId} rightHand={rightHand} armed={PendingRoll.IsArmed} " +
                $"armedRollId={PendingRoll.RollId} {DiagLog.NetRoles()}");

            if (!PendingRoll.IsArmed || PendingRoll.RollId != rollId)
            {
                // Late or duplicate reply. Equipping now would put a weapon in hand out of
                // nowhere, so drop it rather than guess.
                DiagLog.RR(rollId, "setspawned", "ignored — no matching armed roll on this peer");
                return;
            }

            PlayerPickup pp = PendingRoll.Pickup;
            ItemBehaviour roulette = PendingRoll.Roulette;
            PendingRoll.LastStep = "setspawned";
            PendingRoll.Disarm();

            if (pp == null) return;
            Runner().StartCoroutine(EquipWhenSpawned(rollId, objectId, rightHand, pp, roulette));
        }
        catch (System.Exception e)
        {
            Plugin.BepinLogger.LogError($"[RR:setspawned #{rollId}] THREW{System.Environment.NewLine}{e}");
            PendingRoll.Disarm();
        }
    }

    private static IEnumerator EquipWhenSpawned(
        int rollId, int objectId, bool rightHand, PlayerPickup pp, ItemBehaviour roulette)
    {
        // Always give up at least one frame first. On the host, Mycelium delivers a message
        // addressed to itself synchronously, so without this yield the request, the spawn, the
        // reply and the equip would all run inside ItemBehaviour.OnGrab.
        yield return null;

        GameObject spawned = null;
        int waited = 0;
        for (; waited < MaxWaitFrames; waited++)
        {
            if (TryResolveSpawned(objectId, out spawned)) break;
            yield return null;
        }

        if (spawned == null)
        {
            DiagLog.RR(rollId, "timeout",
                $"waitedFrames={waited} lastStepReached=setspawned — the host says it spawned " +
                $"objectId={objectId} but FishNet never delivered it to this peer.");
            GrabPatches.DespawnRoulette(rollId, roulette);
            yield break;
        }

        DiagLog.RR(rollId, "setspawned",
            $"resolved locally after {waited} frame(s): {spawned.name} (objectId={objectId})");

        GrabPatches.EquipRolledWeapon(rollId, pp, rightHand, spawned);
        GrabPatches.DespawnRoulette(rollId, roulette);
    }

    /// <summary>
    /// Looks the spawned weapon up by object id. Checks the client table first and the server
    /// table second: on a listen server the local player is a clientHost, and rather than
    /// depend on exactly how FishNet mirrors spawns into the client table there, fall back to
    /// the table that definitely has it.
    /// </summary>
    private static bool TryResolveSpawned(int objectId, out GameObject spawned)
    {
        spawned = null;

        NetworkObject nob = null;
        Dictionary<int, NetworkObject> clientTable = FishNet.InstanceFinder.ClientManager?.Objects?.Spawned;
        if (clientTable != null) clientTable.TryGetValue(objectId, out nob);

        if (nob == null)
        {
            Dictionary<int, NetworkObject> serverTable = FishNet.InstanceFinder.ServerManager?.Objects?.Spawned;
            if (serverTable != null) serverTable.TryGetValue(objectId, out nob);
        }

        if (nob == null) return false;

        spawned = nob.gameObject;
        return true;
    }

    // ---------------------------------------------------------------------

    /// <summary>
    /// A MonoBehaviour to hang the wait coroutine off. Created lazily, on first use, which is
    /// always well after frame 0: a GameObject made any earlier would be destroyed by Unity's
    /// DontDestroyOnLoad reset when the first scene loads, the same way
    /// <see cref="ArchipelagoOverlay"/>'s host is.
    /// </summary>
    private static CoroutineRunner Runner()
    {
        if (runner != null) return runner;

        GameObject host = new GameObject("Straftapelago_RouletteNet");
        Object.DontDestroyOnLoad(host);
        runner = host.AddComponent<CoroutineRunner>();
        return runner;
    }

    private class CoroutineRunner : MonoBehaviour { }
}
