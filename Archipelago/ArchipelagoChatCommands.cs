using System.Reflection;
using ChatCommands;
using ChatCommands.Attributes;
using Straftapelago.Finnegan_McD.org.Patches;
using Straftapelago.Finnegan_McD.org.Utils;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Archipelago;

/// <summary>
/// The Archipelago console's input half: every <c>!command</c> the Archipelago server
/// understands, exposed as a chat command that forwards it to the room.
/// Registration is attribute-driven, so each command is a real method rather than an entry in a
/// list, and they all funnel into <see cref="Send"/>. Every one returns void and prints nothing
/// itself: the reply comes back from the server through the client's MessageLog and into the
/// chat via <see cref="Utils.ArchipelagoConsole"/>. Every name is prefixed <c>ap_</c>, because
/// the registry refuses a whole command whose name or alias is already taken. ChatCommands'
/// lexer splits on whitespace, so multi-word values have to be quoted and each command takes at
/// most one string.
/// </summary>
[CommandCategory("Archipelago")]
public static class ArchipelagoChatCommands
{
    /// <summary>
    /// Hands this assembly to ChatCommands' registry. Called from <see cref="Plugin.Awake"/>,
    /// which is safe: ChatCommands is a hard dependency, so BepInEx has already run its Awake
    /// and its registry exists.
    /// </summary>
    public static void Install()
    {
        // The explicit overload, so the registration does not depend on where it is called from.
        CommandRegistry.RegisterCommandsFromAssembly(Assembly.GetExecutingAssembly());
    }

    // The server's own commands. The room is the authority on what it
    // accepts - /ap_help asks it, and its answer prints in the chat.

    [Command("ap_help", "Ask the Archipelago room for its list of commands.")]
    public static void Help() => Send("help");

    [Command("ap_license", "Show the Archipelago server's license text.")]
    public static void License() => Send("license");

    [Command("ap_players", "List the players connected to the room.")]
    public static void Players() => Send("players");

    [Command("ap_status", "Show the room's status.")]
    public static void Status() => Send("status");

    [Command("ap_options", "Show the room's options.")]
    public static void Options() => Send("options");

    [Command("ap_remaining", "List the items still to be found in your world.")]
    public static void Remaining() => Send("remaining");

    [Command("ap_missing", "List locations you have not checked yet. Optionally filtered.")]
    public static void Missing(string filter = "") => Send("missing", filter);

    [Command("ap_checked", "List locations you have already checked. Optionally filtered.")]
    public static void Checked(string filter = "") => Send("checked", filter);

    [Command("ap_collect", "Release your remaining items to yourself and finish your world.")]
    public static void Collect() => Send("collect");

    [Command("ap_release", "Release your world's remaining items to the other players.")]
    public static void Release() => Send("release");

    [Command("ap_countdown", "Start a countdown in the room, in seconds.")]
    public static void Countdown(string seconds = "") => Send("countdown", seconds);

    [Command("ap_alias", "Set your display name in the room.")]
    public static void Alias(string name = "") => Send("alias", name);

    [Command("ap_hint", "Ask where an item is. Quote multi-word names: /ap_hint 'pistol'")]
    public static void Hint(string item = "") => Send("hint", item);

    [Command("ap_hint_location", "Ask what is at a location. Quote multi-word names.")]
    public static void HintLocation(string location = "") => Send("hint_location", location);

    [Command("ap_getitem", "Ask the server to give you an item. Quote multi-word names.")]
    public static void GetItem(string item = "") => Send("getitem", item);

    [Command("ap_admin", "Run a server admin command. Quote the whole thing: /ap_admin '/status'")]
    public static void Admin(string command = "") => Send("admin", command);

    /// <summary>
    /// Toggles this slot's ready status with the room. Not a server !command - readiness is a
    /// StatusUpdate packet, which <see cref="ArchipelagoClient.ToggleReady"/> sends - and the
    /// one command here that prints its own answer, since the room acknowledges a status update
    /// with nothing at all.
    /// </summary>
    [Command("ap_ready", "Toggle your ready status with the room, like the text client's /ready.")]
    public static void Ready()
    {
        if (Plugin.ArchipelagoClient == null)
        {
            throw new CommandException("The Archipelago client failed to initialize.");
        }

        if (!ArchipelagoClient.Authenticated)
        {
            throw new CommandException("Not connected to an Archipelago room. Connect from the mod menu first.");
        }

        ArchipelagoConsole.LogMessage(Plugin.ArchipelagoClient.ToggleReady() ? "Readied up." : "Unreadied.");
    }

