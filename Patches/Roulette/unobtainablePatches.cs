using System;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Utils;

namespace Straftapelago.Finnegan_McD.org.Patches;

// Locked weapons cannot be taken off the floor: a weapon in unowned_items is one the player
// has not unlocked, so the only way to get it is a roulette roll. Both patches fail OPEN -
// anything RouletteState cannot match to a pool weapon behaves exactly as it does in vanilla.

/// <summary>
/// Blocks the interact key on a weapon the player has not unlocked. HandleInteraction is the
/// single entry point for that key and the only safe place to refuse: its two-handed branch
/// drops both hands before picking up, so blocking further down would empty the player's hands
/// and hand back nothing.
/// </summary>
[HarmonyPatch(typeof(PlayerPickup), "HandleInteraction")]
public class UnobtainableInteractionPatch
{
    static bool Prefix(PlayerPickup __instance)
    {
        try
        {
            // Runs for every PlayerPickup on this peer; only the local player's input is this
            // player's to refuse.
            if (!__instance.IsOwner) return true;

            RouletteState roulette = Plugin.RouletteState;
            if (roulette == null) return true;

            Interactable focused = Traverse.Create(__instance)
                .Field("currentInteractable").GetValue<Interactable>();
            if (focused == null) return true;

            return !roulette.IsUnobtainable(focused.GetComponent<ItemBehaviour>());
        }
        catch (Exception error)
        {
            // Never let a throw here cost the player their interact key entirely.
            Plugin.BepinLogger.LogError($"[Unobtainable] HandleInteraction prefix failed: {error}");
            return true;
        }
    }
}

/// <summary>
/// Says so on the look-at popup, replacing the interact-key prompt vanilla writes there. The
/// whole string is rebuilt from weaponName rather than edited, so it stays correct if that
/// prompt's formatting changes.
/// </summary>
[HarmonyPatch(typeof(ItemBehaviour), "OnFocus")]
public class UnobtainableFocusPatch
{
    // OnFocus runs every frame the crosshair rests on an item, so the answer for the item it
    // is resting on is kept, along with the popup line, until the gaze moves or the pools
    // change. The pool version is what invalidates it: a weapon unlocked mid-gaze must stop
    // reading as unobtainable.
    private static ItemBehaviour focusedItem;
    private static int focusedPoolVersion = -1;
    private static bool focusedUnobtainable;
    private static string focusedPopupText;

    static void Postfix(ItemBehaviour __instance)
    {
        try
        {
            RouletteState roulette = Plugin.RouletteState;
            if (roulette == null) return;

            if (!ReferenceEquals(__instance, focusedItem) || roulette.Version != focusedPoolVersion)
            {
                focusedItem = __instance;
                focusedPoolVersion = roulette.Version;
                focusedUnobtainable = roulette.IsUnobtainable(__instance);
                focusedPopupText = focusedUnobtainable ? $"{__instance.weaponName.ToLower()} - Unobtainable" : null;
            }

            if (!focusedUnobtainable) return;

            PauseManager pauseManager = PauseManager.Instance;
            if (pauseManager == null || pauseManager.grabPopup == null) return;

            pauseManager.grabPopup.text = focusedPopupText;
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[Unobtainable] OnFocus postfix failed: {error}");
        }
    }
}
