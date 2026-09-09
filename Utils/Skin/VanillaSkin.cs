using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// The game's own font and a matching panel backdrop, handed back as IMGUI styles so the mod's
/// overlay looks like it belongs to STRAFTAT rather than to Unity. Nothing here knows anything
/// about the overlay's layout - <see cref="OverlayPanels"/> keeps every Rect and draws with
/// these styles. Every lookup falls back rather than throwing, and logs once what it settled on.
/// </summary>
internal static partial class VanillaSkin
{
    /// <summary>
    /// The height sizes here are authored against. GUIStyle.border is in texture pixels and
    /// IMGUI does not scale it with the rect being drawn, so without this a border that reads
    /// correctly at 1080p is half as thick at 4K.
    /// </summary>
    private const float ReferenceHeight = 1080f;

    /// <summary>How thick the drawn panel's outline is at <see cref="ReferenceHeight"/>.</summary>
    private const int OutlineThickness = 2;

    /// <summary>
    /// The drawn panel's fill. Near-black rather than black, and not quite opaque, so the map
    /// still reads faintly through it the way vanilla's own dark panels do.
    /// </summary>
    private static readonly Color PanelFill = new Color(0.03f, 0.03f, 0.03f, 0.88f);

    private static Font bodyFont;
    private static Font headerFont;
    private static Texture2D backdrop;
    private static RectOffset backdropBorder;

    /// <summary>
    /// The panel texture this class drew, as opposed to one borrowed from the game. Held so it
    /// can be destroyed when the styles are rebuilt, otherwise every resolution change would
    /// leak one.
    /// </summary>
    private static Texture2D drawnBackdrop;

    /// <summary>The colour every panel's text and outline is drawn in.</summary>
    private static readonly Color TextColor = new Color(0.35f, 0.95f, 0.40f, 1f);

    private static GUIStyle panelStyle;
    private static GUIStyle headerStyle;
    private static GUIStyle entryStyle;

    /// <summary>
    /// The screen height the styles were built for, so a resize rebuilds them: both font sizes
    /// and the slice border are derived from it.
    /// </summary>
    private static int builtForHeight;

    private static bool resolved;

    /// <summary>
    /// Whether <see cref="Resolve"/> came back with a body font. Kept separate from
    /// <c>bodyFont != null</c> because null is a legitimate answer meaning "leave Unity's
    /// default font alone", so only a font that resolved and has since been destroyed is
    /// grounds for looking again.
    /// </summary>
    private static bool resolvedBodyFont;
    private static bool loggedFont;
    private static bool loggedBackdrop;

    /// <summary>The box every panel is drawn on.</summary>
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
    /// One label, with a drop shadow behind it. IMGUI has no equivalent of the SDF outline
    /// vanilla's TextMeshPro labels carry, and every panel here is drawn over something, so a
    /// one-pixel offset in near-black is what keeps a line readable against a bright map.
    /// </summary>
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
    /// <see cref="Label"/>'s shadow takes on the right. This is what lets the panels size
    /// themselves to what they actually contain.
    /// </summary>
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
    /// One panel box, at <paramref name="alpha"/> opacity. The alpha exists for the countdown,
    /// which is the one panel drawn over live gameplay; tinting through <see cref="GUI.color"/>
    /// keeps both variants on one texture and one border.
    /// </summary>
    public static void Box(Rect rect, float alpha = 1f)
    {
        Color previous = GUI.color;
        GUI.color = new Color(previous.r, previous.g, previous.b, previous.a * alpha);

        GUI.Box(rect, GUIContent.none, Panel);

        GUI.color = previous;
    }

    /// <summary>
    /// Resolves the game's assets once, then rebuilds the styles whenever the resolved font is
    /// destroyed or the window is resized. The conditions are deliberately narrow: a broader
    /// test re-resolves on every style access, which is three
    /// Resources.FindObjectsOfTypeAll sweeps plus a fresh panel texture per label drawn.
    /// </summary>
    private static void EnsureBuilt()
    {
        if (!resolved || (resolvedBodyFont && bodyFont == null))
        {
            Resolve();
            builtForHeight = 0;
        }

        // The drawn texture is HideAndDontSave so it survives scene loads, but if something
        // does destroy it the styles have to be rebuilt or the panels lose their backdrop.
        bool lostBackdrop = backdrop == null && drawnBackdrop == null;

        if (builtForHeight == Screen.height && entryStyle != null && !lostBackdrop) return;

        builtForHeight = Screen.height;
        BuildStyles();
    }

    private static void BuildStyles()
    {
        // Fractions of the screen height, the same way every other size in the overlay is.
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

        // The panels place every label with an explicit Rect and add their own padding, so the
        // style must not add any of its own on top.
        panelStyle.padding = new RectOffset(0, 0, 0, 0);
        panelStyle.margin = new RectOffset(0, 0, 0, 0);
        panelStyle.overflow = new RectOffset(0, 0, 0, 0);
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
            normal = { textColor = TextColor },
        };

        // Left alone when nothing resolved, so the style keeps GUI.skin's own font rather than
        // being handed a null and drawing nothing.
        if (font != null) style.font = font;

        return style;
    }

    /// <summary>How far <see cref="Label"/> offsets its shadow, in pixels at this resolution.</summary>
    private static float ShadowOffset() => Mathf.Max(1f, Mathf.Round(Screen.height / ReferenceHeight));
}
