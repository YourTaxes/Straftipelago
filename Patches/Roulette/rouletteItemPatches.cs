using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Utils;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

/// <summary>
/// Replaces every item spawner's item with the Roulette Item, on the server. Also a
/// last-resort registration point for the roulette prefab: the real one is
/// NetworkManager.Awake, and this call normally finds the work already done.
/// </summary>
[HarmonyPatch(typeof(ItemSpawner), "Start")]
public class ItemSpawnerStartPatch
{
    static void Prefix(ItemSpawner __instance)
    {
        RoulettePrefabRegistration.EnsureRegistered("ItemSpawner.Start");

        if (!FishNet.InstanceFinder.IsServer) return;

        if (DiagnosticFlags.SkipRouletteSpawnerReplacement)
        {
            DiagLog.Log("SpawnerReplacement", "SKIPPED via DiagnosticFlags.SkipRouletteSpawnerReplacement");
            return;
        }

        string itemName = __instance.itemToSpawn?.name ?? "null";
        __instance.itemToSpawn = Plugin.RouletteItemPrefab;
        ItemBehaviour item = __instance.itemToSpawn.GetComponent<ItemBehaviour>();
        Traverse t = Traverse.Create(item);
        t.Field("dispenserStart").SetValue(false);

        // `item` is the component on the shared prefab asset, not on a scene instance, so this
        // write reaches every roulette ever spawned. scene==null below is what says so.
        DiagLog.Log("SpawnerReplacement",
            $"spawner={__instance.gameObject.name} replaced '{itemName}' with roulette prefab. " +
            $"mutatedObject={item.gameObject.name} " +
            $"isPrefabAsset={(!item.gameObject.scene.IsValid() ? "YES (write is global)" : "no (scene instance)")} " +
            $"dispenserStart now={t.Field("dispenserStart").GetValue<bool>()}");
    }
}

/// <summary>
/// Probe point on the spawn itself: reads the prefab's dispenserStart as each item is spawned.
/// </summary>
[HarmonyPatch(typeof(ItemSpawner), "Spawn")]
public class ItemSpawnerSpawnPatch
{
    static void Prefix(ItemSpawner __instance)
    {
        ItemBehaviour item = __instance.itemToSpawn.GetComponent<ItemBehaviour>();
        bool dispenserStart = Traverse.Create(item).Field("dispenserStart").GetValue<bool>();
    }
}

/// <summary>
/// Ejects the Roulette Item under its own physics when it is dropped, replacing vanilla's
/// body so the roulette's own rigidbody settings survive.
/// </summary>
[HarmonyPatch(typeof(ItemBehaviour), "OnDrop")]
public class OnDropPatch
{
    static bool Prefix(ItemBehaviour __instance, Camera tempCam)
    {
        if (__instance.weaponName != "Roulette Item") return true;

        Traverse t = Traverse.Create(__instance);
        t.Field("dispenserStart").SetValue(false);
        Rigidbody rb = __instance.GetComponent<Rigidbody>() ?? __instance.gameObject.AddComponent<Rigidbody>();
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.drag = 0f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        // Vanilla OnDrop assigns this private field; the vanilla body is skipped here, so
        // without this Update's "tempRb == null" branch rotates the item forever.
        t.Field("tempRb").SetValue(rb);
        float ejectForce = t.Field<float>("ejectForce").Value;
        float torqueForce = t.Field<float>("torqueForce").Value;
        rb.AddForce(tempCam.transform.forward * ejectForce, ForceMode.Impulse);
        rb.AddTorque(tempCam.transform.forward * torqueForce + __instance.transform.right * torqueForce, ForceMode.Impulse);

        return false;
    }
}

/// <summary>
/// Keeps a parentless item out of vanilla's idle-bob tween, and wires up the Roulette Item's
/// crosshairs, depop effect, colours and materials as it starts.
/// </summary>
[HarmonyPatch(typeof(ItemBehaviour), "Start")]
public class StartPatches
{
    // Vanilla Start ends with transform.DOLocalMove(... transform.parent.up ...) whenever
    // dispenserStart is false, so it throws for any item with no parent. The server parents
    // its instance to the spawner, but the copies FishNet spawns on clients have no parent.
    static void Prefix(ItemBehaviour __instance)
    {
        if (__instance.transform.parent != null) return;

        Traverse dispenserStart = Traverse.Create(__instance).Field("dispenserStart");
        if (dispenserStart.GetValue<bool>()) return;

        dispenserStart.SetValue(true);
        DiagLog.Log("ItemBehaviour.Start",
            $"forced dispenserStart=true on parentless '{__instance.weaponName}' " +
            $"({__instance.gameObject.name}) — vanilla Start would have NREd on transform.parent.up");
    }

    static void Postfix(ItemBehaviour __instance)
    {
        if (__instance.weaponName == "Roulette Item")
        {
            Sprite sprintCrosshair = Resources.FindObjectsOfTypeAll<Sprite>()
                .FirstOrDefault(s => s.name == "Straftat_Crosshair03_1");
            Sprite standCrosshair = Resources.FindObjectsOfTypeAll<Sprite>()
                .FirstOrDefault(s => s.name == "Straftat_Crosshair02_0");
            if (sprintCrosshair == null || standCrosshair == null)
            {
                Plugin.BepinLogger.LogError($"Roulette Item crosshair sprites not found (sprint: {sprintCrosshair != null}, stand: {standCrosshair != null})");
            }
            Traverse.Create(__instance).Field("sprintCrosshair").SetValue(sprintCrosshair);
            Traverse.Create(__instance).Field("standCrosshair").SetValue(standCrosshair);

            GameObject depopVfx = Resources.FindObjectsOfTypeAll<GameObject>()
                .FirstOrDefault(g => g.name == "WFX_Explosion StarSmoke");
            if (depopVfx == null)
            {
                Plugin.BepinLogger.LogError("Roulette Item: WFX_Explosion StarSmoke VFX not found");
            }
            __instance.depopVFX = depopVfx;

            CreateColors.Apply(__instance.gameObject);

            var allMaterials = new List<Material>();
            foreach (Renderer renderer in __instance.GetComponentsInChildren<Renderer>())
            {
                foreach (Material mat in renderer.materials)
                {
                    allMaterials.Add(mat);
                }
            }
            Traverse.Create(__instance).Field("hoveredObjectMat").SetValue(allMaterials);
        }
    }
}

