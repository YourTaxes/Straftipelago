using System;
using System.Linq;
using System.Runtime.CompilerServices;
using DG.Tweening;
using FishNet.Managing.Object;
using FishNet.Object;
using HarmonyLib;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;


//all patches for the itembehaviour class related to the roulette item. This is where the pickup and drop logic is handled.

[HarmonyPatch(typeof(ItemSpawner), "Start")]
public class ItemSpawnerStartPatch
{
    static void Prefix(ItemSpawner __instance)
    {
        Plugin.BepinLogger.LogInfo("ItemSpawner Start called");
        if (!FishNet.InstanceFinder.IsServer) return;

        // Spawn the roulette item at the item spawner's position
        string itemName = __instance.itemToSpawn?.name ?? "null";
        __instance.itemToSpawn = Plugin.RouletteItemPrefab;
        ItemBehaviour ib = __instance.itemToSpawn.GetComponent<ItemBehaviour>();
        Traverse t = Traverse.Create(ib);
        t.Field("dispenserStart").SetValue(false);
        Plugin.BepinLogger.LogInfo("dispenserStart is now " + t.Field("dispenserStart").GetValue<bool>());
        Plugin.BepinLogger.LogInfo("Replaced " + itemName + " spawnerwith Roulette Item Prefab");
        Plugin.BepinLogger.LogInfo("ItemSpawner Start called, spawning Roulette Item at " + __instance.transform.position);
    }
}

[HarmonyPatch(typeof(ItemSpawner), "Spawn")]
public class ItemSpawnerSpawnPatch
{
    static void Prefix(ItemSpawner __instance)
    {
        bool dispenserStart = Traverse.Create(__instance.itemToSpawn.GetComponent<ItemBehaviour>()).Field("dispenserStart").GetValue<bool>();
        Plugin.BepinLogger.LogInfo("dispenserStart is now " + dispenserStart);
    }
}

//all patches for the item behaviour class related to the roulette item. This is where the pickup and drop logic is handled.

[HarmonyPatch(typeof(ItemBehaviour), "OnGrab")]
public class GrabPatches
{
    static void Postfix(ItemBehaviour __instance)
    {
        Plugin.BepinLogger.LogInfo("OnGrab called on weapon " + __instance.weaponName);
        //have this modify the player pickup script, and make it do code nearly identical to the switch weapons script
        //this script shows how weapons are set in a person's hand.
    }
}


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
        float ejectForce = t.Field<float>("ejectForce").Value;
        float torqueForce = t.Field<float>("torqueForce").Value;
        rb.AddForce(tempCam.transform.forward * ejectForce, ForceMode.Impulse);
        rb.AddTorque(tempCam.transform.forward * torqueForce + __instance.transform.right * torqueForce, ForceMode.Impulse);

        return false;
    }
}

[HarmonyPatch(typeof(ItemBehaviour), "Start")]
public class StartPatches
{
    static void Postfix(ItemBehaviour __instance)
    {
        Plugin.BepinLogger.LogInfo("Start called on weapon " + __instance.weaponName);
        if (__instance.weaponName == "Roulette Item")
        {
            Plugin.BepinLogger.LogInfo("Roulette Item Start called");

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

            var allMaterials = new System.Collections.Generic.List<Material>();
            int counter = 0;
            Plugin.BepinLogger.LogInfo($"Renderer length is {__instance.GetComponentsInChildren<Renderer>().Length}");
            foreach (Renderer renderer in __instance.GetComponentsInChildren<Renderer>())
            {
                foreach (Material mat in renderer.materials)
                {
                    counter++;
                    //Plugin.BepinLogger.LogInfo($"Material {counter}: {mat.name}, Renderer: {renderer.name}, Shader: {mat.shader.name}");
                    allMaterials.Add(mat);
                }
            }
            Traverse.Create(__instance).Field("hoveredObjectMat").SetValue(allMaterials);
            Plugin.BepinLogger.LogInfo("Roulette Item materials set");
        }        
    }
}

// All patches for the gun class related to the roulette item.

