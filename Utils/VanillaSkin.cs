using System;
using TMPro;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// The game's own font and panel backdrop, resolved at runtime and handed back as IMGUI
/// styles, so this mod's overlay looks like it belongs to STRAFTAT rather than to Unity.
/// </summary>
/// <remarks>
/// <para>Nothing here knows anything about the overlay's layout - it only answers "what does
/// vanilla look like". <see cref="ArchipelagoOverlay"/> keeps every Rect and fraction it
/// already had and simply draws with these styles.</para>
/// <para>Both halves are borrowed from the game rather than shipped with the mod:</para>
/// <list type="bullet">
/// <item><description><b>Font.</b> STRAFTAT_Data/sharedassets0.assets carries real
/// <see cref="Font"/> assets with embedded TTF data - "centurygothic", "GOTHICBI" (its bold
/// italic), "Hussar" and "Liberation Sans" - alongside the TextMeshPro versions the game
/// actually draws its UI with. IMGUI cannot use a TMP_FontAsset, but it takes a plain Font
/// directly, which is why this overlay can be in the game's face without being rewritten
/// into TextMeshPro.</description></item>
/// <item><description><b>Backdrop.</b> Drawn, not borrowed - see
/// <see cref="BuildBackdrop"/>. The game has no rectangular panel sprite to reuse: its menu
/// frames are 3D geometry (SM_PauseMenu_Frame, SM_Pause_Menu_Button_00, SM_HUD_00) and its
/// flat UI textures are full-screen art (T_TabMenuBG_00 and T_TabMenu_00 are both 1920x1080,
/// T_PauseMenu_00 is 1024x1024). Stretching one of those into a panel-sized rect gives a
/// tapered blob, which is exactly what the first attempt at this produced. What the panels
/// imitate instead is vanilla's look - a light outline on a near-black fill - with the
/// outline colour taken from the game's own UI text.</description></item>
/// </list>
/// <para>Every lookup falls back rather than throwing, and each one logs once what it
/// settled on. A game update that renames an asset therefore costs the mod its styling and
/// says so in the log - it does not cost it the overlay.</para>
/// </remarks>
internal static class VanillaSkin
{
    /// <summary>
    /// Font assets to try, best first. "centurygothic" is the game's UI face; GOTHICBI is
    /// the bold italic of the same family, which vanilla uses for headings and the health
    /// readout ("GOTHICBI UI HEALTH" on the TMP side); Hussar is the remaining game font and
    /// is still closer to vanilla than Unity's Arial.
    /// </summary>
    private static readonly string[] BodyFontNames = { "centurygothic", "Hussar" };

    /// <summary>
    /// Header font assets, best first. Falls back to the body font when none resolves, so
    /// headers are never left unstyled just because the bold italic went missing.
    /// </summary>
    private static readonly string[] HeaderFontNames = { "GOTHICBI", "centurygothic" };

    /// <summary>
    /// Game textures to use as the panel backdrop instead of the drawn one, best first.
    /// </summary>
    /// <remarks>
    /// Empty on purpose, and kept as the seam for filling in later. The obvious candidates
    /// are not panels: T_TabMenuBG_00 and T_TabMenu_00 are 1920x1080 and T_PauseMenu_00 is
    /// 1024x1024 - full-screen menu art, so squeezing one into a panel rect gives a tapered
    /// blob rather than a box. A name added here is only used if the asset turns out to be a
    /// Sprite with a real 9-slice border, which is what makes a texture safe to resize; see
    /// <see cref="ResolveBackdrop"/>.
    /// </remarks>
    private static readonly string[] BackdropNames = { };

    /// <summary>
    /// The height sizes here are authored against. GUIStyle.border is in texture pixels and
    /// IMGUI does not scale it with the rect being drawn, so without this a border that reads
    /// correctly at 1080p is half as thick at 4K. Vanilla's own UI gets this for free from
    /// its CanvasScaler.
    /// </summary>
    private const float ReferenceHeight = 1080f;

    /// <summary>How thick the drawn panel's outline is at <see cref="ReferenceHeight"/>.</summary>
    private const int OutlineThickness = 2;

    /// <summary>
    /// The drawn panel's fill. Near-black rather than black, and not quite opaque, so the
    /// map still reads faintly through it the way vanilla's own dark panels do.
    /// </summary>
    private static readonly Color PanelFill = new Color(0.03f, 0.03f, 0.03f, 0.88f);

