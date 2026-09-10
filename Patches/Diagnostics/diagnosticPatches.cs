using System.Diagnostics;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Utils;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

// Read-and-log only. Every prefix here returns void, so the original method still runs and
// still behaves exactly as it does without the mod.

/// <summary>
/// Logs everything vanilla PlayerValues.Update dereferences: the playerClient sync var and its
/// voice chat source, the typing indicator, and the setup component.
/// </summary>
[HarmonyPatch(typeof(PlayerValues), "Update")]
public class PlayerValuesUpdateDiagPatch
{
    static void Prefix(PlayerValues __instance)
    {
        // FishNet's generated SyncAccessor_ property isn't expressible in C#; call the
        // generated getter directly, the same way the rest of the mod reads sync vars.
        ClientInstance client = __instance.sync___get_value_playerClient();
        Traverse t = Traverse.Create(__instance);

        DiagLog.LogOnChange(__instance, "PlayerValues.Update",
            $"obj={__instance.gameObject.name} IsOwner={__instance.IsOwner} {DiagLog.NetRoles()} " +
            $"playerClient={(client == null ? "NULL" : "ok")} " +
            $"playerClient.voiceChatSource={(client == null ? "n/a" : (client.voiceChatSource == null ? "NULL" : "ok"))} " +
            $"voiceChatSource={(t.Field("voiceChatSource").GetValue<AudioSource>() == null ? "NULL" : "ok")} " +
            $"typingIndicator={(__instance.typingIndicator == null ? "NULL" : "ok")} " +
            $"setup={(t.Field("setup").GetValue<PlayerSetup>() == null ? "NULL" : "ok")}");
    }
}

/// <summary>
/// Logs whether playerClient and setup existed by the time PlayerValues.Start finished.
/// </summary>
[HarmonyPatch(typeof(PlayerValues), "Start")]
public class PlayerValuesStartDiagPatch
{
    static void Postfix(PlayerValues __instance)
    {
        DiagLog.Log("PlayerValues.Start",
            $"obj={__instance.gameObject.name} IsOwner={__instance.IsOwner} {DiagLog.NetRoles()} " +
            $"playerClient={(__instance.sync___get_value_playerClient() == null ? "NULL" : "ok")} " +
            $"setup={(Traverse.Create(__instance).Field("setup").GetValue<PlayerSetup>() == null ? "NULL" : "ok")}");
    }
}

/// <summary>
/// Logs whether HUDTween.Start's one-shot cache of clientScript succeeded. It is never
/// re-read, so a null there means Update dereferences null for the object's whole lifetime.
/// </summary>
[HarmonyPatch(typeof(HUDTween), "Start")]
public class HUDTweenStartDiagPatch
{
    static void Postfix(HUDTween __instance)
    {
        PlayerValues pv = __instance.GetComponentInParent<PlayerValues>();
        DiagLog.Log("HUDTween.Start",
            $"obj={__instance.gameObject.name} {DiagLog.NetRoles()} " +
            $"parentPlayerValues={(pv == null ? "NULL" : "ok")} " +
            $"cachedClientScript={(Traverse.Create(__instance).Field("clientScript").GetValue<ClientInstance>() == null ? "NULL (will NRE every frame)" : "ok")}");
    }
}

/// <summary>
/// Logs everything vanilla HUDTween.Update dereferences: clientScript, hudUp/hudDown, and the
/// PauseManager singleton.
/// </summary>
[HarmonyPatch(typeof(HUDTween), "Update")]
public class HUDTweenUpdateDiagPatch
{
    static void Prefix(HUDTween __instance)
    {
        Traverse t = Traverse.Create(__instance);
        DiagLog.LogOnChange(__instance, "HUDTween.Update",
            $"obj={__instance.gameObject.name} {DiagLog.NetRoles()} " +
            $"clientScript={(t.Field("clientScript").GetValue<ClientInstance>() == null ? "NULL" : "ok")} " +
            $"hudUp={(t.Field("hudUp").GetValue<Transform>() == null ? "NULL" : "ok")} " +
            $"hudDown={(t.Field("hudDown").GetValue<Transform>() == null ? "NULL" : "ok")} " +
            $"PauseManager.Instance={(PauseManager.Instance == null ? "NULL" : "ok")}");
    }
}