[HarmonyPatch(typeof(Gun), "Update")]
public class RouletteGunUpdatePatch
{
    // The Roulette Item binds to the game's real Gun component (via the assetbundle's
    // stub script) so vanilla pickup/hold logic works, but none of Gun/Weapon's private
    // fields (fpArms, playerController, pauseManager, etc.) get wired up like a normal
    // spawned weapon, so its Update/WeaponUpdate NREs every frame. Skip it entirely.
    static bool Prefix(Gun __instance)
    {
        ItemBehaviour ib = __instance.GetComponent<ItemBehaviour>();
        return ib == null || ib.weaponName != "Roulette Item";
    }
}

//all overrides for the player pickup script related to the roulette item. This is where the pickup and drop logic is handled.

[HarmonyPatch(typeof(PlayerPickup), "Update")]
public class PlayerPickupUpdatePatch
{
    static bool Prefix(PlayerPickup __instance)
    {
        Traverse t = Traverse.Create(__instance);
        if (t.Field("weaponInHand").GetValue<Weapon>() != null) return true;

        GameObject objInHand = t.Method("sync___get_value_objInHand").GetValue<GameObject>();
        if (objInHand == null) return true;

        ItemBehaviour ib = objInHand.GetComponent<ItemBehaviour>();
        if (ib == null || ib.weaponName != "Roulette Item") return true;

        // weaponInHand IS set (the roulette item's assetbundle-bound Gun component), but none of
        // Gun/Weapon's fields (fpArms, camAnimScript, etc.) are wired up like a real weapon, so
        // vanilla RightHandFix/LeftHandFix would NRE dereferencing them — run Update manually instead.
        t.Method("UpdateIKPoistion").GetValue();

        if (!__instance.IsOwner) return false;

        // RightHandFix/LeftHandFix internally call RightHandDrop/LeftHandDrop which also
        // dereference weaponInHand — skip them when the roulette item is held
        t.Field("dropTimer").SetValue(t.Field("dropTimer").GetValue<float>() - Time.deltaTime);
        t.Field("interactTimer").SetValue(t.Field("interactTimer").GetValue<float>() - Time.deltaTime);

        if (!t.Method("sync___get_value_hasObjectInHand").GetValue<bool>())
        {
            var pc = t.Field("playerController").GetValue<FirstPersonController>();
            pc.movementFactor = 1f;
            pc.jumpFactor = 1f;
            pc.maxWallJumps = 1;
            pc.wallJumpFactor = 1f;
        }

        Camera cam = t.Field("cam").GetValue<Camera>();
        if (cam != null)
        {
            t.Method("HandleInteractionCheck").GetValue();
            t.Method("HandleInteractEnvironment").GetValue();
            t.Method("HandleAboubiGrab").GetValue();

            Animator animator = t.Field("animator").GetValue<Animator>();
            Animator globalAnimator = t.Field("globalAnimator").GetValue<Animator>();

            if (ib.rightHandAnim == "")
            {
                animator.SetBool("TwoHanded", false);
                animator.SetBool("DoubleHanded", false);
                animator.SetBool("RightHanded", true);
            }
            globalAnimator.SetBool("TwoHanded", false);
            globalAnimator.SetBool("DoubleSingle", false);
            globalAnimator.SetBool("SingleHanded", true);
            globalAnimator.SetBool("LeftHanded", false);
        }

        return false;
    }
}



