using BepInEx;
using ChatCommands;
using HarmonyLib;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// The Archipelago console. Everything the room says, and everything this mod says about the
/// connection, is written into the game's chat.
/// </summary>
/// <remarks>
/// <para>Printed through <c>ChatCommands.ChatPatches.SendSystemMessage</c>, the same call its
/// Evaluator uses to show the output of a command - so a line from the Archipelago room looks
/// exactly like the output of <c>/help</c>, and lands in the same chat log. The other half of
/// the console, the commands the player types, is <see cref="ArchipelagoChatCommands"/>.</para>
/// <para>This class was once an IMGUI window drawn by <see cref="ArchipelagoOverlay"/>, and
/// then a router into the killfeed. Only <see cref="LogMessage"/> survives both moves, because
/// it is what the rest of the mod calls.</para>
/// </remarks>
public static class ArchipelagoConsole
{
    private static readonly MainThreadQueue Queue = new(TryWriteToChat, "Console");

    /// <summary>Kept for the call in <see cref="Plugin.Awake"/>; there is nothing to set up.</summary>
    public static void Awake()
    {
    }

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
    /// Whether ChatCommands has captured the chat panel it prints into.
    /// </summary>
    /// <remarks>
    /// Its printer takes the message prefab and the transform to parent it under from statics
    /// filled in by its own postfix on <c>LobbyChatUILogic.Start</c>, so before that scene is
    /// up they are null and printing would throw. Checked rather than caught, because "not in
    /// a match yet" is the normal state on the menu screen and would otherwise throw an
    /// exception per queued message per frame.
    /// </remarks>
    private static bool ChatReady()
    {
        try
        {
            return Traverse.Create(typeof(ChatPatches))
                .Field("m_messageTemplate").GetValue<GameObject>() != null;
        }
        catch
        {
            // The field is private, so it is not part of ChatCommands' API and a future version
            // may rename it. Assume ready and let the write itself decide; the queue reports a
            // throw and moves on rather than jamming.
            return true;
        }
    }
}