/// <summary>
/// Skips Gun.Update for the Roulette Item. It binds to the game's real Gun component so
/// vanilla pickup and hold logic works, but none of Gun/Weapon's private fields are wired up
/// like a normal spawned weapon, so its Update would dereference null every frame.
/// </summary>
[HarmonyPatch(typeof(Gun), "Update")]
public class RouletteGunUpdatePatch
{
    static bool Prefix(Gun __instance)
    {
        ItemBehaviour ib = __instance.GetComponent<ItemBehaviour>();
        return ib == null || ib.weaponName != "Roulette Item";
    }
}

/// <summary>
/// The Roulette Item's own drop-observer path, replacing vanilla's body because that one
/// dereferences the weapon fields the roulette does not have.
/// </summary>
[HarmonyPatch(typeof(PlayerPickup), "RpcLogic___DropObjectObserver_2127535046")]
public class DropObserverPatch
{
    static bool Prefix(PlayerPickup __instance, GameObject obj, bool rightHand)
    {
        ItemBehaviour item = obj?.GetComponent<ItemBehaviour>();
        if (item == null || item.weaponName != "Roulette Item") return true;

        Traverse trav = Traverse.Create(__instance);

        if (!__instance.IsOwner)
            item.StickOnGroundObservers();

        obj.transform.DOKill(false);

        if (__instance.IsOwner)
        {
            PauseManager.Instance.MoveAmmoDisplay(false, rightHand);
            trav.Field("playerController").GetValue<FirstPersonController>().isScopeAiming = false;
            if (rightHand)
            {
                trav.Field("weaponInHand").SetValue(null);
                trav.Field("behaviourInHand").SetValue(null);
            }
            else
            {
                trav.Field("weaponInLeftHand").SetValue(null);
                trav.Field("behaviourInLeftHand").SetValue(null);
            }
        }

        if (item.rightHandAnim != "")
        {
            Animator anim = trav.Field("animator").GetValue<Animator>();
            anim.SetBool(rightHand ? item.rightHandAnim : item.leftHandAnim, false);
        }

        object camAnimScript = trav.Field("camAnimScript").GetValue();
        Traverse.Create(camAnimScript).Field("rotateBack").SetValue(true);

        item.playerPickup = null;
        item.playerController = null;
        item.rootObject = null;
        item.OnDrop(trav.Field("cam").GetValue<Camera>());
        item.cam = null;
        obj.transform.parent = null;
        obj.transform.localScale = new Vector3(2f, 2f, 2f);
        item.UnsetLayer();
        obj.layer = 7;
        object rigBuilder = trav.Field("RigBuilder").GetValue();
        Traverse.Create(rigBuilder).Method("Build").GetValue();

        return false;
    }
}

/// <summary>
/// Empties both hands before the Roulette Item is picked up, and skips vanilla's own
/// right-hand pickup for it.
/// </summary>
[HarmonyPatch(typeof(PlayerPickup), "RightHandPickup")]
public class RightHandPickupPatch
{
    static void DoSingleHandPickup(PlayerPickup instance, Traverse trav, ItemBehaviour item, Camera cam)
    {
        Transform[] pickupPos = trav.Field("pickupPositionRightHand").GetValue<Transform[]>();
        trav.Method("SetObjectInHandServer",
            instance.sync___get_value_objInHand(),
            pickupPos[item.camChildIndex].position,
            pickupPos[item.camChildIndex].rotation,
            cam.gameObject,
            true).GetValue();
        trav.Method("SetRightIKTarget", item.gripRight).GetValue();
        object rigBuilder = trav.Field("RigBuilder").GetValue();
        Traverse.Create(rigBuilder).Method("Build").GetValue();
    }

    static bool Prefix(PlayerPickup __instance)
    {
        Traverse trav = Traverse.Create(__instance);
        Camera cam = trav.Field("cam").GetValue<Camera>();
        float interactionDistance = trav.Field("interactionDistance").GetValue<float>();
        LayerMask interactionLayer = trav.Field("interactionLayer").GetValue<LayerMask>();
        float sphereRadius = trav.Field("sphereRadius").GetValue<float>();
        float currentHitDistance = trav.Field("currentHitDistance").GetValue<float>();

        GameObject hitObj = null;
        RaycastHit hit, hit2;
        if (Physics.Raycast(cam.transform.position, cam.transform.forward, out hit, interactionDistance, interactionLayer))
            hitObj = hit.transform.gameObject;
        else if (Physics.SphereCast(cam.transform.position, sphereRadius, cam.transform.forward, out hit2, currentHitDistance, interactionLayer))
            hitObj = hit2.transform.gameObject;

        if (hitObj == null || hitObj.GetComponent<Weapon>() != null) return true;
        ItemBehaviour item = hitObj.GetComponent<ItemBehaviour>();
        if (item?.weaponName != "Roulette Item") return true;

        __instance.LeftHandDrop();
        __instance.RightHandDrop();

        return false;
    }
}
