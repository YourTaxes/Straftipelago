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



/*
This class creates the menu page for this mod's settings, as well as the archipelago login page.
it extensivly ueses the ModMenu Api to style the settings well in that tab.
It also includes the logic for connecting to the archipelago server and disconnecting from it.
The login fields themselves are not actually config entries, they are only used to login.
*/
internal static class ArchipelagoMenu
{
    private const string Section = "Archipelago Login";

    //the resource path to the archipelago logo asset
    private const string IconResource = "Straftapelago.Finnegan_McD.org.Assets.logo.png";


    // this config determines if 2 handed weapons are placed in the user's hand, or if they are placed on the ground
    public static ConfigEntry<bool> RolledTwoHandedWeaponsOverride { get; private set; }

    //determines if you accept the challenge
    public static ConfigEntry<bool> GreenMode { get; private set; }

    //determines the greenness. not visible through modmenu
    public static ConfigEntry<Vector3> GreenModeTintRgb { get; private set; }

    // creates the configs and the mod menu custom configs.
    public static void Install(ConfigFile config)
    {
        //bind is what creates BepInEx/config/org.Finnegan_McD.Straftapelago.cfg
        CreateConfigs(config);

        // All three calls resolve the plugin by Assembly.GetCallingAssembly(), so they have to
        // be made from this assembly, and each may only be made once for it.
        ModMenuCustomisation.RegisterContentBuilder(Build);
        ModMenuCustomisation.SetPluginDescription(
            "Archipelago support for STRAFTAT. Connect to a room from the login fields above.");

        // hide green tint, so that it is still modifiable, but not as easy as the others
        ModMenuCustomisation.HideEntry(GreenModeTintRgb);

        Sprite icon = LoadIcon();
        if (icon != null) ModMenuCustomisation.SetPluginIcon(icon);
    }

    // loads the sprite to be used in the modmenu
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
            // don't want the entire page to go under if this one icon isn't found.
            Plugin.BepinLogger.LogError($"Failed to load the Mod Menu icon{Environment.NewLine}{e}");
            return null;
        }
    }


    // creates the .cfg for every config entry. 
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

        // Fires on the frame the checkbox is clicked, on the main thread. A bool entry
        // only raises this when the value actually changes, so one click is one message.
        GreenMode.SettingChanged += (_, _) =>
        {
            // The killfeed, not the chat: this is the mod talking, not the Archipelago room
            Killfeed.Write("Challenge me in Green Mode");

            // makes this change visible mid match
            GreenModeTint.RefreshAll();
        };

        // Each channel is multiplied over what the camera renders, so 1 leaves a channel
        // untouched and 0 erases it. Pure green (0,1,0) is legal but drains every other
        // channel, which takes the readability of the game with it - hence the default
        // leaving a quarter of the red and blue in place.
        GreenModeTintRgb = config.Bind("Green Mode", "Tint RGB",
            new Vector3(0.25f, 1f, 0.25f),
            "The colour Green Mode multiplies over the camera, as RGB in the 0-1 range. " +
            "Not shown in the Mod Menu page; edit it here.");

        // Picked up without a restart when this file is edited and BepInEx reloads it.
        GreenModeTintRgb.SettingChanged += (_, _) => GreenModeTint.RefreshAll();
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

        // ArchipelagoData leaves Password null until something sets it, and the input field
        // is handed this string directly - unlike Uri and SlotName, which its constructor
        // fills in. Null-coalesced here rather than defaulted on the data object, because null
        // is what the Archipelago client wants to mean "no password".
        StringValueController password = optionListContext.InsertStringInput(3, "Password",
            () => data.Password ?? "",
            value => data.Password = value);
        Describe(optionListContext, password, "Password",
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
