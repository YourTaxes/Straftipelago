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

        // The prefab's own fields were set up once at load (see RouletteItemPrefabSetup), so
        // swapping the reference is the whole of the per-spawner work.
        __instance.itemToSpawn = Plugin.RouletteItemPrefab;
    }
}

/// <summary>
/// The scene assets the Roulette Item borrows from the game, resolved once per session. Each
/// sweep of Resources.FindObjectsOfTypeAll walks every loaded object, and a round spawns one
/// Roulette Item per spawner, so resolving per instance would repeat that walk many times at
/// every round start. A cached reference is looked up again only if Unity has destroyed it.
/// </summary>
internal static class RouletteItemAssets
{
    private static Sprite sprintCrosshair;
    private static Sprite standCrosshair;
    private static GameObject depopVfx;

    internal static Sprite SprintCrosshair
    {
        get
        {
            if (sprintCrosshair == null) sprintCrosshair = FindSprite("Straftat_Crosshair03_1");
            return sprintCrosshair;
        }
    }

    internal static Sprite StandCrosshair
    {
        get
        {
            if (standCrosshair == null) standCrosshair = FindSprite("Straftat_Crosshair02_0");
            return standCrosshair;
        }
    }

    internal static GameObject DepopVfx
    {
        get
        {
            if (depopVfx == null)
            {
                depopVfx = Resources.FindObjectsOfTypeAll<GameObject>()
                    .FirstOrDefault(candidate => candidate.name == "WFX_Explosion StarSmoke");
                if (depopVfx == null) Plugin.BepinLogger.LogError("Roulette Item: WFX_Explosion StarSmoke VFX not found");
            }

            return depopVfx;
        }
    }

    private static Sprite FindSprite(string spriteName)
    {
        Sprite sprite = Resources.FindObjectsOfTypeAll<Sprite>()
            .FirstOrDefault(candidate => candidate.name == spriteName);
        if (sprite == null) Plugin.BepinLogger.LogError($"Roulette Item crosshair sprite '{spriteName}' not found");
        return sprite;
    }
}

/// <summary>
/// Keeps a parentless item out of vanilla's idle-bob tween, and wires up the Roulette Item's
/// crosshairs and depop effect as it starts, disabling its Gun component. Its colours and
/// materials come from the prefab, which <see cref="RouletteItemPrefabSetup"/> painted at load.
/// </summary>
[HarmonyPatch(typeof(ItemBehaviour), "Start")]
public class StartPatches
{
    private static readonly AccessTools.FieldRef<ItemBehaviour, bool> DispenserStart =
        AccessTools.FieldRefAccess<ItemBehaviour, bool>("dispenserStart");

    private static readonly AccessTools.FieldRef<ItemBehaviour, Sprite> SprintCrosshair =
        AccessTools.FieldRefAccess<ItemBehaviour, Sprite>("sprintCrosshair");

    private static readonly AccessTools.FieldRef<ItemBehaviour, Sprite> StandCrosshair =
        AccessTools.FieldRefAccess<ItemBehaviour, Sprite>("standCrosshair");

    // Vanilla Start ends with transform.DOLocalMove(... transform.parent.up ...) whenever
    // dispenserStart is false, so it throws for any item with no parent. The server parents
    // its instance to the spawner, but the copies FishNet spawns on clients have no parent.
    static void Prefix(ItemBehaviour __instance)
    {
        if (__instance.transform.parent != null) return;

        ref bool dispenserStart = ref DispenserStart(__instance);
        if (dispenserStart) return;

        dispenserStart = true;
        DiagLog.Log("ItemBehaviour.Start",
            $"forced dispenserStart=true on parentless '{__instance.weaponName}' " +
            $"({__instance.gameObject.name}) — vanilla Start would have NREd on transform.parent.up");
    }

    static void Postfix(ItemBehaviour __instance)
    {
        if (__instance.weaponName != "Roulette Item") return;

        // Gun.Update dereferences fpArms, behaviour and rootObject through WeaponUpdate once
        // the item leaves layer 7, and the roulette wires up none of them. Disabling the
        // component stops Unity calling Update; field reads and the despawn path, which
        // invokes DespawnObject directly, are unaffected.
        Gun rouletteGun = __instance.GetComponent<Gun>();
        if (rouletteGun != null) rouletteGun.enabled = false;

        SprintCrosshair(__instance) = RouletteItemAssets.SprintCrosshair;
        StandCrosshair(__instance) = RouletteItemAssets.StandCrosshair;
        __instance.depopVFX = RouletteItemAssets.DepopVfx;
    }
}