[HarmonyPatch(typeof(PlayerPickup), "RpcLogic___DropObjectObserver_2127535046")]
public class DropObserverPatch
{
    static bool Prefix(PlayerPickup __instance, GameObject obj, bool rightHand)
    {
        ItemBehaviour ib = obj?.GetComponent<ItemBehaviour>();
        if (ib == null || ib.weaponName != "Roulette Item") return true;

        Traverse t = Traverse.Create(__instance);

        if (!__instance.IsOwner)
            ib.StickOnGroundObservers();

        obj.transform.DOKill(false);

        if (__instance.IsOwner)
        {
            PauseManager.Instance.MoveAmmoDisplay(false, rightHand);
            t.Field("playerController").GetValue<FirstPersonController>().isScopeAiming = false;
            if (rightHand)
            {
                t.Field("weaponInHand").SetValue(null);
                t.Field("behaviourInHand").SetValue(null);
            }
            else
            {
                t.Field("weaponInLeftHand").SetValue(null);
                t.Field("behaviourInLeftHand").SetValue(null);
            }
        }

        if (ib.rightHandAnim != "")
        {
            Animator anim = t.Field("animator").GetValue<Animator>();
            anim.SetBool(rightHand ? ib.rightHandAnim : ib.leftHandAnim, false);
        }

        object camAnimScript = t.Field("camAnimScript").GetValue();
        Traverse.Create(camAnimScript).Field("rotateBack").SetValue(true);

        ib.playerPickup = null;
        ib.playerController = null;
        ib.rootObject = null;
        ib.OnDrop(t.Field("cam").GetValue<Camera>());
        // skipped: obj.GetComponent<Weapon>().camAnimScript = null — no Weapon on roulette item
        ib.cam = null;
        obj.transform.parent = null;
        obj.transform.localScale = new Vector3(2f, 2f, 2f);
        ib.UnsetLayer();
        obj.layer = 7;
        object rigBuilder = t.Field("RigBuilder").GetValue();
        Traverse.Create(rigBuilder).Method("Build").GetValue();

        return false;
    }
}

[HarmonyPatch(typeof(PlayerPickup), "RightHandPickup")]
public class RightHandPickupPatch
{
    static void DoSingleHandPickup(PlayerPickup instance, Traverse t, ItemBehaviour ib, Camera cam)
    {
        Transform[] pickupPos = t.Field("pickupPositionRightHand").GetValue<Transform[]>();
        t.Method("SetObjectInHandServer",
            instance.sync___get_value_objInHand(),
            pickupPos[ib.camChildIndex].position,
            pickupPos[ib.camChildIndex].rotation,
            cam.gameObject,
            true).GetValue();
        t.Method("SetRightIKTarget", ib.gripRight).GetValue();
        object rigBuilder = t.Field("RigBuilder").GetValue();
        Traverse.Create(rigBuilder).Method("Build").GetValue();
    }

    static bool Prefix(PlayerPickup __instance)
    {
        Traverse t = Traverse.Create(__instance);
        Camera cam = t.Field("cam").GetValue<Camera>();
        float interactionDistance = t.Field("interactionDistance").GetValue<float>();
        LayerMask interactionLayer = t.Field("interactionLayer").GetValue<LayerMask>();
        float sphereRadius = t.Field("sphereRadius").GetValue<float>();
        float currentHitDistance = t.Field("currentHitDistance").GetValue<float>();

        GameObject hitObj = null;
        RaycastHit hit, hit2;
        if (Physics.Raycast(cam.transform.position, cam.transform.forward, out hit, interactionDistance, interactionLayer))
            hitObj = hit.transform.gameObject;
        else if (Physics.SphereCast(cam.transform.position, sphereRadius, cam.transform.forward, out hit2, currentHitDistance, interactionLayer))
            hitObj = hit2.transform.gameObject;

        if (hitObj == null || hitObj.GetComponent<Weapon>() != null) return true;
        ItemBehaviour ib = hitObj.GetComponent<ItemBehaviour>();
        if (ib?.weaponName != "Roulette Item") return true;
        
        __instance.LeftHandDrop();
        __instance.RightHandDrop();
        Plugin.BepinLogger.LogInfo("Picked up Roulette Item");
        // AudioClip pickupClip = t.Field("pickupClip").GetValue<AudioClip>();

        // if (!__instance.sync___get_value_hasObjectInHand() && hitObj.layer == 7)
        // {
        //     SoundManager.Instance.PlaySound(pickupClip);
        //     t.Method("sync___set_value_objInHand", hitObj, true).GetValue();
        //     t.Method("sync___set_value_hasObjectInHand", true, true).GetValue();
        //     DoSingleHandPickup(__instance, t, ib, cam);
        // }
        // else if (__instance.sync___get_value_hasObjectInHand())
        // {
        //     SoundManager.Instance.PlaySound(pickupClip);
        //     t.Method("RightHandDrop").GetValue();
        //     t.Method("sync___set_value_objInHand", hitObj, true).GetValue();
        //     t.Method("sync___set_value_hasObjectInHand", true, true).GetValue();
        //     DoSingleHandPickup(__instance, t, ib, cam);
        // }

        return false;
    }
}

