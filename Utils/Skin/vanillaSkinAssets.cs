using System;
using TMPro;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

// Where the skin's two halves come from. The font is one of the game's own Font assets, which
// IMGUI can use directly even though the game draws its UI with TextMeshPro. The backdrop is
// drawn here rather than borrowed: the game ships no rectangular panel sprite - its menu frames
// are 3D geometry and its flat UI textures are full-screen art - so a panel-sized rect stretched
// out of one of those is a tapered blob.
internal static partial class VanillaSkin
{
    /// <summary>
    /// Font assets to try, best first. "centurygothic" is the game's UI face; Hussar is the
    /// remaining game font and is still closer to vanilla than Unity's Arial.
    /// </summary>
    private static readonly string[] BodyFontNames = { "centurygothic", "Hussar" };

    /// <summary>
    /// Header font assets, best first. GOTHICBI is the bold italic of the same family, which
    /// vanilla uses for headings. Falls back to the body font when none resolves.
    /// </summary>
    private static readonly string[] HeaderFontNames = { "GOTHICBI", "centurygothic" };

    /// <summary>
    /// Game textures to use as the panel backdrop instead of the drawn one, best first. Empty
    /// on purpose, and kept as the seam for a real panel sprite once there is one.
    /// </summary>
    private static readonly string[] BackdropNames = { };

    private static void Resolve()
    {
        resolved = true;

        bodyFont = ResolveFont(BodyFontNames);
        headerFont = ResolveFont(HeaderFontNames) ?? bodyFont;
        resolvedBodyFont = bodyFont != null;
        ResolveBackdrop();

        if (!loggedFont)
        {
            loggedFont = true;
            Plugin.BepinLogger.LogInfo(
                $"[Skin] body font = {(bodyFont == null ? "none (Unity default)" : bodyFont.name)}, " +
                $"header font = {(headerFont == null ? "none (Unity default)" : headerFont.name)}");
        }
    }

