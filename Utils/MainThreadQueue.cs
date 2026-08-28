using System;
using System.Collections.Generic;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// Holds messages produced anywhere until the main thread can display them.
/// </summary>
/// <remarks>
/// <para>Both of this mod's message sinks need the same thing. Messages arrive on threads that
/// are not Unity's - the Archipelago client's MessageLog callback fires on its websocket
/// thread, and <c>HandleConnectResult</c> runs on a ThreadPool thread - while displaying one
/// Instantiates a prefab, and every Unity API involved is main-thread-only. So a message
/// cannot be written where it is produced.</para>
/// <para>The writer returns false to mean "not ready yet, ask again later" rather than
/// throwing: the chat panel and the killfeed both only exist once the right scene is up, and
/// a message produced in a menu should wait rather than be dropped.</para>
/// </remarks>
internal sealed class MainThreadQueue
{
    /// <summary>
    /// Capped so a long session spent where nothing can be displayed cannot grow without
    /// bound. The BepInEx log has the complete record either way.
    /// </summary>
    private const int MaxPending = 80;

    /// <summary>
    /// Chat lines and killfeed lines both fade or scroll on their own timers, so a backlog
    /// released in one frame would push itself straight off the screen. Trickle it instead.
    /// </summary>
    private const int MaxPerFrame = 3;

    private readonly Queue<string> pending = new();
    private readonly Func<string, bool> write;
    private readonly string label;

    /// <param name="write">Displays one message; returns false if it cannot yet, which leaves
    /// the message at the head of the queue for a later frame.</param>
    /// <param name="label">Names this sink in error logs.</param>
    public MainThreadQueue(Func<string, bool> write, string label)
    {
        this.write = write;
        this.label = label;
    }

    public void Enqueue(string message)
    {
        lock (pending)
        {
            if (pending.Count >= MaxPending) pending.Dequeue();
            pending.Enqueue(message);
        }
    }

    // drains what it can, must be called by the main thread.
    public void Pump()
    {
        for (int i = 0; i < MaxPerFrame; i++)
        {
            string message;
            lock (pending)
            {
                if (pending.Count == 0) return;

                // Peeked rather than dequeued, so that a message the writer is not ready for
                // stays at the head instead of being lost. Safe without holding the lock
                // across the write: producers only ever append, and Pump is the sole consumer.
                message = pending.Peek();
            }

            bool written;
            try
            {
                written = write(message);
            }
            catch (Exception e)
            {
                // The message is already in the BepInEx log, so it is not lost. Drop it from
                // the queue rather than retrying it forever against a sink that is throwing.
                Plugin.BepinLogger.LogError($"[{label}] failed to write a line{Environment.NewLine}{e}");
                written = true;
            }

            if (!written) return;

            lock (pending)
            {
                if (pending.Count > 0) pending.Dequeue();
            }
        }
    }
}
