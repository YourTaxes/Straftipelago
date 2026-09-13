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

        // The prefab's own fields were set up once at load (see RouletteItemPrefabSetup), so
        // swapping the reference is the whole of the per-spawner work.
        __instance.itemToSpawn = Plugin.RouletteItemPrefab;
    }
}

/// <summary>
/// Replaces every item dispenser's spawn list with the Roulette Item alone, on the server, and
/// puts the roulette's weapon logo on the dispenser's "available" screen on every peer. The
/// same last-resort prefab registration as the spawner patch.
/// </summary>
[HarmonyPatch(typeof(ItemDispenser), "Start")]
public class ItemDispenserStartPatch
{
    private static readonly AccessTools.FieldRef<ItemDispenser, GameObject> AvailableScreen =
        AccessTools.FieldRefAccess<ItemDispenser, GameObject>("availableScreen");

    private static bool warnedMissingLogo;

    static void Prefix(ItemDispenser __instance)
    {
        RoulettePrefabRegistration.EnsureRegistered("ItemDispenser.Start");

        // On every peer, not just the server: the screen is local rendering and nothing syncs
        // its sprite. Inactive children included, since the dispenser may be showing its
        // loading screen when Start runs.
        GameObject availableScreen = AvailableScreen(__instance);
        if (Plugin.WeaponLogoSprite == null)
        {
            if (!warnedMissingLogo)
            {
                warnedMissingLogo = true;
                Plugin.BepinLogger.LogWarning("Dispenser screen: weapon logo sprite not loaded; keeping the vanilla icon.");
            }
        }
        else if (availableScreen != null)
        {
            foreach (SpriteRenderer renderer in availableScreen.GetComponentsInChildren<SpriteRenderer>(true))
                renderer.sprite = Plugin.WeaponLogoSprite;
        }

        if (!FishNet.InstanceFinder.IsServer) return;

        // SpawnItem picks Random.Range over this array, so a single entry makes the roll a
        // certainty. The prefab's own fields were set up once at load (see
        // RouletteItemPrefabSetup), as for the spawner.
        __instance.itemsToSpawn = new[] { Plugin.RouletteItemPrefab };
    }
}
