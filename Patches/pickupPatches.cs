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
