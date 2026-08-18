using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using DG.Tweening;
using FishNet.Managing.Object;
using FishNet.Object;
using HarmonyLib;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

// Shared roulette item pools, accessible from any patch class.
public static class RouletteState
{
    public static List<GameObject> obtained_Items = new();
    public static List<GameObject> unowned_items = new();
}

//all patches for the itembehaviour class related to the roulette item. This is where the pickup and drop logic is handled.

[HarmonyPatch(typeof(ItemSpawner), "Start")]
public class ItemSpawnerStartPatch
{
    static void Prefix(ItemSpawner __instance)
    {
        Plugin.BepinLogger.LogInfo("ItemSpawner Start called");

        // Register the AssetBundle-loaded roulette prefab with FishNet's spawnable
        // prefab table. Must run on every peer (host AND clients), not just the
        // server: each machine resolves incoming spawn messages against its own
        // local copy of this table, and this prefab was never part of the game's
        // build-time registration since it's loaded at runtime from our bundle.
        // checkForDuplicates makes this safe to call from every ItemSpawner.Start().
        NetworkObject rouletteNob = Plugin.RouletteItemPrefab.GetComponent<NetworkObject>();
        PrefabObjects spawnables = FishNet.InstanceFinder.NetworkManager?.SpawnablePrefabs;
        if (rouletteNob != null && spawnables != null)
        {
            spawnables.AddObject(rouletteNob, true);
            Plugin.BepinLogger.LogInfo($"Roulette Item prefab registered: PrefabId={rouletteNob.PrefabId}, CollectionId={rouletteNob.SpawnableCollectionId}");
        }

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
    static void Postfix(ItemBehaviour __instance, bool owner, bool rightHand)
    {
        Plugin.BepinLogger.LogInfo("OnGrab called on weapon " + __instance.weaponName);

        if (__instance.weaponName != "Roulette Item") return;
        if (!FishNet.InstanceFinder.IsServer) return;

        GameObject[] allWeapons = RouletteState.obtained_Items.ToArray();
        if (allWeapons == null || allWeapons.Length == 0)
        {
            Plugin.BepinLogger.LogError("RouletteState.obtained_Items is empty; cannot roll a random weapon for Roulette Item");
            return;
        }

        int randomIndex = UnityEngine.Random.Range(0, allWeapons.Length);
        GameObject weaponPrefab = allWeapons[randomIndex];
        if (weaponPrefab == null) return;

        // Step 1: arm the roulette's own Gun so it survives the hand-swap below without
        // despawning itself prematurely, and so it can later trigger a clean vanilla
        // out-of-ammo despawn instead of us destroying it directly.
        Gun rouletteGun = __instance.GetComponent<Gun>();
        if (rouletteGun == null)
        {
            Plugin.BepinLogger.LogError("Roulette Item has no Gun component; aborting roulette roll");
            return;
        }
        rouletteGun.sync___set_value_currentAmmo(1, true);
        Traverse.Create(rouletteGun).Field("inHandDespawn").SetValue(false);
        rouletteGun.noAmmoClicks = 1;

        // Step 2a: WeaponUpdate() normally refreshes rootObject/playerController from the
        // ItemBehaviour every frame, but it never runs for the Roulette Item
        // (RouletteGunUpdatePatch blocks Gun.Update() entirely), and Gun.Fire() (called at
        // the end, below) needs them. Cache them onto the Gun's own fields now, before
        // anything drops the roulette and nulls these fields on the ItemBehaviour.
        GameObject cachedRootObject = __instance.rootObject;
        FirstPersonController cachedPlayerController = __instance.playerController;
        if (cachedRootObject == null)
        {
            Plugin.BepinLogger.LogError("Roulette Item OnGrab: rootObject is null, cannot resolve PlayerPickup");
            return;
        }
        rouletteGun.rootObject = cachedRootObject;
        rouletteGun.playerController = cachedPlayerController;

        PlayerPickup pp = cachedRootObject.GetComponent<PlayerPickup>();
        Traverse ppT = Traverse.Create(pp);
        Camera cam = ppT.Field("cam").GetValue<Camera>();

        Transform playerTransform = cachedRootObject.transform;
        Vector3 spawnPos = playerTransform.position + playerTransform.forward * 5f;
        GameObject spawned = UnityEngine.Object.Instantiate(weaponPrefab, spawnPos, Quaternion.identity);

        // This weapon has no parent transform (unlike ItemSpawner.Spawn(), which instantiates
        // as a child of the spawner). ItemBehaviour.Start()'s idle-bob tween dereferences
        // transform.parent.up when dispenserStart is false, which NREs with no parent.
        // Forcing dispenserStart true (same flag DispenserDrop() sets for ejected items)
        // skips that tween and matches this item's actual "dispensed into the world" state.
        ItemBehaviour spawnedIb = spawned.GetComponent<ItemBehaviour>();
        Weapon spawnedWeapon = spawned.GetComponent<Weapon>();
        if (spawnedIb != null)
        {
            Traverse.Create(spawnedIb).Field("dispenserStart").SetValue(true);
        }

        FishNet.InstanceFinder.ServerManager.Spawn(spawned);

        Plugin.BepinLogger.LogInfo($"Roulette Item rolled weapon [{randomIndex}]: {weaponPrefab.name}");

        // Step 3a/3b: equip the rolled weapon straight into the roulette's hand, unless it
        // needs both hands and the other hand is already full (or the roulette is held
        // left-handed, which has no vanilla both-hands pickup path) — in that case leave it
        // spawned on the ground, same as the old behavior.
        bool requireBothHands = spawnedWeapon != null && spawnedWeapon.requireBothHands;
        bool otherHandOccupied = rightHand
            ? pp.sync___get_value_hasObjectInLeftHand()
            : ppT.Method("sync___get_value_hasObjectInHand").GetValue<bool>();
        bool mustGroundSpawn = spawnedIb == null || spawnedWeapon == null
            || (requireBothHands && (otherHandOccupied || !rightHand));

        if (!mustGroundSpawn)
        {
            Grip[] grips = spawned.GetComponentsInChildren<Grip>();
            Transform gripRightT = grips.Length > 0 ? grips[0].transform : null;
            Transform gripLeftT = grips.Length > 1 ? grips[1].transform : null;
            object rigBuilder = ppT.Field("RigBuilder").GetValue();

            if (requireBothHands)
            {
                pp.RightHandDrop();
                ppT.Method("sync___set_value_objInHand", spawned, true).GetValue();
                ppT.Method("sync___set_value_hasObjectInHand", true, true).GetValue();
                Transform[] bothHandPos = pp.pickupPositionBothHand;
                ppT.Method("SetObjectInHandServer", spawned,
                    bothHandPos[spawnedIb.camChildIndex].position,
                    bothHandPos[spawnedIb.camChildIndex].rotation,
                    cam.gameObject, true).GetValue();
                ppT.Method("SetRightIKTarget", gripRightT).GetValue();
                ppT.Method("SetLeftIKTarget", gripLeftT).GetValue();
                Traverse.Create(rigBuilder).Method("Build").GetValue();
            }
            else if (rightHand)
            {
                pp.RightHandDrop();
                ppT.Method("sync___set_value_objInHand", spawned, true).GetValue();
                ppT.Method("sync___set_value_hasObjectInHand", true, true).GetValue();
                Transform[] pickupPos = ppT.Field("pickupPositionRightHand").GetValue<Transform[]>();
                ppT.Method("SetObjectInHandServer", spawned,
                    pickupPos[spawnedIb.camChildIndex].position,
                    pickupPos[spawnedIb.camChildIndex].rotation,
                    cam.gameObject, true).GetValue();
                ppT.Method("SetRightIKTarget", gripRightT).GetValue();
                Traverse.Create(rigBuilder).Method("Build").GetValue();
            }
            else
            {
                pp.LeftHandDrop();
                ppT.Method("sync___set_value_objInLeftHand", spawned, true).GetValue();
                ppT.Method("sync___set_value_hasObjectInLeftHand", true, true).GetValue();
                Transform[] pickupPos = ppT.Field("pickupPositionLeftHand").GetValue<Transform[]>();
                ppT.Method("SetObjectInHandServer", spawned,
                    pickupPos[spawnedIb.camChildIndexLeftHand].position,
                    pickupPos[spawnedIb.camChildIndexLeftHand].rotation,
                    cam.gameObject, false).GetValue();
                ppT.Method("SetLeftIKTarget", gripLeftT).GetValue();
                Traverse.Create(rigBuilder).Method("Build").GetValue();
            }
        }
        else
        {
            Plugin.BepinLogger.LogInfo("Roulette Item: rolled weapon left on the ground (2-handed / hands full / left-hand roulette)");
        }

        // Step 2b (last) + 4: force the roulette's own out-of-ammo despawn. Uses the
        // cached rouletteGun reference, not __instance-derived state, since __instance's
        // own ItemBehaviour fields may already be null by now (RightHandDrop/LeftHandDrop
        // above, via DropObserverPatch, nulls ib.rootObject/.playerController/.cam).
        rouletteGun.sync___set_value_currentAmmo(-1, true);
        Traverse.Create(rouletteGun).Method("Fire").GetValue();

        __instance.StartCoroutine(DelayedRouletteDespawn(__instance, rouletteGun));
    }

    // The vanilla caller of OnGrab (PlayerPickup's SetObjectInHandObserver RPC logic)
    // force-sets obj.layer back to 8 immediately after OnGrab returns, regardless of what
    // happens in this postfix. Weapon.DespawnObject() refuses to despawn while layer is 8
    // or 9, so scheduling the despawn inline here would silently no-op forever. Deferring
    // by one frame lets that caller finish first before we force the layer back down.
    static System.Collections.IEnumerator DelayedRouletteDespawn(ItemBehaviour rouletteIb, Gun rouletteGun)
    {
        yield return null;
        if (rouletteIb == null || rouletteGun == null) yield break;
        rouletteIb.gameObject.layer = 7;
        rouletteIb.UnsetLayer();
        yield return new WaitForSeconds(0.65f);
        if (rouletteGun == null) yield break;
        Traverse.Create(rouletteGun).Method("DespawnObject").GetValue();
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
        // Vanilla OnDrop() assigns this private field; since we skip the vanilla body (return false)
        // it never gets set, leaving Update()'s "tempRb == null" perpetual transform.Rotate() active forever.
        t.Field("tempRb").SetValue(rb);
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

            CreateColors.Apply(__instance.gameObject);

            var allMaterials = new System.Collections.Generic.List<Material>();
            int counter = 0;
            Plugin.BepinLogger.LogInfo($"Renderer length is {__instance.GetComponentsInChildren<Renderer>().Length}");
            foreach (Renderer renderer in __instance.GetComponentsInChildren<Renderer>())
            {
                foreach (Material mat in renderer.materials)
                {
                    counter++;
                    Plugin.BepinLogger.LogInfo($"Material {counter}: {mat.name}, Renderer: {renderer.name}, Shader: {mat.shader.name}");
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

[HarmonyPatch(typeof(PlayerPickup), "Awake")]
public class PlayerPickupAwakePatch
{
    static void Prefix()
    {
        SpawnerManager.PopulateAllWeapons();
        GameObject[] allWeapons = SpawnerManager.AllWeapons;

        RouletteState.unowned_items.Clear();
        if (allWeapons != null)
        {
            RouletteState.unowned_items.AddRange(allWeapons);
        }

        if (RouletteState.unowned_items.Count > 0)
        {
            GameObject firstWeapon = RouletteState.unowned_items[0];
            RouletteState.unowned_items.RemoveAt(0);
            RouletteState.obtained_Items.Add(firstWeapon);
        }
    }
}

[HarmonyPatch(typeof(PlayerPickup), "Update")]
public class PlayerPickupUpdatePatch
{
    static bool Prefix(PlayerPickup __instance)
    {
        if (Input.GetKeyDown(KeyCode.P) && RouletteState.unowned_items.Count > 0)
        {
            int randomIndex = UnityEngine.Random.Range(0, RouletteState.unowned_items.Count);
            GameObject randomItem = RouletteState.unowned_items[randomIndex];
            RouletteState.unowned_items.RemoveAt(randomIndex);
            RouletteState.obtained_Items.Add(randomItem);
            Plugin.BepinLogger.LogInfo($"Added random item {randomItem.name} to obtained_Items. Remaining unowned_items: {RouletteState.unowned_items.Count}");
        }

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
1. the starting ammo for the weapon will be 1, and it will have it's Gun component's
     base.inHandDespawn set to false, noAmmoClicks is set to 1,
    so that when the item has no ammo, it will not despawn immedietly in the player's hand, 
    so that when it is on the ground it can finish running the code
    for setting the player's weapon in their hand.
2a. then save the reference to the hand that the gun is in now, so after it is dropped,
     it can still modify it.
2b. at the end of onGrab, it will set the gun's ammo to -1 and call Gun.Fire(), so that after 
    a set amount of time, it will despawn the item 
    using vanilla methods. 
3a. then, create the random item as a gameobject, instantiate it, and then do the pickup code 
    after the raycast, and just have the hit object be the new item. 
3b. if the item that was spawned was a 2 handed weapon and the player was already holding something, 
    then it will not do this, and just spawn the 2 handed weapon on the ground in front of the player.
4. because the out of ammo code is what is run, then the roulette item should be destroyed by this point.
*/

/*
The code in the Weapon class in WeaponUpdate() checks if the gun's ammo is 0, and the player is not holding it
This code destroys the gun after time seconds when despawn object is called. 

if (this.SyncAccessor_currentAmmo <= 0 && this.SyncAccessor_currentAmmo > -100 && base.gameObject.layer == 7)
		{
			base.Invoke("DespawnObject", 0.65f);
			this.sync___set_value_currentAmmo(-103, true);
		}

/*
Plan for choosing random item
1. each player has a list instance of gameobjects that they can randomly get.
2. every time they earn a new weapon, it is simpily added to the list.
3. when a roulette item is picked up it will choose the weapon from the list with the random(0, item_list.length)
4. the item is spawned in the player's hand using thr mehod from above. 
*/