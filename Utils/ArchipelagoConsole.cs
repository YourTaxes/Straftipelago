using System;
using System.Collections.Generic;
using BepInEx;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// Where every Archipelago-facing message goes. Each one is written to the BepInEx log and
/// to the game's killfeed.
/// </summary>
/// <remarks>
/// <para>This used to be an IMGUI window (a scrolling terminal drawn by
/// <see cref="ArchipelagoOverlay"/>, adapted from oc2-modding's GameLog) plus a text field
/// for sending server commands. The terminal now goes to the killfeed instead, so none of
/// that drawing survives; <see cref="LogMessage"/> keeps its signature, which is what the
/// rest of the mod calls.</para>
/// <para><b>Why the queue.</b> Messages arrive on threads that are not Unity's: the
/// Archipelago client's MessageLog callback fires on its websocket thread, and
/// <c>HandleConnectResult</c> runs on a ThreadPool thread. Writing a killfeed line
/// Instantiates a prefab, and every Unity API involved is main-thread-only, so the write
/// cannot happen where the message is produced. Messages are queued here and drained by
/// <see cref="Pump"/> from the overlay's Update instead.</para>
/// </remarks>
public static class ArchipelagoConsole
{
    /// <summary>
    /// Held rather than dropped when the killfeed is not up yet, so that messages produced
    /// in a menu are not lost. Capped so a long disconnected session cannot grow unbounded;
    /// the BepInEx log has the complete record either way.
    /// </summary>
    private const int MaxPending = 80;

    /// <summary>
    /// Killfeed lines fade on their own timer, so a backlog released all at once would push
    /// itself off the screen. Trickle it instead.
    /// </summary>
    private const int MaxPerFrame = 3;

    private static readonly Queue<string> Pending = new();

    /// <summary>Kept for the call in <see cref="Plugin.Awake"/>; there is nothing to set up.</summary>
    public static void Awake()
    {
    }

    public static void LogMessage(string message)
    {
        if (message.IsNullOrWhiteSpace()) return;

        // Unconditional, and first: this is the record that survives whether or not the
        // killfeed ever comes up, and it is safe from any thread.
        Plugin.BepinLogger.LogMessage(message);

        lock (Pending)
        {
            if (Pending.Count >= MaxPending) Pending.Dequeue();
            Pending.Enqueue(message);
        }
    }

    /// <summary>
    /// Drains queued messages to the killfeed. Must be called from the main thread, once a
    /// frame — see <see cref="ArchipelagoOverlay.Update"/>.
    /// </summary>
    public static void Pump()
    {
        // MatchLogsOffline.WriteLog dereferences a transform it finds by name in Awake, so it
        // is only usable once a scene that has one is up. Leave the messages queued until
        // then rather than throwing one per frame.
        if (PauseManager.Instance == null || MatchLogsOffline.Instance == null) return;

        for (int i = 0; i < MaxPerFrame; i++)
        {
            string message;
            lock (Pending)
            {
                if (Pending.Count == 0) return;
                message = Pending.Dequeue();
            }

            try
            {
                WriteToKillfeed(message);
            }
            catch (Exception e)
            {
                // Already in the BepInEx log by the time it got here, so the message itself
                // is not lost. Report the failure once per occurrence and keep draining.
                Plugin.BepinLogger.LogError($"[Killfeed] failed to write a line{Environment.NewLine}{e}");
            }
        }
    }

    private static void WriteToKillfeed(string text) => PauseManager.Instance.WriteOfflineLog(text);
}
