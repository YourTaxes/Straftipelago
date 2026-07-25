using System.Runtime.CompilerServices;
using DG.Tweening;
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

        Vector3 spawnPos = __instance.transform.position + __instance.transform.forward * 5f;
        GameObject spawned = Object.Instantiate(Plugin.RouletteItemPrefab, spawnPos, Quaternion.identity);
        //Weapon weaponComponent = spawned.GetComponent<WeaponHandSpawner>();
        spawned.AddComponent<CreateColors>();
        ItemBehaviour itemBehaviour = spawned.AddComponent<ItemBehaviour>();
        itemBehaviour.weaponName = "Roulette Item";

        //spawned.AddComponent<BoxCollider>();
        spawned.AddComponent<Rigidbody>();
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

        Transform gripRight = null, gripLeft = null;
        foreach (Transform t in __instance.GetComponentsInChildren<Transform>())
        {
            if (t.name == "Grip_Right") gripRight = t;
            else if (t.name == "Grip_Left") gripLeft = t;
        }
        Plugin.BepinLogger.LogInfo($"Grip_Right: {gripRight}, Grip_Left: {gripLeft}");

        gripRight?.gameObject.AddComponent<Grip>();
        gripLeft?.gameObject.AddComponent<Grip>();
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
                    Plugin.BepinLogger.LogInfo($"Material {counter}: {mat.name}, Renderer: {renderer.name}, Shader: {mat.shader.name}");
                    allMaterials.Add(mat);
                }
            }
            Traverse.Create(__instance).Field("hoveredObjectMat").SetValue(allMaterials);
            Plugin.BepinLogger.LogInfo("Roulette Item materials set");
        }        
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
