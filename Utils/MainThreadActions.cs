using System;
using System.Collections.Generic;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// Holds work produced off Unity's thread until the main thread can run it.
/// </summary>
/// <remarks>
/// <para>The Archipelago client decides things on threads that are not Unity's - slot data is
/// read in <c>HandleConnectResult</c> on a ThreadPool thread, and items arrive on the client's
/// websocket thread - while acting on either of them is main-thread-only. Applying Green Mode
/// touches <c>Object.FindObjectsOfType</c> and instantiates a killfeed line; rebuilding the
/// roulette pool reads <c>SpawnerManager</c>. So the decision and the action have to be split.</para>
/// <para>Deliberately not <see cref="MainThreadQueue"/>: that one holds strings, hands each to a
/// writer that may answer "not ready, ask me later", and trickles at most three per frame so
/// chat lines do not scroll off screen. None of that applies to a one-shot action, which runs
/// once, cannot be deferred by its own return value, and has nothing to fade.</para>
/// </remarks>
internal static class MainThreadActions
{
    /// <summary>
    /// Capped so a session that somehow never pumps cannot grow without bound. Generous next to
    /// <see cref="MainThreadQueue"/>'s cap because the producers here are connects and item
    /// receipts, not a chat feed - a room dumping a full starting inventory at once is the
    /// realistic worst case.
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
    /// Runs everything waiting. Must be called from the main thread - see
    /// <see cref="ArchipelagoOverlay.Update"/>.
    /// </summary>
    /// <remarks>
    /// Drains fully rather than a few per frame: these are not messages competing for screen
    /// space, and holding half a starting inventory back for later frames would let the player
    /// walk into a match with a pool the room has already finished filling.
    /// </remarks>
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
                // One failed action must not stop the rest of the queue, and must not escape
                // into the Update that is pumping us.
                Plugin.BepinLogger.LogError($"[MainThreadActions] an action threw{Environment.NewLine}{e}");
            }
        }
    }
}
