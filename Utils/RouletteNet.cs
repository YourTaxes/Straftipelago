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
/// Carries a roulette roll from the player who made it to the host, and the resulting
/// weapon's object id back again. Nothing else is ever sent — a peer's unlocked-weapon pool
/// stays entirely on its own machine.
/// </summary>
/// <remarks>
/// <para>Why this is not done with the game's own RPCs. The requirement was to use vanilla
/// networking wherever possible, and the game does contain exactly the right shape:
/// <c>PlayerSpawnObject.SpawnObject</c> is a ServerRpc whose server body Instantiates,
/// <c>ServerManager.Spawn</c>s and answers the caller — and FishNet even serializes an
/// unspawned prefab by <c>PrefabId</c>, so a weapon prefab can be handed over by reference.
/// The catch is ownership: a client may only invoke a ServerRpc on a NetworkObject it owns,
/// and a runtime probe proved <c>PlayerSpawnObject</c> is <b>not a component on the player
/// prefab</b> (see the G1 line in PlayerPickupOnStartClientDiagPatch). Of every vanilla
/// ServerRpc that spawns an arbitrary prefab, none lives on anything a client owns:</para>
/// <list type="bullet">
/// <item><c>PlayerSpawnObject.SpawnObject</c> — the component does not exist on the player.</item>
/// <item><c>WeaponHandSpawner.SpawnObject</c> — only on the placeable mine/claymore weapons.</item>
/// <item><c>ItemDispenser.SpawnWeapon</c> — no ownership guard, so a client *can* call it, but
/// it spawns at the dispenser rather than at the player, needs a dispenser on the map, and
/// drags dispenser side effects along. Rejected as too fragile.</item>
/// </list>
/// <para>So the vanilla mechanism exists but is unreachable, and this uses Mycelium instead —
/// the same Steam-P2P RPC library several other STRAFTAT mods already depend on. It is a hard
/// dependency, declared on <see cref="Plugin"/>.</para>
/// <para>The remaining pure-vanilla option, if the dependency is ever unwanted: bind a
/// <c>WeaponHandSpawner</c> component onto the Roulette Item prefab in the asset bundle, the
/// same way <c>Gun</c> is already bound. The roulette is owned by whoever picked it up
/// (<c>PlayerPickup.HandleInteraction</c> calls the <c>GiveOwnerToObj</c> ServerRpc), so its
/// <c>SpawnObject(prefab, position, rotation)</c> would then be callable and would take a
/// spawn position directly. That needs a rebuild of the bundle in Unity, which this does not.</para>
/// </remarks>
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
    /// Runs on the host. Resolves the requesting player, spawns the one weapon they rolled,
    /// and tells them its object id. The host never learns anything about that player's pool
    /// beyond this single weapon.
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
            GameObject prefab = RouletteState.Lookup(weaponName);
            NetworkObject prefabNob = prefab == null ? null : prefab.GetComponent<NetworkObject>();

            // prefabId is logged here and on the requester's [RR:roll] line so the two can be
            // diffed across the two machines' logs. Under Mycelium the weapon travels as a
            // NAME rather than a FishNet PrefabId, so a mismatch here means the peers disagree
            // about the weapon list itself, not about the spawnable-prefab table.
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
            // picked-up item (PlayerPickup.HandleInteraction -> GiveOwnerToObj) and what
            // WeaponHandSpawner does for a placed one. Without it the weapon's own ServerRpcs
            // (RemoveAmmo, KillServer) would be called by a client that does not own it.
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
        // addressed to itself synchronously (SendBytes short-circuits when the target is the
        // local Steam id), so without this yield the request, the spawn, the reply and the
        // equip would all run INSIDE ItemBehaviour.OnGrab — putting back exactly the reentrant
        // swap this design removed, and with it the need for the LeftHandPickup override.
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
    /// always well after frame 0 — a GameObject made any earlier would be destroyed by Unity's
    /// DontDestroyOnLoad reset when the first scene loads (the same trap documented on
    /// <see cref="ArchipelagoOverlay"/>).
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
