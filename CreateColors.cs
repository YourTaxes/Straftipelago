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
        int index = 0;
        Shader weaponShader = Shader.Find("S_WeaponOutline_00");
        foreach (Renderer renderer in GetComponentsInChildren<Renderer>())
        {
            foreach (Material mat in renderer.materials)
            {
                mat.shader = weaponShader;

                if (mat.HasProperty("_BC"))
                {
                    Texture2D texture = new Texture2D(1, 1);
                    texture.SetPixel(0, 0, Colors[index % Colors.Length]);
                    texture.Apply();
                    mat.SetTexture("_BC", texture);
                }
                index++;
            }
        }
    }
}