/// <summary>
/// Timestamps every change to PlayerManager.player, which is what vanilla's "joined mid match"
/// check reads.
/// </summary>
[HarmonyPatch(typeof(PlayerManager), "Update")]
public class PlayerManagerPlayerDiagPatch
{
    static void Prefix(PlayerManager __instance)
    {
        // LogOnChange prints the first observation of every object, so a null here is normal
        // before SpawnPlayer has run and only means "joined mid match" if it stays null.
        DiagLog.LogOnChange(__instance, "PlayerManager.player",
            $"IsOwner={__instance.IsOwner} {DiagLog.NetRoles()} " +
            $"player={(__instance.player == null ? "null (fine before SpawnPlayer; only a problem if it stays null)" : "ok")} " +
            $"SpawnedObject={DiagLog.Describe(Traverse.Create(__instance).Field("SpawnedObject").GetValue<GameObject>())}");
    }
}

/// <summary>
/// Confirms whether SpawnPlayer ran on this peer, and with what owner. It is the only place
/// PlayerManager.player is ever assigned.
/// </summary>
[HarmonyPatch(typeof(PlayerManager), "SpawnPlayer", typeof(int), typeof(int), typeof(Vector3), typeof(Quaternion))]
public class PlayerManagerSpawnPlayerDiagPatch
{
    static void Postfix(PlayerManager __instance)
    {
        DiagLog.Log("PlayerManager.SpawnPlayer",
            $"ran on this peer. {DiagLog.NetRoles()} IsOwner={__instance.IsOwner} " +
            $"owner={(__instance.Owner == null ? "null" : __instance.Owner.ClientId.ToString())} " +
            $"player={(__instance.player == null ? "NULL" : "ok")}");
    }
}

/// <summary>
/// Logs an item's parent and dispenserStart as it starts. Vanilla's Start dereferences
/// <c>transform.parent.up</c> when dispenserStart is false, so parentless plus false is the
/// combination that throws.
/// </summary>
[HarmonyPatch(typeof(ItemBehaviour), "Start")]
public class ItemBehaviourStartDiagPatch
{
    static void Prefix(ItemBehaviour __instance)
    {
        DiagLog.Log("ItemBehaviour.Start",
            $"weapon={__instance.weaponName} obj={__instance.gameObject.name} " +
            $"id={__instance.GetInstanceID()} {DiagLog.NetRoles()} " +
            $"dispenserStart={Traverse.Create(__instance).Field("dispenserStart").GetValue<bool>()} " +
            $"parent={(__instance.transform.parent == null ? "NULL (Start will NRE unless dispenserStart)" : __instance.transform.parent.name)} " +
            $"layer={__instance.gameObject.layer}");
    }
}

/// <summary>
/// Logs the two references OnDrop can fall over on: the camera it ejects along, and the
/// weaponScript its first statement reads.
/// </summary>
[HarmonyPatch(typeof(ItemBehaviour), "OnDrop")]
public class ItemBehaviourOnDropDiagPatch
{
    static void Prefix(ItemBehaviour __instance, Camera tempCam)
    {
        DiagLog.Log("ItemBehaviour.OnDrop",
            $"weapon={__instance.weaponName} obj={__instance.gameObject.name} " +
            $"id={__instance.GetInstanceID()} {DiagLog.NetRoles()} " +
            $"tempCam={(tempCam == null ? "NULL (OnDrop would NRE)" : "ok")} " +
            $"weaponScript={(Traverse.Create(__instance).Field("weaponScript").GetValue<Weapon>() == null ? "NULL (vanilla OnDrop would NRE)" : "ok")} " +
            $"parent={DiagLog.Describe(__instance.transform.parent)} layer={__instance.gameObject.layer}");
    }
}

