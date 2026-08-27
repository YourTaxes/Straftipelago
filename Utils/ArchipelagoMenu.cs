using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using ModMenu.Api;
using ModMenu.Behaviours.OptionList.Dummies;
using ModMenu.Behaviours.OptionList.ValueControllers;
using Straftapelago.Finnegan_McD.org.Archipelago;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// The mod's page in Mod Menu, and the only login UI this mod has. It replaces the IMGUI
/// host/slot/password/Connect block that used to be drawn by <see cref="ArchipelagoOverlay"/>.
/// </summary>
/// <remarks>
/// <para>Mod Menu already generates a page from this plugin's config entries on its own —
/// <see cref="CreateConfigs"/> binds them. All this adds is the login block at the top,
/// because a header, three text fields and two buttons in a fixed order is not something an
/// auto-generated list can express.</para>
/// <para><b>The login fields are not config entries.</b> Each input is built from the Mod Menu
/// API's own getter/setter pair, reading and writing
/// <see cref="ArchipelagoClient.ServerData"/> — the object the Archipelago client already
/// takes its connection details from. Nothing about them is written to the .cfg, so the host,
/// slot name and password live only for the session.</para>
/// <para><b>The builder runs once per page, not once per open.</b> Mod Menu caches the
/// GameObjects it builds (OptionListPanel.m_optionCache) and on later opens merely
/// re-activates them, calling UpdateAppearance() on value controllers but not on plain text
/// or buttons. So the three inputs re-read ServerData every time the page is shown, while
/// anything drawn as static text would go stale. That is why the connection status is
/// reported through the info panel — SetInfoPanelContents runs on every hover — and through
/// the killfeed, rather than as a line in the list.</para>
/// </remarks>
internal static class ArchipelagoMenu
{
    private const string Section = "Archipelago Login";

    /// <summary>Set by the EmbeddedResource item in the csproj: folder path, dots for slashes.</summary>
    private const string IconResource = "Straftapelago.Finnegan_McD.org.Assets.logo.png";

    /// <summary>
    /// Read by <c>GrabPatches.EquipRolledWeapon</c>. Bound by <see cref="CreateConfigs"/>, so
    /// it is non-null from <see cref="Plugin.Awake"/> onwards - well before any patch runs.
    /// </summary>
    public static ConfigEntry<bool> RolledTwoHandedWeaponsOverride { get; private set; }

    public static ConfigEntry<bool> GreenMode { get; private set; }

    /// <summary>
    /// Binds the config and registers the page. Called once, from <see cref="Plugin.Awake"/>.
    /// </summary>
    /// <param name="config">The plugin's own <c>Config</c>.</param>
    public static void Install(ConfigFile config)
    {
        // First: the bind is what creates BepInEx/config/org.Finnegan_McD.Straftapelago.cfg,
        // and it is also what gives Mod Menu a page to hang the login block off (see Build).
        CreateConfigs(config);

        // All three calls resolve the plugin by Assembly.GetCallingAssembly(), so they have to
        // be made from this assembly, and each may only be made once for it.
        ModMenuCustomisation.RegisterContentBuilder(Build);
        ModMenuCustomisation.SetPluginDescription(
            "Archipelago support for STRAFTAT. Connect to a room from the login fields above.");

        Sprite icon = LoadIcon();
        if (icon != null) ModMenuCustomisation.SetPluginIcon(icon);
    }

    /// <summary>
    /// The Archipelago logo, for the mod's entry in Mod Menu's list.
    /// </summary>
    /// <remarks>
    /// <para>Mod Menu can also find an icon on its own, by searching the plugin's folder for
    /// an icon.png - but only files in the build output reach that folder, and Assets/logo.png
    /// is a source file, so it is embedded in the assembly instead and decoded here.
    /// SetPluginIcon takes precedence over that search either way.</para>
    /// <para>Both objects are marked HideAndDontSave: they belong to no scene and are
    /// referenced only by Mod Menu's list, so a scene change or an UnloadUnusedAssets sweep
    /// would otherwise be free to collect the icon out from under it.</para>
    /// </remarks>
    private static Sprite LoadIcon()
    {
        try
        {
            using (Stream stream = typeof(ArchipelagoMenu).Assembly.GetManifestResourceStream(IconResource))
            {
                if (stream == null)
                {
                    Plugin.BepinLogger.LogWarning(
                        $"Embedded resource '{IconResource}' not found; Mod Menu will show its default icon.");
                    return null;
                }

                byte[] data = new byte[stream.Length];
                stream.Read(data, 0, data.Length);

                // Size and format are replaced wholesale by LoadImage, which reads them from
                // the png itself, so the values here only have to be legal.
                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };

                if (!texture.LoadImage(data))
                {
                    Plugin.BepinLogger.LogWarning($"Could not decode '{IconResource}' as an image.");
                    Object.Destroy(texture);
                    return null;
                }

                Sprite sprite = Sprite.Create(texture,
                    new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                sprite.hideFlags = HideFlags.HideAndDontSave;
                return sprite;
            }
        }
        catch (Exception e)
        {
            // An icon is decoration; losing it must not cost the page it is attached to.
            Plugin.BepinLogger.LogError($"Failed to load the Mod Menu icon{Environment.NewLine}{e}");
            return null;
        }
    }

