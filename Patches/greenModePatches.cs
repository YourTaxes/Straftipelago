using System;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Utils;

namespace Straftapelago.Finnegan_McD.org.Patches;

/// <summary>
/// Puts the Green Mode tint on each player camera as it comes up. See <see cref="GreenModeTint"/>
/// for what the tint actually does.
/// </summary>
/// <remarks>
/// A postfix on Awake, not a prefix: FishNet's codegen splits the method, so vanilla's body
/// lives in <c>Awake___UserLogic()</c> and <c>Awake()</c> merely calls it between the two
/// network-initialize halves. <c>colorGrading</c> is pulled off the camera's post-process
/// profile inside that body, so it is only non-null once the original method has returned.
/// </remarks>
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
            // Swallowed on purpose. This runs inside the player's Awake, and an exception
            // escaping a Harmony patch there would abandon the rest of that Awake - losing a
            // camera tint must not cost the player their input bindings or their pause
            // manager reference.
            Plugin.BepinLogger.LogError($"[GreenMode] failed to tint a player camera{Environment.NewLine}{e}");
        }
    }
}