    /// <summary>
    /// The first of <paramref name="names"/> that is loaded and usable, or a Windows copy of
    /// Century Gothic, or null to mean "leave Unity's default alone". FindObjectsOfTypeAll
    /// rather than Resources.Load, because these are assets baked into the game's scenes with
    /// no path to load them by.
    /// </summary>
    private static Font ResolveFont(string[] names)
    {
        try
        {
            Font[] loaded = Resources.FindObjectsOfTypeAll<Font>();

            foreach (string name in names)
            {
                foreach (Font candidate in loaded)
                {
                    if (candidate == null) continue;
                    if (!candidate.name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Usable(candidate)) continue;

                    return candidate;
                }
            }

            // Second pass, through TextMeshPro: a TMP_FontAsset keeps a reference to the Font
            // it was baked from, which reaches the same objects by another road.
            foreach (string name in names)
            {
                foreach (TMP_FontAsset candidate in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                {
                    if (candidate == null) continue;
                    if (candidate.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!Usable(candidate.sourceFontFile)) continue;

                    return candidate.sourceFontFile;
                }
            }
        }
        catch (Exception error)
        {
            // An unstyled panel is a far smaller problem than an OnGUI that throws every frame.
            Plugin.BepinLogger.LogWarning($"[Skin] font lookup failed; using Unity's default.{Environment.NewLine}{error}");
            return null;
        }

        // OS fallback, and only for the body face: centurygothic is a Microsoft font, so
        // GOTHIC.TTF is on Windows already.
        Font osFont = Font.CreateDynamicFontFromOSFont("Century Gothic", 16);
        return Usable(osFont) ? osFont : null;
    }

    /// <summary>
    /// Whether a font can be drawn at an arbitrary size. Every font size in the overlay is a
    /// fraction of the screen height, and a non-dynamic font ignores
    /// <see cref="GUIStyle.fontSize"/> entirely.
    /// </summary>
    private static bool Usable(Font font) => font != null && font.dynamic;

    /// <summary>
    /// Looks for a game sprite to use as the panel backdrop, and leaves both fields null when
    /// there is none, which is what makes <see cref="BuildBackdrop"/> draw one instead.
    /// </summary>
    private static void ResolveBackdrop()
    {
        backdrop = null;
        backdropBorder = null;

        try
        {
            // Sprites first, because a Sprite carries the 9-slice border the artist authored
            // and a bare Texture2D does not.
            foreach (string name in BackdropNames)
            {
                foreach (Sprite candidate in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (candidate == null || candidate.texture == null) continue;
                    if (!Matches(candidate.name, name) && !Matches(candidate.texture.name, name)) continue;

                    // An atlased sprite is a window onto a shared page, and IMGUI can only draw
                    // a whole texture, so taking one would paint every other sprite on that
                    // page into the panel.
                    if (candidate.packed) continue;
                    if (!IsWholeTexture(candidate)) continue;

                    // GUIStyle.border wants left/right/top/bottom; Sprite.border packs the same
                    // four as x/y/z/w = left/bottom/right/top.
                    Vector4 border = candidate.border;

                    // A zero border means the artist never sliced it, so the whole texture
                    // would stretch to fill the rect - fine for a flat colour and a smear for
                    // anything with a shape to it.
                    if (border == Vector4.zero) continue;

                    backdrop = candidate.texture;
                    backdropBorder = new RectOffset(
                        (int)border.x, (int)border.z, (int)border.w, (int)border.y);
                    return;
                }
            }

        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogWarning(
                $"[Skin] backdrop lookup failed; using Unity's default box.{Environment.NewLine}{error}");
            backdrop = null;
            backdropBorder = null;
        }
        finally
        {
            if (!loggedBackdrop)
            {
                loggedBackdrop = true;
                Plugin.BepinLogger.LogInfo(
                    $"[Skin] panel backdrop = {(backdrop == null ? "drawn (no game panel sprite to borrow)" : backdrop.name)}");
            }
        }
    }

    private static bool Matches(string actual, string wanted) =>
        !string.IsNullOrEmpty(actual) && actual.Equals(wanted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the sprite covers its whole texture, which is what makes the texture safe to
    /// hand to IMGUI as a background.
    /// </summary>
    private static bool IsWholeTexture(Sprite sprite) =>
        Mathf.Approximately(sprite.rect.width, sprite.texture.width)
        && Mathf.Approximately(sprite.rect.height, sprite.texture.height);

    /// <summary>
    /// Draws the panel texture: a near-black fill inside an outline in <see cref="TextColor"/>,
    /// nine texels plus the outline, sliced so only the middle stretches and the outline stays
    /// exactly <paramref name="thickness"/> pixels thick whatever size the panel ends up.
    /// </summary>
    private static void BuildBackdrop(out Texture2D texture, out int thickness)
    {
        // Scaled like everything else, so the outline is as heavy relative to the text at 4K as
        // it is at 1080p.
        thickness = Mathf.Max(1,
            Mathf.RoundToInt(OutlineThickness * Screen.height / ReferenceHeight));

        // Two outlines plus at least one texel of fill in the middle for the slice to stretch.
        int size = thickness * 2 + 2;

        // BuildStyles runs again on every resolution change, so without this each change would
        // leave a texture behind.
        if (drawnBackdrop != null) UnityEngine.Object.Destroy(drawnBackdrop);

        drawnBackdrop = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "Straftapelago_Panel",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };

        Color outline = new Color(TextColor.r, TextColor.g, TextColor.b, 0.85f);
        Color[] pixels = new Color[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool onEdge = x < thickness || y < thickness
                    || x >= size - thickness || y >= size - thickness;

                pixels[y * size + x] = onEdge ? outline : PanelFill;
            }
        }

        drawnBackdrop.SetPixels(pixels);
        drawnBackdrop.Apply();

        texture = drawnBackdrop;
    }

    /// <summary>
    /// The authored border, scaled to this resolution and never allowed to reach zero: a zero
    /// border turns the 9-slice into a plain stretch, which smears the corners across the whole
    /// panel.
    /// </summary>
    private static RectOffset ScaleBorder(RectOffset border)
    {
        if (border == null) return new RectOffset(0, 0, 0, 0);

        float scale = Screen.height / ReferenceHeight;

        return new RectOffset(
            ScaleEdge(border.left, scale),
            ScaleEdge(border.right, scale),
            ScaleEdge(border.top, scale),
            ScaleEdge(border.bottom, scale));
    }

    private static int ScaleEdge(int edge, float scale) =>
        edge <= 0 ? 0 : Mathf.Max(1, Mathf.RoundToInt(edge * scale));
}
