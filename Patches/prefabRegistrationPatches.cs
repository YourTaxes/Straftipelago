using FishNet;
using FishNet.Managing;
using FishNet.Managing.Object;
using FishNet.Object;
using HarmonyLib;

namespace Straftapelago.Finnegan_McD.org.Patches;

/// <summary>
/// Puts the AssetBundle-loaded roulette prefab into FishNet's spawnable prefab table on every
/// peer, early enough that any incoming spawn message can resolve it.
/// </summary>
internal static class RoulettePrefabRegistration
{
    /// <summary>
    /// Idempotent: AddObject with checkForDuplicates skips a prefab already in the list, and
    /// re-runs InitializePrefabRange either way, so calling this more often than strictly
    /// needed costs a Contains plus one pass over the table. Only a call that actually grew
    /// the table logs, so the backstop callers stay silent once the primary hook has done the
    /// work.
    /// </summary>
    internal static void EnsureRegistered(NetworkManager networkManager, string reason)
    {
        if (DiagnosticFlags.SkipPrefabRegistration)
        {
            DiagLog.Log("PrefabRegistration",
                $"SKIPPED via DiagnosticFlags.SkipPrefabRegistration (reason={reason})");
            return;
        }

        NetworkObject rouletteNob = Plugin.RouletteItemPrefab == null
            ? null
            : Plugin.RouletteItemPrefab.GetComponent<NetworkObject>();
        PrefabObjects spawnables = networkManager == null ? null : networkManager.SpawnablePrefabs;

        if (rouletteNob == null || spawnables == null)
        {
            // If this ever appears in a client log, that client is one entry short and every
            // roulette spawn it receives will fail to resolve.
            DiagLog.Log("PrefabRegistration",
                $"!! SKIPPED - table not mutated on this peer. {DiagLog.NetRoles()} reason={reason} " +
                $"NetworkManager={(networkManager == null ? "NULL" : "ok")} " +
                $"SpawnablePrefabs={(spawnables == null ? "NULL" : "ok")} " +
                $"rouletteNob={(rouletteNob == null ? "NULL" : "ok")}");
            return;
        }

        int countBefore = spawnables.GetObjectCount();
        spawnables.AddObject(rouletteNob, true);
        int countAfter = spawnables.GetObjectCount();

        if (countAfter == countBefore) return;

        // Compare this line between the host's log and the client's: the PrefabId and the
        // counts have to match, and both have to be printed before either peer's first
        // roulette spawn.
        DiagLog.Log("PrefabRegistration",
            $"registered on this peer. {DiagLog.NetRoles()} reason={reason} " +
            $"countBefore={countBefore} countAfter={countAfter} " +
            $"PrefabId={rouletteNob.PrefabId} CollectionId={rouletteNob.SpawnableCollectionId}");
    }

    internal static void EnsureRegistered(string reason) =>
        EnsureRegistered(InstanceFinder.NetworkManager, reason);
}

/// <summary>
/// The primary registration point. See RoulettePrefabRegistration for why it has to be this
/// early.
/// </summary>
[HarmonyPatch(typeof(NetworkManager), "Awake")]
public class NetworkManagerAwakePatch
{
    static void Postfix(NetworkManager __instance)
    {
        // Awake has three early returns, no SpawnablePrefabs assigned, and the two
        // can't-persist paths that leave this instance about to be destroyed, and a postfix
        // runs after all of them. Initialized is set true only on the path that also ran
        // InitializePrefabRange and SetCollectionId, so it is the one flag that says "this
        // table is real, and appending to it means something".
        if (!__instance.Initialized) return;

        RoulettePrefabRegistration.EnsureRegistered(__instance, "NetworkManager.Awake");
    }
}

/// <summary>
/// Backstop, and the place the invariant is easiest to state: OnLobbyEntered ends by calling
/// FishySteamworks.StartConnection, so this prefix is the last instruction that runs on either
/// peer before a socket exists. Host and client both pass through here. Silent when
/// NetworkManagerAwakePatch already registered, which is the expected case.
/// </summary>
[HarmonyPatch(typeof(SteamLobby), "OnLobbyEntered")]
public class SteamLobbyOnLobbyEnteredPatch
{
    static void Prefix()
    {
        RoulettePrefabRegistration.EnsureRegistered("SteamLobby.OnLobbyEntered");
    }
}
