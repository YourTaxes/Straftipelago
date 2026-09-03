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
/// When the leaning modifier patches in metronomePatches are allowed to run.
/// </summary>
/// <remarks>
/// An enum rather than a string with an AcceptableValueList, because that is what Mod Menu draws
/// with its EnumDropdownOption prefab - the same control its own example plugin uses for
/// TestEnum - and that prefab gives the dropdown a field wide enough to read. The list version
/// takes the AcceptableListDropdownOption prefab instead, which is a much thinner field.
///
/// The cost is that the member names ARE the labels: Mod Menu fills the dropdown from
/// Enum.GetNames, so they cannot carry spaces. Named to read as close to a sentence as
/// identifiers allow, the way ModMenu's own example enum does.
/// </remarks>
internal enum LeaningModifierRemoval
{
    Always,
    OnlyWhileInMetronomeMode,
    Never,
}



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

    // when the leaning movement patches in metronomePatches are allowed to run
    public static ConfigEntry<LeaningModifierRemoval> RemoveLeaningModifiers { get; private set; }

    // how often a roulette roll produces a weapon the player has not got a kill with yet
    public static ConfigEntry<int> NewWeaponChance { get; private set; }

    //determines if you accept the challenge
    public static ConfigEntry<bool> GreenMode { get; private set; }

    //determines the greenness. not visible through modmenu
    public static ConfigEntry<Vector3> GreenModeTintRgb { get; private set; }

    // how many seconds the Metronome trap's countdown runs for. not visible through modmenu
    public static ConfigEntry<int> MetronomeTrapSeconds { get; private set; }

    // how long between the Metronome's tick/and/tock prints. not visible through modmenu
    public static ConfigEntry<float> MetronomeTickSeconds { get; private set; }

    // gates the I/O/P/K roulette debug keys in PlayerPickupUpdatePatch
    public static ConfigEntry<bool> DebugButtons { get; private set; }

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

        // The two Metronome knobs are hidden for the same reason: they tune a trap the room
        // inflicts, so they are meant to be set once in the .cfg rather than reached for from
        // the pause menu while one is running.
        ModMenuCustomisation.HideEntry(MetronomeTrapSeconds);
        ModMenuCustomisation.HideEntry(MetronomeTickSeconds);

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

        // AcceptableValueRange is what makes Mod Menu draw this as a bounded slider rather
        // than a free-text int, and it is also what stops a hand-edited .cfg putting a value
        // outside 1-100 into the roll.
        NewWeaponChance = config.Bind("Roulette", "New Weapon Chance",
            50,
            new ConfigDescription(
                "The percent chance that a roulette roll gives you a weapon you have NOT got a " +
                "kill with yet. The rest of the time it gives you one you already have a kill " +
                "with - so at 40, four rolls in ten are new weapons and six are old ones.\n\n" +
                "If either group is empty the roll comes from the other one regardless.",
                new AcceptableValueRange<int>(1, 100)));

        // Bound after the last Roulette entry, so the Movement heading it opens sits directly
        // below the Roulette block on the Mod Menu page: Mod Menu walks a plugin's entries in
        // the order they were bound and starts a new heading whenever the section changes. The
        // .cfg groups by section too, but sorts those groups alphabetically, so the file's own
        // order is its own business.
        //
        // A plain enum entry with no AcceptableValues, which is what routes it to Mod Menu's
        // EnumDropdownOption prefab and its full-width field. Attaching an AcceptableValueList
        // here would send it to the much thinner AcceptableListDropdownOption instead - that
        // branch is tested first in Mod Menu's Option.CreateForEntry. See LeaningModifierRemoval
        // for why the member names read the way they do. BepInEx parses the enum back out of the
        // .cfg itself, and falls back to the default for anything it does not recognise.
        RemoveLeaningModifiers = config.Bind("Movement", "Remove Leaning Modifiers",
            LeaningModifierRemoval.OnlyWhileInMetronomeMode,
            "Leaning normally costs you speed, cannot be done in the air or while sliding, and " +
            "drops your sprint. This decides when those penalties are taken off.\n\n" +
            "Always: every match, all the time.\n\n" +
            "OnlyWhileInMetronomeMode: only while a Metronome the multiworld sent you is " +
            "counting down.\n\n" +
            "Never: the game is left alone. A Metronome will still swing you left and right - " +
            "that is the trap, not a modifier - but you will pay the full price for every lean " +
            "it puts you in.");

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

        // The Metronome trap's two knobs. AcceptableValueRange on both is what stops a
        // hand-edited .cfg putting a zero or a negative into the countdown - see the second
        // guard MetronomeTrap keeps against the same thing.
        MetronomeTrapSeconds = config.Bind("Traps", "Metronome Seconds",
            30,
            new ConfigDescription(
                "How many seconds the Metronome trap's countdown runs for. A Metronome that " +
                "arrives while one is already running extends it by this much again.\n\n" +
                "The countdown only runs while you are alive and playing - it holds between " +
                "rounds and while you are dead, but it keeps going while you are stunned.\n\n" +
                "Not shown in the Mod Menu page; edit it here.",
                new AcceptableValueRange<int>(1, 600)));

        MetronomeTickSeconds = config.Bind("Traps", "Metronome Tick Seconds",
            0.5f,
            new ConfigDescription(
                "How long between the Metronome's prints, which beat 'tick', 'and', 'tock', " +
                "'and' round and round for as long as the countdown lasts.\n\n" +
                "Not shown in the Mod Menu page; edit it here.",
                new AcceptableValueRange<float>(0.05f, 10f)));

        // Bound after the Green Mode entries so the Debug section sits below them, in the
        // .cfg and on the Mod Menu page alike.
        DebugButtons = config.Bind("Debug", "Debug Buttons", false,
            "Enables the roulette debug keys while in a match:\n\nO resets the item pools, " +
            "P grants one random unowned weapon, I grants every unowned weapon, and K runs " +
            "the roll distribution self-test and writes the result to the log.");
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
    /// Makes the Mod Menu page show the current value of every setting this mod owns.
    /// </summary>
    /// <remarks>
    /// <para>Needed because Mod Menu builds a plugin's option list once and keeps it:
    /// <c>OptionListPanel</c> holds an <c>m_optionCache</c> keyed by mod and re-shows the same
    /// rows every time the page is opened, so a control never looks at its config entry again
    /// after the frame it was created on. Setting <c>GreenMode.Value</c> from slot data
    /// therefore tinted the game but left the checkbox unticked, and would have kept doing so
    /// for the whole session.</para>
    /// <para>The row is not rebuilt, only re-read. <c>UpdateAppearance()</c> is public on
    /// <see cref="BoxedValueController"/>, and for a control Mod Menu generated from a config
    /// entry its getter is <c>() =&gt; option.BoxedValue</c>, which reads that entry live - so
    /// this pushes the current value into the widget with SetIsOnWithoutNotify /
    /// SetValueWithoutNotify, raising no change events and writing nothing back.</para>
    /// <para>Must be called on the main thread.</para>
    /// </remarks>
    public static void RefreshDisplayedValues()
    {
        // FindObjectsOfTypeAll, not FindObjectsOfType: the rows for a page that is not currently
        // open are inactive, and inactive is exactly the state they are in when a connect
        // happens from the login block on some other screen.
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
    /// Whether this control was generated from one of this mod's config entries.
    /// </summary>
    /// <remarks>
    /// Filtered rather than refreshing everything on screen, because
    /// <see cref="Resources.FindObjectsOfTypeAll{T}"/> also returns every other mod's rows and
    /// the untouched prefabs the rows are cloned from. Reached through Traverse because
    /// <c>sourceOption</c> is internal to Mod Menu; it is null for a control built by hand
    /// through the content builder - our own login fields - which is another thing this
    /// excludes, since those read <see cref="ArchipelagoClient.ServerData"/> and not a config
    /// entry.
    /// </remarks>
    private static bool OwnsSetting(BoxedValueController controller)
    {
        try
        {
            // Every hop is a Traverse: ModMenu.Options.Option is internal to Mod Menu, so it
            // cannot even be named here, let alone its BaseEntry read directly. The entry
            // itself is BepInEx's own public type, which is where this comes back to C#.
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
