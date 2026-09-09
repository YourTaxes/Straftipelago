using System;
using System.Collections.Generic;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// Holds work produced off Unity's thread until the main thread can run it. Slot data is read
/// on a ThreadPool thread and items arrive on the Archipelago client's websocket thread, while
/// acting on either is main-thread-only: applying Green Mode walks the scene, rebuilding the
/// roulette pool reads SpawnerManager. Separate from <see cref="MainThreadQueue"/>, which holds
/// strings for a writer that can answer "not ready yet" and trickles them so chat lines do not
/// scroll off screen; a one-shot action runs once and has nothing to fade.
/// </summary>
internal static class MainThreadActions
{
    /// <summary>
    /// Capped so a session that somehow never pumps cannot grow without bound. Generous, because
    /// the producers here are connects and item receipts: a room dumping a full starting
    /// inventory at once is the realistic worst case.
    /// </summary>
    private const int MaxPending = 256;

    private static readonly Queue<Action> Pending = new();

    public static void Enqueue(Action action)
    {
        if (action == null) return;

        lock (Pending)
        {
            if (Pending.Count >= MaxPending)
            {
                // Reported rather than silently dropped: unlike a chat line, a dropped action
                // is a granted weapon or an applied setting that never happened.
                Plugin.BepinLogger.LogWarning(
                    $"[MainThreadActions] queue is full at {MaxPending}; dropping the oldest " +
                    "pending action. Something is producing work faster than the game can run it.");
                Pending.Dequeue();
            }

            Pending.Enqueue(action);
        }
    }

    /// <summary>
    /// Runs everything waiting. Must be called from the main thread. Drains fully rather than a
    /// few per frame: holding half a starting inventory back would let the player walk into a
    /// match with a pool the room has already finished filling.
    /// </summary>
    public static void Pump()
    {
        while (true)
        {
            Action action;
            lock (Pending)
            {
                if (Pending.Count == 0) return;
                action = Pending.Dequeue();
            }

            try
            {
                action();
            }
            catch (Exception e)
            {
                // One failed action must not stop the rest of the queue, or escape into the
                // Update that is pumping it.
                Plugin.BepinLogger.LogError($"[MainThreadActions] an action threw{Environment.NewLine}{e}");
            }
        }
    }
}
