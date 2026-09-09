using System;
using HarmonyLib;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

/*
The two hand grenades sit outside the sixteen emitters KillDetectPatch spans.

HandGrenade has no SendKillLog and no KillShockWave: its HandleExplosion calls
Settings.IncreaseKillsAmount() inline for each enemy and IncreaseSuicidesAmount() for itself,
so the counter is the only per-victim signal. The scope below opens around the explosion and
HandGrenadeKillPatch reports each increment that lands inside it.

HandGrenadeTwo calls neither counter and writes no log, so it is silent in vanilla. It does
latch `touched` (self) and `touched2` (enemy) one-shot, so the transition of those two bools
across HandleExplosion is the kill signal.

Both live here rather than in suicideDetectPatches because the scope spans a kill and a
self-kill at once: one blast can do both, and the suicide side only reads what this sets.
*/

/// <summary>
/// Marks the window during which a <see cref="HandGrenade"/> is resolving its victims, so kill
/// counter increments can be attributed to it.
/// </summary>
internal static class HandGrenadeScope
{
    static string weaponName;
    static bool thrownByLocalPlayer;
    static int frame = -1;

    // Frame-stamped as well as explicitly closed: if the original method throws, Harmony skips
    // the postfix and Exit never runs, so the stamp is what stops a leaked scope from affecting
    // an unrelated kill later on.
    static bool IsOpen => frame == Time.frameCount;

    /// <summary>Weapon name if the local player's hand grenade is exploding right now, else null.</summary>
    internal static string ActiveWeapon => IsOpen && thrownByLocalPlayer ? weaponName : null;

    /// <summary>
    /// True while someone else's hand grenade is exploding on this machine. HandGrenade's
    /// HandleExplosion has no ownership gate, so each peer simulates its own copy and its
    /// IncreaseSuicidesAmount call fires everywhere.
    /// </summary>
    internal static bool SuppressSuicide => IsOpen && !thrownByLocalPlayer;

    internal static void Enter(HandGrenade grenade)
    {
        Exit();
        if (grenade == null) return;

        frame = Time.frameCount;

        // HandGrenade has no isOwner field, so gate on the thrower instead: _rootObject is the
        // player the grenade came from, and its controller is a NetworkBehaviour.
        GameObject root = Traverse.Create(grenade).Field<GameObject>("_rootObject").Value;
        FirstPersonController thrower = root != null ? root.GetComponent<FirstPersonController>() : null;
        if (thrower == null || !thrower.IsOwner) return;

        thrownByLocalPlayer = true;
        weaponName = KillDetectPatch.ResolveWeaponName(grenade);
    }

    internal static void Exit()
    {
        weaponName = null;
        thrownByLocalPlayer = false;
        frame = -1;
    }
}

/// <summary>
/// Opens and closes <see cref="HandGrenadeScope"/> around a hand grenade's explosion.
/// </summary>
[HarmonyPatch(typeof(HandGrenade), "HandleExplosion")]
public class HandGrenadeExplosionPatch
{
    static void Prefix(HandGrenade __instance)
    {
        try
        {
            HandGrenadeScope.Enter(__instance);
        }
        catch (Exception error)
        {
            // A throwing prefix would suppress the explosion entirely.
            HandGrenadeScope.Exit();
            Plugin.BepinLogger.LogError($"[KillDetect] HandGrenade prefix failed: {error}");
        }
    }

    static void Postfix() => HandGrenadeScope.Exit();
}

/// <summary>
/// Reports hand-grenade kills only. Every other weapon reaches this counter too but is already
/// reported by <see cref="KillDetectPatch"/>, so anything outside an open hand-grenade scope is
/// ignored here.
/// </summary>
[HarmonyPatch(typeof(Settings), "IncreaseKillsAmount")]
public class HandGrenadeKillPatch
{
    static void Postfix()
    {
        try
        {
            string weaponName = HandGrenadeScope.ActiveWeapon;
            if (weaponName == null) return;

            KillFeed.WriteKill(weaponName);

            // Undecorated already - the Bublee suffix is the only decoration there is, and a
            // hand grenade is never a Bubble.
            KillDetectPatch.CreditKill(weaponName);
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[KillDetect] HandGrenade kill postfix failed: {error}");
        }
    }
}

/// <summary>
/// Reports a HandGrenadeTwo's kill and self-kill by watching its two one-shot latches across
/// HandleExplosion, which is otherwise silent.
/// </summary>
[HarmonyPatch(typeof(HandGrenadeTwo), "HandleExplosion")]
public class HandGrenadeTwoExplosionPatch
{
    // __state carries the latches as they were on entry: { touched, touched2 }.
    // HandleExplosion is called from Update and early-returns until the fuse window opens, so
    // the transition is what distinguishes the one call that killed from the many that did not.
    static void Prefix(HandGrenadeTwo __instance, out bool[] __state)
    {
        __state = new[] { Latch(__instance, "touched"), Latch(__instance, "touched2") };
    }

    static void Postfix(HandGrenadeTwo __instance, bool[] __state)
    {
        try
        {
            if (__state == null || !__instance.isOwner) return;

            // Self and enemy latch independently, so one grenade that kills an enemy and the
            // thrower reports both. Only the enemy latch credits the pools.
            if (!__state[1] && Latch(__instance, "touched2"))
            {
                string weaponName = KillDetectPatch.ResolveWeaponName(__instance);
                KillFeed.WriteKill(weaponName);
                KillDetectPatch.CreditKill(weaponName);
            }

            if (!__state[0] && Latch(__instance, "touched"))
            {
                KillFeed.WriteSelfKill(KillDetectPatch.ResolveWeaponName(__instance));
            }
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[KillDetect] HandGrenadeTwo postfix failed: {error}");
        }
    }

    static bool Latch(HandGrenadeTwo grenade, string field) =>
        Traverse.Create(grenade).Field<bool>(field).Value;
}
