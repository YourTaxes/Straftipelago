using System.Runtime.CompilerServices;
using DG.Tweening;
using FishNet.Managing.Object;
using FishNet.Object;
using HarmonyLib;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

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
        GameObject spawned = Object.Instantiate(Plugin.RouletteItemPrefab, spawnPos, Quaternion.identity);
        FishNet.InstanceFinder.ServerManager.Spawn(spawned);
    }
}

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
    static void Prefix(ItemBehaviour __instance)
    {
        if (__instance.weaponName != "Roulette Item") return;
        Traverse.Create(__instance).Field("dispenserStart").SetValue(true);
    }

    static void Postfix(ItemBehaviour __instance)
    {
        Plugin.BepinLogger.LogInfo("Start called on weapon " + __instance.weaponName);
        if (__instance.weaponName == "Roulette Item")
        {
            Plugin.BepinLogger.LogInfo("Roulette Item Start called");
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

        AudioClip pickupClip = t.Field("pickupClip").GetValue<AudioClip>();

        if (!__instance.sync___get_value_hasObjectInHand() && hitObj.layer == 7)
        {
            SoundManager.Instance.PlaySound(pickupClip);
            t.Method("sync___set_value_objInHand", hitObj, true).GetValue();
            t.Method("sync___set_value_hasObjectInHand", true, true).GetValue();
            DoSingleHandPickup(__instance, t, ib, cam);
        }
        else if (__instance.sync___get_value_hasObjectInHand())
        {
            SoundManager.Instance.PlaySound(pickupClip);
            t.Method("RightHandDrop").GetValue();
            t.Method("sync___set_value_objInHand", hitObj, true).GetValue();
            t.Method("sync___set_value_hasObjectInHand", true, true).GetValue();
            DoSingleHandPickup(__instance, t, ib, cam);
        }

        return false;
    }
}

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

        // weaponInHand is null because roulette item has no Weapon component — run Update manually
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

[HarmonyPatch(typeof(ItemBehaviour), "KillAnimation")]
public class KillAnimationPatches
{
    static void Postfix(ItemBehaviour __instance)
    {
        Plugin.BepinLogger.LogInfo("KillAnimation called on weapon " + __instance.weaponName);
    }
}

// [HarmonyPatch(typeof(PlayerPickup), "HandleAboubiGrab")]
// public class HandleAboubiGrabPatches
// {
//     static void Postfix(PlayerPickup __instance)
//     {
//         Plugin.BepinLogger.LogInfo("grabbed aboubi");
//     }
// }

[HarmonyPatch(typeof(Claymore), "SendKillLog")]
public class ClaymoreKillPatch
{
    static void Postfix(Claymore __instance)
    {
        Plugin.BepinLogger.LogInfo($"Player killed with claymore");
    }
}

[HarmonyPatch(typeof(Shotgun), "KillServer")]
public class ShotgunKillPatch
{
    static void Postfix(ItemBehaviour __instance)
    {
        Plugin.BepinLogger.LogInfo($"Player killed with {__instance?.weaponName}");
    }
}

[HarmonyPatch(typeof(MeleeWeapon), "KillServer")]
public class MeleeKillPatch
{
    static void Postfix(MeleeWeapon __instance)
    {
        
        Plugin.BepinLogger.LogInfo($"Player killed with melee");
    }
}

[HarmonyPatch(typeof(Bubble), "SendKillLog")]
public class BubbleKillPatch
{
    static void Postfix(Bubble __instance)
    {
        //make bubble ramdomization an option in the yaml, considering it is extremely bugged
        Plugin.BepinLogger.LogInfo($"Player killed with Bublee");
    }
}

[HarmonyPatch(typeof(Minigun), "KillServer")]
public class MinigunKillPatch
{
    static void Postfix(Minigun __instance)
    {
        Plugin.BepinLogger.LogInfo($"Player killed with minigun");
    }
}

[HarmonyPatch(typeof(PhysicsGrenade), "SendKillLog")]
public class GrenadeKillPatch
{
    static void Postfix(PhysicsGrenade __instance)
    {
        Plugin.BepinLogger.LogInfo($"Player killed with grenade");
    }
}

[HarmonyPatch(typeof(Obus), "SendKillLog")]
public class ObusKillPatch
{
    static void Postfix(Obus __instance)
    {
        Plugin.BepinLogger.LogInfo($"Player killed with obus");
    }
}

[HarmonyPatch(typeof(ChargeGun), "KillServer")]
public class ChargeGunKillPatch
{
    static void Postfix(ChargeGun __instance)
    {
        Plugin.BepinLogger.LogInfo($"Player killed with charge gun");
    }
}

[HarmonyPatch(typeof(BeamGun), "KillServer")]
public class BeamGunKillPatch
{
    static void Postfix(BeamGun __instance)
    {
        Plugin.BepinLogger.LogInfo($"Player killed with Beam gun");
    }
}

[HarmonyPatch(typeof(ShrapnelBallistic), "SendKillLog")]
public class ShrapnelKillPatch
{
    static void Postfix(ShrapnelBallistic __instance)
    {
        ItemBehaviour behaviour = Traverse.Create(__instance).Field<ItemBehaviour>("behavior").Value;
        Plugin.BepinLogger.LogInfo($"Player killed with {behaviour?.weaponName}");
    }
}

[HarmonyPatch(typeof(PredictedProjectile), "SendKillLog")]
public class PredictedProjectileKillPatch
{
    static void Postfix(GameObject ___weapon)
    {
        ItemBehaviour behaviour = ___weapon?.GetComponent<ItemBehaviour>();
        Plugin.BepinLogger.LogInfo($"Player killed with {behaviour?.weaponName}");
    }
}

[HarmonyPatch(typeof(Gun), "KillServer")]
public class GunKillPatch
{
    static void Postfix(Gun __instance)
    {
        ItemBehaviour behaviour = Traverse.Create(__instance).Field<ItemBehaviour>("behaviour").Value;
        Plugin.BepinLogger.LogInfo($"Player killed with {behaviour?.weaponName}");
    }
}
