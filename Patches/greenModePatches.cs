using System;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Utils;

namespace Straftapelago.Finnegan_McD.org.Patches;

/// <summary>
/// Puts the Green Mode tint on each player camera as it comes up. See <see cref="GreenModeTint"/>
/// for what the tint actually does. A postfix, because colorGrading is pulled off the camera's
/// post-process profile inside vanilla's own body and is only non-null once it has returned.
/// </summary>
[HarmonyPatch(typeof(FirstPersonController), "Awake")]
public class FirstPersonControllerGreenModePatch
{
    static void Postfix(FirstPersonController __instance)
    {
        try
        {
            GreenModeTint.Apply(__instance);
        }
        catch (Exception e)
        {
            // Swallowed: this runs inside the player's Awake, and losing a camera tint must not
            // abandon the rest of it.
            Plugin.BepinLogger.LogError($"[GreenMode] failed to tint a player camera{Environment.NewLine}{e}");
        }
    }
}
