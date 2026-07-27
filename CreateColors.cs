using UnityEngine;

namespace Straftapelago.Finnegan_McD.org;

public class CreateColors : MonoBehaviour
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

    void Start()
    {
        Shader weaponShader = Shader.Find("S_WeaponOutline_00");
        foreach (Renderer renderer in GetComponentsInChildren<Renderer>())
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

            foreach (Material mat in renderer.materials)
            {
                mat.shader = weaponShader;

                if (mat.HasProperty("_BC"))
                {
                    Texture2D texture = new Texture2D(1, 1);
                    texture.SetPixel(0, 0, Colors[colorIndex]);
                    texture.Apply();
                    mat.SetTexture("_BC", texture);
                }
            }
        }
    }
}
