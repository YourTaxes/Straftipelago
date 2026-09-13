using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// The mod's event-level trace lines. Written at Debug level, which BepInEx's default filter
/// keeps out of both the console and LogOutput.log; enable Debug in BepInEx.cfg to read them.
/// Every caller is an event (a roll, a spawn, a rebuild), never a frame.
/// </summary>
public static class DiagLog
{
    public static void Log(string label, string message)
    {
        Plugin.BepinLogger.LogDebug($"[Diag:{label}] frame={Time.frameCount} {message}");
    }

    /// <summary>
    /// One step of a roulette roll, tagged with the roll's id so a client's lines can be
    /// stitched to the host's across two log files. The healthy sequence is
    /// grab, roll, send (owner client), server-spawn (host), then setspawned, equip, equipped,
    /// despawn (owner client).
    /// </summary>
    public static void RR(int rollId, string step, string message)
    {
        Plugin.BepinLogger.LogDebug($"[RR:{step} #{rollId}] frame={Time.frameCount} {message}");
    }

    /// <summary>Unity's == reports a destroyed object as null too, which is what is wanted here.</summary>
    public static string Describe(Object target) => target == null ? "null" : target.name;

    public static string NetRoles() =>
        $"IsServer={FishNet.InstanceFinder.IsServer} IsClient={FishNet.InstanceFinder.IsClient}";
}
