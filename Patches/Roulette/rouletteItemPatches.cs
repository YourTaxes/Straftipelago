using System.Collections.Generic;
using System.Linq;
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

        if (DiagnosticFlags.SkipRouletteSpawnerReplacement)
        {
            DiagLog.Log("SpawnerReplacement", "SKIPPED via DiagnosticFlags.SkipRouletteSpawnerReplacement");
            return;
        }

        string itemName = __instance.itemToSpawn?.name ?? "null";
        __instance.itemToSpawn = Plugin.RouletteItemPrefab;
        ItemBehaviour item = __instance.itemToSpawn.GetComponent<ItemBehaviour>();
        Traverse t = Traverse.Create(item);
        t.Field("dispenserStart").SetValue(false);

        // `item` is the component on the shared prefab asset, not on a scene instance, so this
        // write reaches every roulette ever spawned. scene==null below is what says so.
        DiagLog.Log("SpawnerReplacement",
            $"spawner={__instance.gameObject.name} replaced '{itemName}' with roulette prefab. " +
            $"mutatedObject={item.gameObject.name} " +
            $"isPrefabAsset={(!item.gameObject.scene.IsValid() ? "YES (write is global)" : "no (scene instance)")} " +
            $"dispenserStart now={t.Field("dispenserStart").GetValue<bool>()}");
    }
}

/// <summary>
/// Probe point on the spawn itself: reads the prefab's dispenserStart as each item is spawned.
/// </summary>
[HarmonyPatch(typeof(ItemSpawner), "Spawn")]
public class ItemSpawnerSpawnPatch
{
    static void Prefix(ItemSpawner __instance)
    {
        ItemBehaviour item = __instance.itemToSpawn.GetComponent<ItemBehaviour>();
        bool dispenserStart = Traverse.Create(item).Field("dispenserStart").GetValue<bool>();
    }
}

/// <summary>
/// Keeps a parentless item out of vanilla's idle-bob tween, and wires up the Roulette Item's
/// crosshairs, depop effect, colours and materials as it starts, disabling its Gun component.
/// </summary>
[HarmonyPatch(typeof(ItemBehaviour), "Start")]
public class StartPatches
{
    // Vanilla Start ends with transform.DOLocalMove(... transform.parent.up ...) whenever
    // dispenserStart is false, so it throws for any item with no parent. The server parents
    // its instance to the spawner, but the copies FishNet spawns on clients have no parent.
    static void Prefix(ItemBehaviour __instance)
    {
        if (__instance.transform.parent != null) return;

        Traverse dispenserStart = Traverse.Create(__instance).Field("dispenserStart");
        if (dispenserStart.GetValue<bool>()) return;

        dispenserStart.SetValue(true);
        DiagLog.Log("ItemBehaviour.Start",
            $"forced dispenserStart=true on parentless '{__instance.weaponName}' " +
            $"({__instance.gameObject.name}) — vanilla Start would have NREd on transform.parent.up");
    }

    static void Postfix(ItemBehaviour __instance)
    {
        if (__instance.weaponName == "Roulette Item")
        {
            // Gun.Update dereferences fpArms, behaviour and rootObject through WeaponUpdate once
            // the item leaves layer 7, and the roulette wires up none of them. Disabling the
            // component stops Unity calling Update; field reads and the despawn path, which
            // invokes DespawnObject directly, are unaffected.
            Gun rouletteGun = __instance.GetComponent<Gun>();
            if (rouletteGun != null) rouletteGun.enabled = false;

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

            GameObject depopVfx = Resources.FindObjectsOfTypeAll<GameObject>()
                .FirstOrDefault(g => g.name == "WFX_Explosion StarSmoke");
            if (depopVfx == null)
            {
                Plugin.BepinLogger.LogError("Roulette Item: WFX_Explosion StarSmoke VFX not found");
            }
            __instance.depopVFX = depopVfx;

            CreateColors.Apply(__instance.gameObject);

            var allMaterials = new List<Material>();
            foreach (Renderer renderer in __instance.GetComponentsInChildren<Renderer>())
            {
                foreach (Material mat in renderer.materials)
                {
                    allMaterials.Add(mat);
                }
            }
            Traverse.Create(__instance).Field("hoveredObjectMat").SetValue(allMaterials);
        }
    }
}

