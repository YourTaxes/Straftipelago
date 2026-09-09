using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using ModMenu.Api;
using ModMenu.Behaviours.OptionList.Dummies;
using ModMenu.Behaviours.OptionList.ValueControllers;
using Straftapelago.Finnegan_McD.org.Archipelago;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// The mod's Mod Menu page: its settings, and the Archipelago login block above them. The
/// login fields are not config entries - they write straight to
/// <see cref="ArchipelagoClient.ServerData"/> and are only used to log in.
/// </summary>
internal static partial class ArchipelagoMenu
{
    private const string Section = "Archipelago Login";

    // The resource path to the archipelago logo asset.
    private const string IconResource = "Straftapelago.Finnegan_McD.org.Assets.logo.png";

    // Whether a rolled two-handed weapon is placed in the player's hands or on the ground.
    public static ConfigEntry<bool> RolledTwoHandedWeaponsOverride { get; private set; }

    // When the leaning movement patches are allowed to run.
    public static ConfigEntry<LeaningModifierRemoval> RemoveLeaningModifiers { get; private set; }

    // How often a roulette roll produces a weapon the player has not got a kill with yet.
    public static ConfigEntry<int> NewWeaponChance { get; private set; }

    // Whether the player accepts the challenge.
    public static ConfigEntry<bool> GreenMode { get; private set; }

    // The greenness itself. Not visible through Mod Menu.
    public static ConfigEntry<Vector3> GreenModeTintRgb { get; private set; }

    // How many seconds the Metronome trap's countdown runs for. Not visible through Mod Menu.
    public static ConfigEntry<int> MetronomeTrapSeconds { get; private set; }

    // How long the Metronome holds each lean of its swing. Not visible through Mod Menu.
    public static ConfigEntry<float> MetronomeTickSeconds { get; private set; }

    // How many seconds a Made in Heaven runs for. Not visible through Mod Menu.
    public static ConfigEntry<int> MadeInHeavenSeconds { get; private set; }

    // The beat interval a Made in Heaven opens on, before it accelerates. Not visible through Mod Menu.
    public static ConfigEntry<float> MadeInHeavenStartTickSeconds { get; private set; }

    // The beat interval a Made in Heaven ends on, its fastest. Not visible through Mod Menu.
    public static ConfigEntry<float> MadeInHeavenEndTickSeconds { get; private set; }

    // Gates the I/O/P/K/L roulette debug keys in PlayerPickupUpdatePatch.
    public static ConfigEntry<bool> DebugButtons { get; private set; }

    /// <summary>
    /// Binds the config and registers the Mod Menu page. The three ModMenuCustomisation calls
    /// resolve the plugin by Assembly.GetCallingAssembly, so they have to be made from this
    /// assembly and each may only be made once for it.
    /// </summary>
    public static void Install(ConfigFile config)
    {
        // Bind is what creates BepInEx/config/org.Finnegan_McD.Straftapelago.cfg.
        CreateConfigs(config);

        ModMenuCustomisation.RegisterContentBuilder(Build);
        ModMenuCustomisation.SetPluginDescription(
            "Archipelago support for STRAFTAT. Connect to a room from the login fields above.");

        // Hidden so the tint stays modifiable, but not as easily as the others.
        ModMenuCustomisation.HideEntry(GreenModeTintRgb);

        // The Traps knobs are hidden for the same reason: they tune what the room inflicts, so
        // they are meant to be set once in the .cfg rather than reached for from the pause menu
        // while one is running.
        ModMenuCustomisation.HideEntry(MetronomeTrapSeconds);
        ModMenuCustomisation.HideEntry(MetronomeTickSeconds);
        ModMenuCustomisation.HideEntry(MadeInHeavenSeconds);
        ModMenuCustomisation.HideEntry(MadeInHeavenStartTickSeconds);
        ModMenuCustomisation.HideEntry(MadeInHeavenEndTickSeconds);

        Sprite icon = LoadIcon();
        if (icon != null) ModMenuCustomisation.SetPluginIcon(icon);
    }