/// <summary>
/// Records the PlayerPickup fields that OnStartClient populates - cam, playerController,
/// RigBuilder and the three pickup-position arrays - which both the drop path and the roll's
/// equip path need.
/// </summary>
[HarmonyPatch(typeof(PlayerPickup), "OnStartClient")]
public class PlayerPickupOnStartClientDiagPatch
{
    static void Postfix(PlayerPickup __instance)
    {
        DiagLog.Log("PlayerPickup.OnStartClient", SnapshotFields(__instance, "completed"));
    }

    /// <summary>Shared with the RightHandFix probe so both report identically.</summary>
    public static string SnapshotFields(PlayerPickup pp, string note)
    {
        Traverse t = Traverse.Create(pp);
        Transform[] right = t.Field("pickupPositionRightHand").GetValue<Transform[]>();
        Transform[] left = t.Field("pickupPositionLeftHand").GetValue<Transform[]>();
        Transform[] both = pp.pickupPositionBothHand;

        return $"{note} obj={pp.gameObject.name} IsOwner={pp.IsOwner} {DiagLog.NetRoles()} " +
               $"cam={(t.Field("cam").GetValue<Camera>() == null ? "NULL" : "ok")} " +
               $"playerController={(t.Field("playerController").GetValue<FirstPersonController>() == null ? "NULL" : "ok")} " +
               $"RigBuilder={(t.Field("RigBuilder").GetValue() == null ? "NULL" : "ok")} " +
               $"pickupRight={(right == null ? "NULL" : right.Length.ToString())} " +
               $"pickupLeft={(left == null ? "NULL" : left.Length.ToString())} " +
               $"pickupBoth={(both == null ? "NULL" : both.Length.ToString())}";
    }
}

/// <summary>
/// Logs what RightHandFix is about to do. It force-drops whatever is in hand when that
/// object's layer is 7 or 9, which is the layer the roulette's own despawn sets.
/// </summary>
[HarmonyPatch(typeof(PlayerPickup), "RightHandFix")]
public class RightHandFixDiagPatch
{
    static void Prefix(PlayerPickup __instance)
    {
        GameObject inHand = __instance.sync___get_value_objInHand();
        bool hasInHand = __instance.sync___get_value_hasObjectInHand();

        // Only interesting when it is actually about to do something.
        if (!hasInHand) return;

        DiagLog.LogOnChange(__instance, "PlayerPickup.RightHandFix",
            $"hasObjectInHand={hasInHand} objInHand={DiagLog.Describe(inHand)} " +
            $"layer={(inHand == null ? -1 : inHand.layer)} " +
            $"willForceDrop={(inHand != null && (inHand.layer == 7 || inHand.layer == 9))} | " +
            PlayerPickupOnStartClientDiagPatch.SnapshotFields(__instance, "fields:"));
    }
}

/// <summary>
/// Times the whole of ItemSpawner.Start, mod prefix and vanilla body together. __state carries
/// the timer from prefix to postfix, so several spawners initialising in sequence each get
/// their own.
/// </summary>
[HarmonyPatch(typeof(ItemSpawner), "Start")]
public class ItemSpawnerStartTimingDiagPatch
{
    static void Prefix(ref Stopwatch __state) => __state = Stopwatch.StartNew();

    static void Postfix(ItemSpawner __instance, Stopwatch __state)
    {
        __state.Stop();
        DiagLog.Log("ItemSpawner.Start",
            $"spawner={__instance.gameObject.name} took {__state.Elapsed.TotalMilliseconds:F2}ms " +
            $"{DiagLog.NetRoles()}");
    }
}