    /// <summary>
    /// Every config entry this mod has. BepInEx writes the .cfg itself from these binds - an
    /// install that has never run simply has no file yet, and one that has keeps whatever the
    /// user edited - and Mod Menu turns each entry into a list item on the page on its own.
    /// </summary>
    /// <remarks>
    /// The Archipelago login details are deliberately not bound here: they are session values
    /// on <see cref="ArchipelagoData"/>, driven by the string inputs in <see cref="Build"/>,
    /// so no host, slot name or room password is ever written to disk.
    /// </remarks>
    private static void CreateConfigs(ConfigFile config)
    {
        RolledTwoHandedWeaponsOverride = config.Bind("Roulette", "Rolled 2 handed weapons override",
            false,
            "When a roulette roll produces a two-handed weapon -\n\nOff: It is only taken if the " +
            "roulette was in the right hand and the left hand is empty, otherwise it is left " +
            "on the ground.\n\nOn: Anything held is dropped and the weapon is taken in both " +
            "hands, the same way picking a two-handed weapon up off the floor works.");
        
        GreenMode = config.Bind("Green Mode", "Green Mode", false,
            "Challenge me in Green Mode.");
    }

    /// <summary>
    /// Builds the login block. Positions 0-5 put it above Mod Menu's auto-generated section:
    /// the items are inserted into the same list the generated options are already in, and
    /// each insert grows the block by one, so consecutive indices land in the order written.
    /// </summary>
    /// <remarks>
    /// Mod Menu only runs a content builder for a plugin that has at least one config entry
    /// (Mod.HasAnyConfigs, checked before the builder is invoked), so this block reaches the
    /// screen on the back of the Roulette entry <see cref="CreateConfigs"/> binds. If that
    /// entry ever goes away, the login UI silently goes with it.
    /// </remarks>
    private static void Build(OptionListContext c)
    {
        ArchipelagoData data = ArchipelagoClient.ServerData;

        c.InsertHeader(0, Section);

        StringValueController host = c.InsertStringInput(1, "Host",
            () => data.Uri,
            value => data.Uri = value);
        Describe(c, host, "Host",
            "The Archipelago server to connect to, as host:port — for example " +
            "archipelago.gg:38281, or localhost for a room hosted on this machine.");

        StringValueController playerName = c.InsertStringInput(2, "Player Name",
            () => data.SlotName,
            value => data.SlotName = value);
        Describe(c, playerName, "Player Name",
            "The slot name to log in as. This has to match the slot in the room's YAML.");

        // ArchipelagoData leaves Password null until something sets it, and the input field
        // is handed this string directly - unlike Uri and SlotName, which its constructor
        // fills in. Null-coalesced here rather than defaulted on the data object, because null
        // is what the Archipelago client wants to mean "no password".
        StringValueController password = c.InsertStringInput(3, "Password",
            () => data.Password ?? "",
            value => data.Password = value);
        Describe(c, password, "Password",
            "The room password. Leave blank if the room has none. Kept in memory for this " +
            "session only — it is never written to the config file.");

        // Masked because the field is a password field. Guarded rather than assumed: the
        // controller's inputField is a serialized reference on Mod Menu's own prefab, and a
        // null here would take the whole page down with it.
        if (password.inputField != null)
        {
            password.inputField.contentType = TMP_InputField.ContentType.Password;
            password.inputField.ForceLabelUpdate();
        }

        // Empty nameText makes the button fill the line, which is what the label already says.
        // Note PrependButton is unusable — in this version of Mod Menu it calls itself.
        ButtonDummy connect = c.InsertButton(4, "", "Connect", Connect);
        connect.OnItemHovered += () => c.SetInfoPanelContents("Connect", Section,
            $"Connect to the room with the details above.\n\nStatus: {StatusLine()}");

        ButtonDummy disconnect = c.InsertButton(5, "", "Disconnect", Disconnect);
        disconnect.OnItemHovered += () => c.SetInfoPanelContents("Disconnect", Section,
            $"Close the connection to the room.\n\nStatus: {StatusLine()}");
    }

    private static void Describe(OptionListContext c, StringValueController item, string title, string body)
    {
        item.OnItemHovered += () => c.SetInfoPanelContents(title, Section, body);
    }

    /// <summary>
    /// Read on hover, so it is always current despite the page itself being built only once.
    /// </summary>
    private static string StatusLine()
    {
        return ArchipelagoClient.Authenticated
            ? $"connected to {ArchipelagoClient.ServerData.Uri} as {ArchipelagoClient.ServerData.SlotName}"
            : "not connected";
    }

    private static void Connect()
    {
        if (Plugin.ArchipelagoClient == null)
        {
            ArchipelagoConsole.LogMessage("Archipelago client failed to initialize; cannot connect.");
            return;
        }

        if (ArchipelagoClient.Authenticated)
        {
            ArchipelagoConsole.LogMessage("Already connected to Archipelago.");
            return;
        }

        // Same rule the old IMGUI Connect button enforced: the server rejects an empty slot
        // name with a login failure, so catch it here where it can be explained.
        if (ArchipelagoClient.ServerData.SlotName.IsNullOrWhiteSpace())
        {
            ArchipelagoConsole.LogMessage("Cannot connect: Player Name is empty.");
            return;
        }

        ArchipelagoConsole.LogMessage(
            $"Connecting to {ArchipelagoClient.ServerData.Uri} as {ArchipelagoClient.ServerData.SlotName}...");
        Plugin.ArchipelagoClient.Connect();
    }

    private static void Disconnect()
    {
        if (Plugin.ArchipelagoClient == null) return;

        if (!ArchipelagoClient.Authenticated)
        {
            ArchipelagoConsole.LogMessage("Not connected to Archipelago.");
            return;
        }

        Plugin.ArchipelagoClient.Disconnect();
        ArchipelagoConsole.LogMessage("Disconnected from Archipelago.");
    }
}