    private static Font bodyFont;
    private static Font headerFont;
    private static Texture2D backdrop;
    private static RectOffset backdropBorder;

    /// <summary>
    /// The panel texture this class drew, as opposed to one borrowed from the game. Held so
    /// it can be destroyed when the styles are rebuilt - otherwise every resolution change
    /// would leak one.
    /// </summary>
    private static Texture2D drawnBackdrop;

    private static Color textColor = Color.white;

    private static GUIStyle panelStyle;
    private static GUIStyle headerStyle;
    private static GUIStyle entryStyle;

    /// <summary>Rebuilds the styles when the window is resized, since both font sizes and the
    /// slice border are derived from it.</summary>
    private static int builtForHeight;

    private static bool resolved;
    private static bool loggedFont;
    private static bool loggedBackdrop;

    /// <summary>The box every panel is drawn on. Falls back to <c>GUI.skin.box</c>'s look.</summary>
    public static GUIStyle Panel
    {
        get
        {
            EnsureBuilt();
            return panelStyle;
        }
    }

    /// <summary>The style for a panel's title line.</summary>
    public static GUIStyle Header
    {
        get
        {
            EnsureBuilt();
            return headerStyle;
        }
    }

    /// <summary>The style for one line inside a panel.</summary>
    public static GUIStyle Entry
    {
        get
        {
            EnsureBuilt();
            return entryStyle;
        }
    }

    /// <summary>
    /// One label, with a drop shadow behind it.
    /// </summary>
    /// <remarks>
    /// IMGUI has no equivalent of the SDF outline vanilla's TextMeshPro labels carry, and
    /// every panel here is drawn over something - the countdown over live gameplay, the
    /// other two over the pause menu's 3D geometry. A one-pixel offset in near-black is
    /// what keeps a white line readable against a bright map.
    /// </remarks>
    public static void Label(Rect rect, string text, GUIStyle style)
    {
        float offset = ShadowOffset();

        Color previous = GUI.color;

        // Not pure black, and not opaque: a hard black outline reads as a second font weight
        // rather than as a shadow.
        GUI.color = new Color(0f, 0f, 0f, 0.75f * previous.a);
        GUI.Label(new Rect(rect.x + offset, rect.y + offset, rect.width, rect.height), text, style);

        GUI.color = previous;
        GUI.Label(rect, text, style);
    }

    /// <summary>
    /// How wide <paramref name="text"/> needs to be drawn at, including the room
    /// <see cref="Label"/>'s shadow takes on the right.
    /// </summary>
    /// <remarks>
    /// This is what lets the panels size themselves to what they actually contain. They used
    /// to take fixed fractions of the screen width, which was only ever right for one font at
    /// one resolution - and once the font became the game's own, those fractions left the
    /// progress box far wider than its text and the weapon list narrower than its.
    /// </remarks>
    public static float MeasureWidth(string text, GUIStyle style)
    {
        if (string.IsNullOrEmpty(text)) return 0f;

        return style.CalcSize(new GUIContent(text)).x + ShadowOffset();
    }

    /// <summary>The widest of <paramref name="texts"/>, or zero if there are none.</summary>
    public static float MeasureWidest(System.Collections.Generic.IEnumerable<string> texts, GUIStyle style)
    {
        float widest = 0f;
        foreach (string text in texts) widest = Mathf.Max(widest, MeasureWidth(text, style));
        return widest;
    }

    /// <summary>
    /// One panel box, at <paramref name="alpha"/> opacity.
    /// </summary>
    /// <remarks>
    /// The alpha exists for the countdown, which is the one panel that draws over live
    /// gameplay: a fully opaque box sitting in the corner for a thirty second trap is a
    /// bigger imposition than the countdown is worth. Tinting through <see cref="GUI.color"/>
    /// rather than keeping a second GUIStyle, so both variants stay one texture and one
    /// border.
    /// </remarks>
    public static void Box(Rect rect, float alpha = 1f)
    {
        Color previous = GUI.color;
        GUI.color = new Color(previous.r, previous.g, previous.b, previous.a * alpha);

        GUI.Box(rect, GUIContent.none, Panel);

        GUI.color = previous;
    }