//unused, initally used for testing the roulette item spawn on the player pickup script. This is now handled by the item spawner script.
/*
[HarmonyPatch(typeof(FirstPersonController), "Awake")]
public class FirstPersonControllerAwakePatch
{
    static void Postfix(FirstPersonController __instance)
    {
        Plugin.BepinLogger.LogInfo("FirstPersonController Awake called");
        if (Plugin.RouletteItemPrefab == null) 
        {
            Plugin.BepinLogger.LogError("RouletteItemPrefab is null");
            return;
        }

        if (!FishNet.InstanceFinder.IsServer) return;

        NetworkObject nob = Plugin.RouletteItemPrefab.GetComponent<NetworkObject>();
        if (nob != null)
        {
            DefaultPrefabObjects spawnables = FishNet.InstanceFinder.NetworkManager.SpawnablePrefabs as DefaultPrefabObjects;
            spawnables?.AddObject(nob, true);
        }

        Vector3 spawnPos = __instance.transform.position + __instance.transform.forward * 5f;
        GameObject spawned = UnityEngine.Object.Instantiate(Plugin.RouletteItemPrefab, spawnPos, Quaternion.identity);
        FishNet.InstanceFinder.ServerManager.Spawn(spawned);
    }
}
*/

//NEED TO USE THE Player pickup right hand positions to dictate where the item will be.

// [HarmonyPatch(typeof(PlayerPickup), "OnStartClient")]
// public class PlayerPickupOnStartClientPatch
// {
//     static bool Prefix(PlayerPickup __instance)
//     {
//         Transform[] pickupPositionRightHand = Traverse.Create(__instance).Field("pickupPositionRightHand").GetValue<Transform[]>();

//         GameObject rouletteRight = new("Roulette Right");
//         rouletteRight.transform.SetParent(pickupPositionRightHand[0], false);
//         rouletteRight.transform.localPosition = Vector3.zero; // TODO: set desired local position/rotation
//         rouletteRight.AddComponent<ItemPosition>();
//         Plugin.BepinLogger.LogInfo("Added ItemPosition to Roulette Right");
//         return true;
//     }
// }


//NOTE TO SELF ABOUT HOW TO SPAWN FROM A CHANGING LIST OF RANDDOM ITEMS - 
//THIS IS HOW THE ItemDispenser DOES IT -
// Token: 0x06000457 RID: 1111 RVA: 0x0001E466 File Offset: 0x0001C666
// private IEnumerator SpawnItem()
// {
//     yield return new WaitForSeconds(0.2f);
//     if (SpawnerManager.Instance.SyncAccessor_randomiseWeapons)
//     {
//         this.item = SpawnerManager.Instance.GetRandomSpawnableWeapon();
//     }
//     else
//     {
//         this.item = this.itemsToSpawn[UnityEngine.Random.Range(0, this.itemsToSpawn.Length)];
//         string text;
//         if (SpawnerManager.Instance.SyncAccessor_swapGuns && SpawnerManager.Instance.Swaps.TryGetValue(this.item.name, out text))
//         {
//             if (string.IsNullOrEmpty(text))
//             {
//                 yield break;
//             }
//             GameObject gameObject;
//             if (SpawnerManager.NameToWeaponDict.TryGetValue(text, out gameObject))
//             {
//                 this.item = gameObject;
//             }
//         }
//     }
//     if (this.item == null)
//     {
//         yield break;
//     }
//     this.SpawnWeapon(this.item);
//     yield break;
// }



/*
Plan for putting random item into player's hand 
1. roulette item will be single hand.
2. on pickup, it will do the method for throwing the gun away for being out of ammo,
    freeing up the hand that held the item. Save which hand is th eone with the item. 
3. then, create the random item as a gameobject, instantiate it, and then do the pickup code 
    after the raycast, and just have the hit object be the new item. 
4. if the item that was spawned was a 2 handed weapon and the player was already holding something, 
    then it will not do this, and just spawn the 2 handed weapon on the ground in front of the player.
5. because the out of ammo code is what is run, then the roulette item should be destroyed when it is thrown.

*/