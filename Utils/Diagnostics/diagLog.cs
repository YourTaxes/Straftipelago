using System.Collections.Generic;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// Switches that disable individual pieces of mod behaviour, for bisecting which one is
/// responsible for a fault. With a flag set to true the mod is deliberately partly disabled.
/// </summary>
public static class DiagnosticFlags
{
    /// <summary>Skips RouletteState.Reset() in PlayerPickupAwakePatch.</summary>
    public static bool SkipRouletteResetOnAwake = false;

    /// <summary>Skips runtime SpawnablePrefabs registration in RoulettePrefabRegistration.</summary>
    public static bool SkipPrefabRegistration = false;

    /// <summary>Skips replacing ItemSpawner.itemToSpawn with the roulette prefab.</summary>
    public static bool SkipRouletteSpawnerReplacement = false;

    /// <summary>OnGrab still observes and logs, but never rolls or sends.</summary>
    public static bool SkipRouletteRoll = false;

    /// <summary>
    /// The [RR:...] roll trace. On by default: one line per step per roll, not per frame.
    /// </summary>
    public static bool VerboseRouletteLogging = true;
}

/// <summary>
/// The mod's diagnostic log. <see cref="LogOnChange"/> only emits when an object's state
/// string differs from the last one recorded for it, which turns a per-frame probe into one
/// line per transition.
/// </summary>
public static class DiagLog
{
    private static readonly Dictionary<int, string> lastState = new();

    // Every round respawns the player objects, so each round contributes a fresh set of
    // instance IDs that will never be seen again. Drop the whole table once it gets large.
    private const int MaxTrackedObjects = 512;

    public static void LogOnChange(Object owner, string label, string state)
    {
        int key = owner.GetInstanceID();
        if (lastState.TryGetValue(key, out string previous) && previous == state) return;

        if (lastState.Count >= MaxTrackedObjects) lastState.Clear();

        lastState[key] = state;
        Plugin.BepinLogger.LogInfo($"[Diag:{label}] frame={Time.frameCount} id={key} {state}");
    }

    public static void Log(string label, string message)
    {
        Plugin.BepinLogger.LogInfo($"[Diag:{label}] frame={Time.frameCount} {message}");
    }

    /// <summary>
    /// One step of a roulette roll, tagged with the roll's id so a client's lines can be
    /// stitched to the host's across two log files. The healthy sequence is
    /// grab, roll, send (owner client), server-spawn (host), then setspawned, equip, equipped,
    /// despawn (owner client).
    /// </summary>
    public static void RR(int rollId, string step, string message)
    {
        if (!DiagnosticFlags.VerboseRouletteLogging) return;
        Plugin.BepinLogger.LogInfo($"[RR:{step} #{rollId}] frame={Time.frameCount} {message}");
    }

    /// <summary>Unity's == reports a destroyed object as null too, which is what is wanted here.</summary>
    public static string Describe(Object o) => o == null ? "null" : o.name;

    public static string NetRoles() =>
        $"IsServer={FishNet.InstanceFinder.IsServer} IsClient={FishNet.InstanceFinder.IsClient}";
}