    /// <summary>
    /// Completes a weapon's check by hand: the same thing the first kill with that weapon does,
    /// without the kill. Not a server command - marking a location checked is a LocationChecks
    /// packet, which <see cref="LocationSender.Send_Location"/> sends. The pool move happens
    /// first and stands even when the send fails, so the pause screen's weapon list always shows
    /// what this machine believes.
    /// </summary>
    [Command("ap_completecheck", "Mark a weapon's first-kill check as done. Quote multi-word names: /ap_completecheck 'Baseball Bat'")]
    public static void CompleteCheck(string weapon = "")
    {
        string weaponName = weapon?.Trim();
        if (string.IsNullOrEmpty(weaponName))
        {
            throw new CommandException("Name a weapon: /ap_completecheck 'Baseball Bat'");
        }

        RouletteState roulette = Plugin.RouletteState;
        if (roulette == null)
        {
            throw new CommandException("The weapon pools do not exist yet.");
        }

        // The pools are populated the first time a player object comes up, not at startup, so
        // this command is reachable from a main menu where they are still empty.
        roulette.EnsureInitialized();

        GameObject prefab = roulette.MarkKillEarned(weaponName);
        if (prefab == null)
        {
            throw new CommandException(
                $"'{weaponName}' is not a weapon in the pool. Both the name on the weapon and " +
                "the prefab name work.");
        }

        // The moved weapon's own display name, not what was typed: the room names its
        // locations after the weapons, and this is the spelling the datapackage uses.
        string locationName = RouletteState.DisplayNameOf(prefab);
        ArchipelagoConsole.LogMessage($"{Prompt}complete check {locationName}");

        switch (LocationSender.Send_Location(locationName))
        {
            case LocationSendResult.Sent:
                break;

            case LocationSendResult.AlreadySent:
                ArchipelagoConsole.LogMessage($"{locationName} was already checked this session.");
                break;

            case LocationSendResult.NotConnected:
                throw new CommandException(
                    $"{locationName} counts as killed with locally, but there is no room to send " +
                    "its check to. Connect from the mod menu first.");

            case LocationSendResult.UnknownLocation:
                // The name in this message is the one the player typed, not the one that was
                // actually looked up - the prefab name the send uses is in the warning
                // LocationSender logs, and is of no use to somebody who has never seen it.
                throw new CommandException(
                    $"The room has no location for '{locationName}'. The apworld and this build " +
                    "of the game disagree about that weapon's name; see LogOutput.log for both.");

            default:
                throw new CommandException(
                    $"Could not send {locationName}'s check. See LogOutput.log.");
        }
    }

    /// <summary>
    /// Says anything at all to the room, for a command this list does not name and for plain
    /// chat. What is typed goes as typed - a leading <c>!</c> is not added - which is the way
    /// out when the server accepts something newer than this list.
    /// </summary>
    [Command("ap", "Say something to the Archipelago room verbatim. Quote it: /ap '!hint pistol' for hinting the pistol, /ap 'hello' for saying hello in chat.")]
    public static void Say(string message)
    {
        Dispatch(message);
    }

    /// <summary>
    /// Marks an echoed line as something the player sent, so it reads apart from the room's
    /// replies in the chat log they share.
    /// </summary>
    private const string Prompt = "> ";

    /// <summary>Builds "!command argument" and sends it.</summary>
    private static void Send(string command, string argument = "")
    {
        string trimmed = argument?.Trim();
        Dispatch(string.IsNullOrEmpty(trimmed) ? "!" + command : $"!{command} {trimmed}");
    }

    private static void Dispatch(string text)
    {
        // Failures are reported through the ChatCommands api.
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new CommandException("Nothing to send.");
        }

        // Echo the command into the chat before the reply arrives, for the look of a terminal.
        ArchipelagoConsole.LogMessage(Prompt + text);

        if (Plugin.ArchipelagoClient == null)
        {
            throw new CommandException("The Archipelago client failed to initialize.");
        }

        if (!ArchipelagoClient.Authenticated)
        {
            throw new CommandException("Not connected to an Archipelago room. Connect from the mod menu first.");
        }

        Plugin.ArchipelagoClient.SendMessage(text);
    }
}
