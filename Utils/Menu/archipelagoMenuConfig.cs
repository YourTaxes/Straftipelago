using BepInEx.Configuration;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// When the leaning modifier patches are allowed to run. An enum rather than a string with an
/// AcceptableValueList, because that is what Mod Menu draws with its EnumDropdownOption prefab
/// and its full-width field. The cost is that the member names are the labels Mod Menu shows,
/// so they cannot carry spaces.
/// </summary>
internal enum LeaningModifierRemoval
{
    Always,
    OnlyWhileInMetronomeMode,
    Never,
}

// The .cfg itself. Entries are bound in the order they should appear on the Mod Menu page: Mod
// Menu walks a plugin's entries in bind order and starts a new heading whenever the section
// changes. The .cfg groups by section too, but sorts those groups alphabetically.
internal static partial class ArchipelagoMenu
{
    private static void CreateConfigs(ConfigFile config)
    {
        RolledTwoHandedWeaponsOverride = config.Bind("Roulette", "Rolled 2 handed weapons override",
            false,
            "When a roulette roll produces a two-handed weapon -\n\nOff: It is only taken if the " +
            "roulette was in the right hand and the left hand is empty, otherwise it is left " +
            "on the ground.\n\nOn: Anything held is dropped and the weapon is taken in both " +
            "hands, the same way picking a two-handed weapon up off the floor works.");

        // AcceptableValueRange is what makes Mod Menu draw this as a bounded slider rather than
        // a free-text int, and it is also what stops a hand-edited .cfg putting a value outside
        // 1-100 into the roll.
        NewWeaponChance = config.Bind("Roulette", "New Weapon Chance",
            50,
            new ConfigDescription(
                "The percent chance that a roulette roll gives you a weapon you have NOT got a " +
                "kill with yet. The rest of the time it gives you one you already have a kill " +
                "with, so at 40, four rolls in ten are new weapons and six are old ones.\n\n" +
                "If either group is empty the roll comes from the other one regardless.",
                new AcceptableValueRange<int>(1, 100)));

        // A plain enum entry with no AcceptableValues, which is what routes it to Mod Menu's
        // EnumDropdownOption prefab and its full-width field. BepInEx parses the enum back out
        // of the .cfg itself, and falls back to the default for anything it does not recognise.
        RemoveLeaningModifiers = config.Bind("Movement", "Remove Leaning Modifiers",
            LeaningModifierRemoval.OnlyWhileInMetronomeMode,
            "Leaning normally costs you speed, cannot be done in the air or while sliding, and " +
            "drops your sprint. This decides when those penalties are taken off.\n\n" +
            "Always: every match, all the time.\n\n" +
            "OnlyWhileInMetronomeMode: only while a Metronome the multiworld sent you is " +
            "counting down.\n\n" +
            "Never: the game is left alone. A Metronome will still swing you left and right because " +
            "that is the trap, not a modifier, but you will pay the full price for every lean " +
            "it puts you in.");

        GreenMode = config.Bind("Green Mode", "Green Mode", false,
            "Challenge me in Green Mode.");

        // Fires on the frame the checkbox is clicked, on the main thread. A bool entry only
        // raises this when the value actually changes, so one click is one message.
        GreenMode.SettingChanged += (_, _) =>
        {
            // The killfeed, not the chat: this is the mod talking, not the Archipelago room.
            Killfeed.Write("Challenge me in Green Mode");

            // Makes the change visible mid match.
            GreenModeTint.RefreshAll();
        };

        // Each channel is multiplied over what the camera renders, so 1 leaves a channel
        // untouched and 0 erases it. Pure green is legal but takes the readability of the game
        // with it, hence the default leaving a quarter of the red and blue in place.
        GreenModeTintRgb = config.Bind("Green Mode", "Tint RGB",
            new Vector3(0.25f, 1f, 0.25f),
            "The colour Green Mode multiplies over the camera, as RGB in the 0-1 range. " +
            "Not shown in the Mod Menu page; edit it here.");

        // Picked up without a restart when this file is edited and BepInEx reloads it.
        GreenModeTintRgb.SettingChanged += (_, _) => GreenModeTint.RefreshAll();

        // AcceptableValueRange on each of the Traps knobs is what stops a hand-edited .cfg
        // putting a zero or a negative into a countdown.
        MetronomeTrapSeconds = config.Bind("Traps", "Metronome Seconds",
            30,
            new ConfigDescription(
                "How many seconds the Metronome trap's countdown runs for. A Metronome that " +
                "arrives while one is already running extends it by this much again.\n\n" +
                "The countdown only runs while you are alive and playing, and it holds between " +
                "rounds and while you are dead, but it keeps going while you are stunned.\n\n" +
                "Not shown in the Mod Menu page; edit it here.",
                new AcceptableValueRange<int>(1, 600)));

        MetronomeTickSeconds = config.Bind("Traps", "Metronome Tick Seconds",
            0.5f,
            new ConfigDescription(
                "How long the Metronome holds you in each lean. It beats 'tick' (leaning left), " +
                "'and' (upright), 'tock' (leaning right), 'and' (upright), round and round for " +
                "as long as the countdown lasts, and you cannot lean by hand while it does.\n\n" +
                "Smaller is faster and much harder to fight.\n\n" +
                "Not shown in the Mod Menu page; edit it here.",
                new AcceptableValueRange<float>(0.05f, 10f)));

        // Only the activating player's copy of this is used: the number travels to the rest of
        // the lobby in the Mycelium message, so everyone counts the same countdown down.
        MadeInHeavenSeconds = config.Bind("Traps", "Made in Heaven Seconds",
            60,
            new ConfigDescription(
                "How many seconds Made in Heaven runs for once it is activated. A second one " +
                "does not add to the first and instead it replaces it, and the countdown restarts at this " +
                "many seconds.\n\n" +
                "The countdown holds between rounds and picks up again when the next one " +
                "starts.\n\n" +
                "Only your own copy of this setting matters, and only when the multiworld gives " +
                "the buff to YOU: the length is sent to everyone else in the lobby along with " +
                "the activation.\n\n" +
                "Not shown in the Mod Menu page; edit it here.",
                new AcceptableValueRange<int>(1, 600)));

        // The two ends of the ramp, sent to the lobby with the activation for the same reason
        // the length is: the beat takes hold of people's leaning, so it has to be the same beat
        // for everyone.
        MadeInHeavenStartTickSeconds = config.Bind("Traps", "Made in Heaven Start Tick Seconds",
            1f,
            new ConfigDescription(
                "How long each lean is held at the START of a Made in Heaven, before it begins " +
                "to accelerate. Bigger is slower.\n\n" +
                "The metronome swings everyone in the lobby EXCEPT whoever activated it, and " +
                "speeds up smoothly from this toward Made in Heaven End Tick Seconds as the " +
                "countdown runs out.\n\n" +
                "Only the activating player's copy is used; it is sent to the rest of the lobby " +
                "with the activation.\n\n" +
                "Not shown in the Mod Menu page; edit it here.",
                new AcceptableValueRange<float>(0.05f, 10f)));

        MadeInHeavenEndTickSeconds = config.Bind("Traps", "Made in Heaven End Tick Seconds",
            0.15f,
            new ConfigDescription(
                "How long each lean is held by the END of a Made in Heaven, at its fastest. " +
                "Smaller is faster.\n\n" +
                "Setting this LARGER than Made in Heaven Start Tick Seconds is allowed and simply " +
                "runs the ramp backwards, slowing down instead of speeding up.\n\n" +
                "Only the activating player's copy is used; it is sent to the rest of the lobby " +
                "with the activation.\n\n" +
                "Not shown in the Mod Menu page; edit it here.",
                new AcceptableValueRange<float>(0.05f, 10f)));

        DebugButtons = config.Bind("Debug", "Debug Buttons", false,
            "Enables the roulette debug keys while in a match:\n\nO resets the item pools, " +
            "P grants one random unowned weapon, I grants every unowned weapon, and K runs " +
            "the roll distribution self-test and writes the result to the log.");
    }
}
