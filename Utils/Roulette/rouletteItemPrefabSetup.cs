using HarmonyLib;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// One-time setup of the Roulette Item prefab asset: paints its renderers, one colour per name
/// prefix, through the game's own weapon outline shader, and clears <c>dispenserStart</c> so
/// vanilla's ItemBehaviour.Start treats a spawned copy as a floor item. Everything here writes
/// to the shared asset, so it runs once from Plugin.Awake and every instance inherits it.
/// </summary>
public static class RouletteItemPrefabSetup
{
    private static readonly Color[] Colors = new Color[]
    {
        new Color(0.0f, 0.0f, 0.0f, 1.0f), // Black
        new Color(0.7215686f, 0.2901961f, 0.003921569f, 1.0f), // orange
        new Color(0.6862745f, 0.1137255f, 0.1137255f, 1.0f), // Red
        new Color(0.1058824f, 0.4745098f, 0.1058824f, 1.0f), // Green
        new Color(0.3529412f, 0.1764706f, 0.3921569f, 1.0f), // purple
        new Color(0.05490196f, 0.05098039f, 0.1764706f, 1.0f), // navy
        new Color(0.5647059f, 0.4470588f, 0.07843138f, 1.0f), // yellow
    };

    /// <summary>
    /// One 1x1 texture per colour, built on first use and kept for the session. The shader
    /// reads its base colour from a texture, so a solid swatch is the smallest thing it accepts.
    /// </summary>
    private static Texture2D[] swatches;

    private static bool applied;

    public static void Apply(GameObject prefab)
    {
        if (applied || prefab == null) return;
        applied = true;

        Shader weaponShader = Shader.Find("S_WeaponOutline_00");
        foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
        {
            string prefix = renderer.name.Length >= 3 ? renderer.name.Substring(0, 3) : "";
            int colorIndex = prefix switch
            {
                "Bas" => 0,
                "Org" => 1,
                "Red" => 2,
                "Grn" => 3,
                "Pur" => 4,
                "Blu" => 5,
                "Yel" => 6,
                _ => -1
            };

            if (colorIndex == -1)
            {
                Plugin.BepinLogger.LogWarning($"Renderer '{renderer.name}' does not start with a recognized color prefix.");
                continue;
            }

            // sharedMaterials, not materials: the latter would instantiate copies on the prefab
            // and leave the bundle's own materials untouched, so instances would never see this.
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material == null) continue;

                material.shader = weaponShader;
                if (material.HasProperty("_BC")) material.SetTexture("_BC", Swatch(colorIndex));
            }
        }

        ItemBehaviour item = prefab.GetComponent<ItemBehaviour>();
        if (item != null) Traverse.Create(item).Field("dispenserStart").SetValue(false);
    }

    private static Texture2D Swatch(int colorIndex)
    {
        swatches ??= new Texture2D[Colors.Length];

        Texture2D swatch = swatches[colorIndex];
        if (swatch != null) return swatch;

        swatch = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        swatch.SetPixel(0, 0, Colors[colorIndex]);
        swatch.Apply();
        swatches[colorIndex] = swatch;
        return swatch;
    }
}