    /// <summary>
    /// Resolves the game's assets once, then rebuilds the styles whenever the resolved font
    /// is destroyed (Unity fake-null after a scene teardown) or the window is resized.
    /// </summary>
    private static void EnsureBuilt()
    {
        if (!resolved || bodyFont == null || backdrop == null)
        {
            Resolve();
            builtForHeight = 0;
        }

        if (builtForHeight == Screen.height && entryStyle != null) return;

        builtForHeight = Screen.height;
        BuildStyles();
    }

    private static void Resolve()
    {
        resolved = true;

        bodyFont = ResolveFont(BodyFontNames);
        headerFont = ResolveFont(HeaderFontNames) ?? bodyFont;
        ResolveBackdrop();
        ResolveTextColor();

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
    /// Century Gothic, or null to mean "leave Unity's default alone".
    /// </summary>
    /// <remarks>
    /// <para>FindObjectsOfTypeAll rather than Resources.Load: these are assets baked into the
    /// game's scenes, not files under a Resources folder, so there is no path to load them
    /// by. They are loaded because vanilla's own TextMeshPro assets reference them.</para>
    /// <para>The OS fallback is worth having because centurygothic is a Microsoft font -
    /// GOTHIC.TTF is on Windows already - so a game update that renames the asset still
    /// leaves the overlay in the right face on the platform the game is mostly played on.</para>
    /// </remarks>
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

            // Second pass, through TextMeshPro. A TMP_FontAsset keeps a reference to the
            // Font it was baked from, and that reference is what put the TTF in the build in
            // the first place - so this reaches the same objects by another road if the pass
            // above missed them (a differently named asset, say).
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
            // Never at the cost of the overlay: an unstyled panel is a far smaller problem
            // than an OnGUI that throws every frame.
            Plugin.BepinLogger.LogWarning($"[Skin] font lookup failed; using Unity's default.{Environment.NewLine}{error}");
            return null;
        }

