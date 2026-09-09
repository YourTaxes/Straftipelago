using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Utils;

namespace Straftapelago.Finnegan_McD.org.Patches;

/*
Suicide detection, the other half of killDetectPatches.

A suicide is harder to name than a kill because the signal carries no weapon:
Settings.IncreaseSuicidesAmount() is an argument-less counter. Three mechanisms cover the three
shapes a suicide comes in, cheapest first:

  1. The emitter is on the call stack. Most weapons route their own self-hits to the counter
     from inside their explosion loop, so the object calling it IS the weapon. SuicideScopes
     patches every caller of the counter to record its instance, and SuicideSource reads it back.

  2. The emitter is on the call stack but shares its class with other weapons, so only the
     instance can name the weapon. That is KillDetectPatch.ResolveWeaponName's job.

  3. Nothing is on the call stack at all: an acid pool ticks damage and a later tick kills.
     AcidZones covers that one.

Environmental deaths reach none of the three, which is correct: they have no weapon, and the
message drops the clause.
*/

/// <summary>
/// Fires once per suicide, on the dying player's own machine, so the victim is by definition
/// the local player and no argument is needed. Every per-weapon wrapper and every direct caller
/// of the counter funnels here, including Obus and HandGrenade, which are silent in vanilla.
/// </summary>
[HarmonyPatch(typeof(Settings), "IncreaseSuicidesAmount")]
public class SuicideDetectPatch
{
    static void Postfix()
    {
        try
        {
            // Every other caller of this counter is owner-gated by vanilla. HandGrenade is the
            // exception.
            if (HandGrenadeScope.SuppressSuicide) return;

            // Null for a death with no weapon behind it, which is the plain message's job.
            KillFeed.WriteSelfKill(SuicideSource.Resolve());
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[SuicideDetect] Suicide postfix failed: {error}");
        }
    }
}

/// <summary>
/// The scope hooks themselves. Plain static methods rather than a [HarmonyPatch] class because
/// their targets are discovered at runtime and installed one at a time.
/// </summary>
internal static class ScopeHooks
{
    internal static void EnterSuicideSource(object __instance) => SuicideSource.Emitting.Enter(__instance);

    internal static void ExitSuicideSource() => SuicideSource.Emitting.Exit();
}

/// <summary>
/// Installs the emitter scope. Called from Plugin after PatchAll rather than by it: these
/// targets come from an IL search and there are dozens of them, so each is patched inside its
/// own try/catch instead of letting one unpatchable method abort the mod's initialization.
/// </summary>
internal static class SuicideScopes
{
    internal static void Install(Harmony harmony)
    {
        MethodInfo counter = AccessTools.Method(typeof(Settings), "IncreaseSuicidesAmount");
        if (counter == null)
        {
            Plugin.BepinLogger.LogError(
                "[SuicideDetect] Settings.IncreaseSuicidesAmount not found - suicides will not name a weapon.");
            return;
        }

        Scope(harmony,
            CallerSearch.Of(new MethodBase[] { counter }, "the suicide counter"),
            nameof(ScopeHooks.EnterSuicideSource),
            nameof(ScopeHooks.ExitSuicideSource));
    }

    // Prefix and finalizer, not prefix and postfix: a finalizer runs on the exception path too,
    // so a throwing explosion cannot leave an instance on the scope stack forever.
    static void Scope(Harmony harmony, List<MethodBase> targets, string enter, string exit)
    {
        int patched = 0;
        foreach (MethodBase target in targets)
        {
            try
            {
                harmony.Patch(target, prefix: Hook(enter), finalizer: Hook(exit));
                patched++;
            }
            catch (Exception error)
            {
                Plugin.BepinLogger.LogError(
                    $"[SuicideDetect] Could not scope {target.DeclaringType?.Name}.{target.Name}: {error}");
            }
        }

        Plugin.BepinLogger.LogInfo($"[SuicideDetect] Scoped {patched} of {targets.Count} target(s) for {enter}.");
    }

    static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(ScopeHooks), name);
}
