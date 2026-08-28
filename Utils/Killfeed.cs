using BepInEx;

namespace Straftapelago.Finnegan_McD.org.Utils;

// writes a line to the killfeed.
internal static class Killfeed
{
    private static readonly MainThreadQueue Queue = new(TryWrite, "Killfeed");

    public static void Write(string message)
    {
        if (message.IsNullOrWhiteSpace()) return;

        Plugin.BepinLogger.LogMessage(message);
        Queue.Enqueue(message);
    }

    // drains queued messages into the killfeed. must be called from the main thread
    public static void Pump() => Queue.Pump();

    private static bool TryWrite(string message)
    {
        // if the values are in place to write, then write to the kill feed.
        if (PauseManager.Instance == null || MatchLogsOffline.Instance == null) return false;

        WriteToKillfeed(message);
        return true;
    }

    private static void WriteToKillfeed(string text) => PauseManager.Instance.WriteOfflineLog(text);
}