        // OS fallback, and only for the body face - the header falls back to the body font,
        // which is a closer match than a second guess at what Windows has installed.
        Font osFont = Font.CreateDynamicFontFromOSFont("Century Gothic", 16);
        return Usable(osFont) ? osFont : null;
    }

    /// <summary>
    /// Whether a font can be drawn at an arbitrary size.
    /// </summary>
    /// <remarks>
    /// Every font size in the overlay is a fraction of the screen height, and a non-dynamic
    /// font ignores <see cref="GUIStyle.fontSize"/> entirely - it only has the sizes it was
    /// baked at. Taking one would make the panels wrong at every resolution but one, which
    /// is worse than not styling them at all.
    /// </remarks>
    private static bool Usable(Font font) => font != null && font.dynamic;

    private static void ResolveBackdrop()
    {
        backdrop = null;
        backdropBorder = null;

        try
        {
            // Sprites first, because a Sprite carries the 9-slice border the artist authored
            // and a bare Texture2D does not. GUIStyle.border wants left/right/top/bottom;
            // Sprite.border packs the same four as x/y/z/w = left/bottom/right/top.
            foreach (string name in BackdropNames)
            {
                foreach (Sprite candidate in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (candidate == null || candidate.texture == null) continue;
                    if (!Matches(candidate.name, name) && !Matches(candidate.texture.name, name)) continue;

                    // An atlased sprite is a window onto a shared page, and IMGUI can only
                    // draw a whole texture - taking one would paint every other sprite on
                    // that page into the panel.
                    if (candidate.packed) continue;
                    if (!IsWholeTexture(candidate)) continue;

                    // GUIStyle.border wants left/right/top/bottom; Sprite.border packs the
                    // same four as x/y/z/w = left/bottom/right/top.
                    Vector4 border = candidate.border;

                    // A zero border means the artist never sliced it, so the whole texture
                    // stretches to fill the rect. That is fine for a flat colour and wrong
                    // for anything with a shape to it - which is every UI texture this game
                    // has. Refused rather than drawn as a smear.
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

    /// <summary>How far <see cref="Label"/> offsets its shadow, in pixels at this resolution.</summary>
    private static float ShadowOffset() => Mathf.Max(1f, Mathf.Round(Screen.height / ReferenceHeight));

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
    /// Takes the label colour off whatever TextMeshPro label the game currently has on
    /// screen, so the overlay tracks a palette change rather than hardcoding one.
    /// </summary>
    /// <remarks>
    /// Deliberately undemanding about which label it finds: vanilla's UI is essentially one
    /// colour, so any of them is the right answer, and a wrong-but-vanilla colour is still
    /// closer than a guess. Very dark and fully transparent results are rejected - those are
    /// prefab defaults and tween-hidden labels, not the colour anything is drawn in.
    /// </remarks>
    private static void ResolveTextColor()
    {
        textColor = Color.white;

        try
        {
            foreach (TMP_Text candidate in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (candidate == null || !candidate.gameObject.activeInHierarchy) continue;

                Color color = candidate.color;
                if (color.a < 0.5f) continue;
                if (color.r + color.g + color.b < 1.5f) continue;

                textColor = new Color(color.r, color.g, color.b, 1f);
                return;
            }
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogWarning(
                $"[Skin] text colour lookup failed; using white.{Environment.NewLine}{error}");
        }
    }

    private static void BuildStyles()
    {
        // Fractions of the screen height rather than pixels, the same way every other size in
        // the overlay is, so the panels hold their proportions at any resolution.
        int entrySize = Mathf.Max(1, (int)(Screen.height * 0.016f));
        int headerSize = Mathf.Max(1, (int)(Screen.height * 0.019f));

        entryStyle = BuildLabelStyle(bodyFont, entrySize);
        headerStyle = BuildLabelStyle(headerFont, headerSize);

        panelStyle = new GUIStyle(GUI.skin.box);

        if (backdrop != null)
        {
            panelStyle.normal.background = backdrop;
            panelStyle.border = ScaleBorder(backdropBorder);
        }
        else
        {
            BuildBackdrop(out Texture2D texture, out int thickness);
            panelStyle.normal.background = texture;
            panelStyle.border = new RectOffset(thickness, thickness, thickness, thickness);
        }

        // The panels place every label with an explicit Rect and add their own padding, so
        // the style must not add any of its own on top.
        panelStyle.padding = new RectOffset(0, 0, 0, 0);
        panelStyle.margin = new RectOffset(0, 0, 0, 0);
        panelStyle.overflow = new RectOffset(0, 0, 0, 0);
    }

    /// <summary>
    /// Draws the panel texture: a near-black fill inside an outline the colour of the game's
    /// UI text. Nine texels plus the outline, sliced so only the middle stretches - which is
    /// what keeps the outline exactly <paramref name="thickness"/> pixels thick whatever size
    /// the panel ends up.
    /// </summary>
    /// <remarks>
    /// This is here because the game has nothing to borrow: every flat UI texture it ships is
    /// full-screen art, and its menu frames are 3D meshes. So the panels imitate the vanilla
    /// look - light outline, dark fill, which is what the options screen reads as - rather
    /// than reusing a texture that was never a panel. The outline colour still comes from
    /// vanilla, via <see cref="ResolveTextColor"/>.
    /// </remarks>
    private static void BuildBackdrop(out Texture2D texture, out int thickness)
    {
        // Scaled like everything else, so the outline is as heavy relative to the text at 4K
        // as it is at 1080p.
        thickness = Mathf.Max(1,
            Mathf.RoundToInt(OutlineThickness * Screen.height / ReferenceHeight));

        // Two outlines plus at least one texel of fill in the middle for the slice to
        // stretch. Any larger would only be texels nobody sees.
        int size = thickness * 2 + 2;

        // The previous one is dead the moment this replaces it, and BuildStyles runs again on
        // every resolution change - so without this each change would leave a texture behind.
        if (drawnBackdrop != null) UnityEngine.Object.Destroy(drawnBackdrop);

        drawnBackdrop = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "Straftapelago_Panel",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };

        Color outline = new Color(textColor.r, textColor.g, textColor.b, 0.85f);
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

    private static GUIStyle BuildLabelStyle(Font font, int fontSize)
    {
        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            alignment = TextAnchor.MiddleLeft,
            wordWrap = false,
            padding = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 0, 0),
            normal = { textColor = textColor },
        };

        // Left alone when nothing resolved, so the style keeps GUI.skin's own font rather
        // than being handed a null and drawing nothing.
        if (font != null) style.font = font;

        return style;
    }

    /// <summary>
    /// The authored border, scaled to this resolution and never allowed to reach zero - a
    /// zero border turns the 9-slice into a plain stretch, which smears the corners across
    /// the whole panel.
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
