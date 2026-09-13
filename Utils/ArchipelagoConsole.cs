using System.Reflection;
using BepInEx;
using ChatCommands;
using HarmonyLib;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// The Archipelago console. Everything the room says, and everything this mod says about the
/// connection, is written into the game's chat.
/// Printed through <c>ChatCommands.ChatPatches.SendSystemMessage</c>, the same call its
/// Evaluator uses for command output, so a line from the room looks like the output of
/// <c>/help</c> and lands in the same chat log. The other half of the console, the commands the
/// player types, is <see cref="Archipelago.ArchipelagoChatCommands"/>.
/// </summary>
public static class ArchipelagoConsole
{
    private static readonly MainThreadQueue Queue = new(TryWriteToChat, "Console");

    /// <summary>
    /// ChatCommands' private message prefab field, resolved once. Null when ChatCommands has
    /// renamed it, in which case <see cref="ChatReady"/> assumes ready and lets the write decide.
    /// </summary>
    private static readonly FieldInfo MessageTemplateField =
        AccessTools.Field(typeof(ChatPatches), "m_messageTemplate");

    public static void LogMessage(string message)
    {
        if (message.IsNullOrWhiteSpace()) return;

        // Unconditional, and first: this is the record that survives whether or not the chat
        // ever comes up, and it is safe to call from any thread.
        Plugin.BepinLogger.LogMessage(message);
        Queue.Enqueue(message);
    }

    /// <summary>
    /// Drains queued messages into the chat. Must be called from the main thread, once a
    /// frame - see <see cref="ArchipelagoOverlay.Update"/>.
    /// </summary>
    public static void Pump() => Queue.Pump();

    private static bool TryWriteToChat(string message)
    {
        if (!ChatReady()) return false;

        ChatPatches.SendSystemMessage(message);
        return true;
    }

    /// <summary>
    /// Whether ChatCommands has captured the chat panel it prints into. Its printer reads the
    /// message prefab from statics filled in by its own postfix on LobbyChatUILogic.Start, so
    /// before that scene is up printing would throw - and being on the menu is normal, so this
    /// is checked rather than caught.
    /// </summary>
    private static bool ChatReady()
    {
        // The field is private, so it is not part of ChatCommands' API and a future version may
        // rename it. Assume ready and let the write itself decide; the queue reports a throw
        // and moves on rather than jamming.
        if (MessageTemplateField == null) return true;

        return MessageTemplateField.GetValue(null) as GameObject != null;
    }
}