    /// <summary>Loads the embedded logo as the sprite Mod Menu shows for this plugin.</summary>
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
            // One missing icon must not take the whole page down with it.
            Plugin.BepinLogger.LogError($"Failed to load the Mod Menu icon{Environment.NewLine}{e}");
            return null;
        }
    }

    /// <summary>
    /// Builds the login block. Positions 0-5 put it above Mod Menu's auto-generated section:
    /// the items are inserted into the same list the generated options are in, and each insert
    /// grows the block by one, so consecutive indices land in the order written. Mod Menu only
    /// runs a content builder for a plugin that has at least one config entry, so this block
    /// reaches the screen on the back of the entries <see cref="CreateConfigs"/> binds.
    /// </summary>
    private static void Build(OptionListContext optionListContext)
    {
        ArchipelagoData data = ArchipelagoClient.ServerData;

        optionListContext.InsertHeader(0, Section);

        StringValueController host = optionListContext.InsertStringInput(1, "Host",
            () => data.Uri,
            value => data.Uri = value);
        Describe(optionListContext, host, "Host",
            "The Archipelago server to connect to, as host:port — for example " +
            "archipelago.gg:38281, or localhost for a room hosted on this machine.");

        StringValueController playerName = optionListContext.InsertStringInput(2, "Player Name",
            () => data.SlotName,
            value => data.SlotName = value);
        Describe(optionListContext, playerName, "Player Name",
            "The slot name to log in as. This has to match the slot in the room's YAML.");

        // Null-coalesced here rather than defaulted on the data object, because null is what
        // the Archipelago client wants to mean "no password".
        StringValueController password = optionListContext.InsertStringInput(3, "Password",
            () => data.Password ?? "",
            value => data.Password = value);
        Describe(optionListContext, password, "Password",
            "The room password. Leave blank if the room has none. Kept in memory for this " +
            "session only — it is never written to the config file.");

        // Masked because the field is a password field. Guarded rather than assumed: the
        // controller's inputField is a serialized reference on Mod Menu's own prefab.
        if (password.inputField != null)
        {
            password.inputField.contentType = TMP_InputField.ContentType.Password;
            password.inputField.ForceLabelUpdate();
        }

        // Empty nameText makes the button fill the line, which is what the label already says.
        ButtonDummy connect = optionListContext.InsertButton(4, "", "Connect", Connect);
        connect.OnItemHovered += () => optionListContext.SetInfoPanelContents("Connect", Section,
            $"Connect to the room with the details above.\n\nStatus: {StatusLine()}");

        ButtonDummy disconnect = optionListContext.InsertButton(5, "", "Disconnect", Disconnect);
        disconnect.OnItemHovered += () => optionListContext.SetInfoPanelContents("Disconnect", Section,
            $"Close the connection to the room.\n\nStatus: {StatusLine()}");
    }

    private static void Describe(OptionListContext c, StringValueController item, string title, string body)
    {
        item.OnItemHovered += () => c.SetInfoPanelContents(title, Section, body);
    }

    /// <summary>
    /// Makes the Mod Menu page show the current value of every setting this mod owns. Mod Menu
    /// builds a plugin's option list once and keeps it, so a control never looks at its config
    /// entry again after the frame it was created on - which is why a value the room decides
    /// would otherwise take effect without the widget moving. The row is not rebuilt, only
    /// re-read, so no change events are raised and nothing is written back. Main thread only.
    /// </summary>
    public static void RefreshDisplayedValues()
    {
        // FindObjectsOfTypeAll, not FindObjectsOfType: the rows for a page that is not open are
        // inactive, which is exactly their state when a connect happens from another screen.
        BoxedValueController[] controllers = Resources.FindObjectsOfTypeAll<BoxedValueController>();

        foreach (BoxedValueController controller in controllers)
        {
            if (controller == null || !OwnsSetting(controller)) continue;

            try
            {
                controller.UpdateAppearance();
            }
            catch (Exception e)
            {
                // One row that will not redraw must not cost the others theirs, and this runs
                // from a queued action where a throw would be reported as the action failing.
                Plugin.BepinLogger.LogError(
                    $"Could not refresh a Mod Menu control{Environment.NewLine}{e}");
            }
        }
    }

    /// <summary>
    /// Whether this control was generated from one of this mod's config entries. Filtered
    /// rather than refreshing everything on screen, since FindObjectsOfTypeAll also returns
    /// every other mod's rows and the prefabs they are cloned from. It is null for a control
    /// built by hand through the content builder, which excludes the login fields.
    /// </summary>
    private static bool OwnsSetting(BoxedValueController controller)
    {
        try
        {
            // Every hop is a Traverse: ModMenu.Options.Option is internal to Mod Menu, so it
            // cannot be named here, let alone its BaseEntry read directly. The entry itself is
            // BepInEx's own public type, which is where this comes back to C#.
            Traverse option = Traverse.Create(controller).Field("sourceOption");
            if (option.GetValue() == null) return false;

            ConfigEntryBase entry = option.Property("BaseEntry").GetValue<ConfigEntryBase>();
            if (entry == null) return false;

            return entry == RolledTwoHandedWeaponsOverride
                || entry == RemoveLeaningModifiers
                || entry == NewWeaponChance
                || entry == GreenMode
                || entry == GreenModeTintRgb
                || entry == MetronomeTrapSeconds
                || entry == MetronomeTickSeconds
                || entry == DebugButtons;
        }
        catch (Exception e)
        {
            // The field is internal, so it is not part of Mod Menu's API and a future version
            // may rename it. Claim nothing rather than refreshing somebody else's control.
            Plugin.BepinLogger.LogWarning(
                $"Could not read a Mod Menu control's source option; skipping it.{Environment.NewLine}{e}");
            return false;
        }
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

        // The server rejects an empty slot name with a login failure, so it is caught here
        // where it can be explained.
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
