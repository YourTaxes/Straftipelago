using HarmonyLib;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

[HarmonyPatch(typeof(PlayerPickup), "RightHandPickup")]
public class PickupPatches
{
    static void Postfix(PlayerPickup __instance)
    {
        Plugin.BepinLogger.LogInfo("RightHandPickup called");
    }
}




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
    static void Postfix(Shotgun __instance)
    {
        Plugin.BepinLogger.LogInfo($"Player killed with shotgun");
    }
}

[HarmonyPatch(typeof(MeleeWeapon), "KillServer")]
public class MeleeKillPatch
{
    static void Postfix(MeleeWeapon __instance)
    {
        Plugin.BepinLogger.LogInfo($"Player killed with melee weapon");
    }
}

[HarmonyPatch(typeof(Bubble), "SendKillLog")]
public class BubbleKillPatch
{
    static void Postfix(Bubble __instance)
    {
        //make bubble ramdomization an option in the yaml, considering it is extremely bugged
        Plugin.BepinLogger.LogInfo($"Player killed with bubble");
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