/// <summary>
/// The server half of a roulette roll. A prefab crosses the wire as a positional PrefabId into
/// each peer's SpawnablePrefabs list, so diffing this line's prefabId against the client's
/// [RR:roll] prefabId is what proves the two tables agree.
/// </summary>
[HarmonyPatch(typeof(PlayerSpawnObject), "RpcLogic___SpawnObject_1585589339")]
public class PlayerSpawnObjectServerDiagPatch
{
    static void Postfix(PlayerSpawnObject __instance, GameObject obj, Transform player)
    {
        FishNet.Object.NetworkObject prefabNob = obj == null ? null : obj.GetComponent<FishNet.Object.NetworkObject>();

        // spawnedObject is assigned by the SetSpawnedObject observers rpc, which arrives on a
        // later tick, so reading as not-yet-set here is normal.
        GameObject spawned = __instance.spawnedObject;

        DiagLog.Log("RR:server-spawn",
            $"requestedBy={(__instance.Owner == null ? "null" : __instance.Owner.ClientId.ToString())} " +
            $"resolvedPrefab={DiagLog.Describe(obj)} " +
            $"prefabId={(prefabNob == null ? "NO-NETWORKOBJECT" : prefabNob.PrefabId.ToString())} " +
            $"collectionId={(prefabNob == null ? "n/a" : prefabNob.SpawnableCollectionId.ToString())} " +
            $"previousSpawnedObject={DiagLog.Describe(spawned)} " +
            $"position={(player == null ? "player NULL" : player.position.ToString())} " +
            $"{DiagLog.NetRoles()}");
    }
}

/// <summary>
/// Logs the fields WeaponAnimation's branches read, for every weapon fire, so a rolled
/// weapon's recoil animation can be diffed against a weapon picked up off the floor.
/// </summary>
[HarmonyPatch(typeof(Weapon), "WeaponAnimation")]
public class WeaponAnimationDebugPatch
{
    static void Prefix(Weapon __instance)
    {
        Traverse wt = Traverse.Create(__instance);
        Plugin.BepinLogger.LogInfo(
            $"[Roulette:WeaponAnimation] weapon={__instance.gameObject.name} layer={__instance.gameObject.layer} " +
            $"holdback={wt.Field("holdback").GetValue<bool>()} instantPush={wt.Field("instantPush").GetValue<bool>()} " +
            $"horizontalAnimation={wt.Field("horizontalAnimation").GetValue<bool>()} requireBothHands={__instance.requireBothHands} " +
            $"instantComebackOnFire={wt.Field("instantComebackOnFire").GetValue<bool>()} " +
            $"animationPunch={wt.Field("animationPunch").GetValue<Vector3>()} animationDuration={wt.Field("animationDuration").GetValue<float>()} " +
            $"animationVibrato={wt.Field("animationVibrato").GetValue<int>()} animationElasticity={wt.Field("animationElasticity").GetValue<float>()} " +
            $"fpArms={(__instance.fpArms == null ? "null" : __instance.fpArms.name)} elbowPivot={(__instance.elbowPivot == null ? "null" : __instance.elbowPivot.name)} " +
            $"transform.localPosition={__instance.transform.localPosition} transform.parent={(__instance.transform.parent == null ? "null" : __instance.transform.parent.name)} " +
            $"activeInHierarchy={__instance.gameObject.activeInHierarchy}");
    }
}

/// <summary>
/// Writes a round boundary into the log. ItemSpawner.StartNewRound fires once per spawner, so
/// <see cref="RoundGate"/> collapses that burst into one announcement per round.
/// </summary>
[HarmonyPatch(typeof(ItemSpawner), "StartNewRound")]
public class RoundMarkerDiagPatch
{
    private static int roundCounter;

    static void Prefix(ItemSpawner __instance)
    {
        if (RoundGate.ShouldAnnounce())
        {
            roundCounter++;
            Plugin.BepinLogger.LogInfo(
                $"========== ROUND {roundCounter} (frame={Time.frameCount}, {DiagLog.NetRoles()}) ==========");
        }
    }

    /// <summary>
    /// Collapses the per-spawner StartNewRound calls that all happen on the same frame into a
    /// single round announcement.
    /// </summary>
    private static class RoundGate
    {
        private static int lastAnnouncedFrame = -1;

        public static bool ShouldAnnounce()
        {
            if (Time.frameCount == lastAnnouncedFrame) return false;
            lastAnnouncedFrame = Time.frameCount;
            return true;
        }
    }
}
