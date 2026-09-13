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

Both grenades call HandleExplosion from Update every frame they exist and return at once
unless explosionTimer is inside (-2, 0) - the one frame the blast resolves. Every patch here
tests that window first, so a grenade in flight costs one float read per frame and nothing
else.

Both live here rather than in suicideDetectPatches because the scope spans a kill and a
self-kill at once: one blast can do both, and the suicide side only reads what this sets.
*/

/// <summary>
/// Direct readers for the grenades' private fields, built once. Null when the game build has
/// renamed a field, in which case the patch that needs it does nothing rather than throw.
/// </summary>
internal static class HandGrenadeFields
{
    internal static readonly AccessTools.FieldRef<HandGrenade, float> ExplosionTimer =
        Resolve<HandGrenade, float>("explosionTimer");

    internal static readonly AccessTools.FieldRef<HandGrenade, GameObject> RootObject =
        Resolve<HandGrenade, GameObject>("_rootObject");

    internal static readonly AccessTools.FieldRef<HandGrenadeTwo, float> ExplosionTimerTwo =
        Resolve<HandGrenadeTwo, float>("explosionTimer");

    internal static readonly AccessTools.FieldRef<HandGrenadeTwo, bool> Touched =
        Resolve<HandGrenadeTwo, bool>("touched");

    internal static readonly AccessTools.FieldRef<HandGrenadeTwo, bool> TouchedTwo =
        Resolve<HandGrenadeTwo, bool>("touched2");

    /// <summary>Vanilla's own test at the top of both HandleExplosion bodies.</summary>
    internal static bool IsExplosionFrame(float explosionTimer) => explosionTimer < 0f && explosionTimer > -2f;

    private static AccessTools.FieldRef<TOwner, TField> Resolve<TOwner, TField>(string fieldName)
    {
        try
        {
            return AccessTools.FieldRefAccess<TOwner, TField>(fieldName);
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError(
                $"[KillDetect] {typeof(TOwner).Name}.{fieldName} not found - kills with it will not be reported: {error.Message}");
            return null;
        }
    }
}

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

    /// <param name="root">The grenade's _rootObject, already read by the caller.</param>
    internal static void Enter(HandGrenade grenade, GameObject root)
    {
        Exit();
        if (grenade == null) return;

        frame = Time.frameCount;

        // HandGrenade has no isOwner field, so gate on the thrower instead: _rootObject is the
        // player the grenade came from, and its controller is a NetworkBehaviour.
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
            if (HandGrenadeFields.ExplosionTimer == null || HandGrenadeFields.RootObject == null) return;
            if (!HandGrenadeFields.IsExplosionFrame(HandGrenadeFields.ExplosionTimer(__instance))) return;

            HandGrenadeScope.Enter(__instance, HandGrenadeFields.RootObject(__instance));
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
    /// <summary>
    /// The latches as they were on entry. Armed only on the explosion frame, so the postfix
    /// can tell the one call that resolved the blast from the many that returned at once.
    /// </summary>
    internal readonly struct Latches
    {
        public Latches(bool touched, bool touchedTwo)
        {
            Armed = true;
            Touched = touched;
            TouchedTwo = touchedTwo;
        }

        public bool Armed { get; }
        public bool Touched { get; }
        public bool TouchedTwo { get; }
    }

    static void Prefix(HandGrenadeTwo __instance, out Latches __state)
    {
        __state = default;

        if (HandGrenadeFields.ExplosionTimerTwo == null || HandGrenadeFields.Touched == null
            || HandGrenadeFields.TouchedTwo == null) return;
        if (!HandGrenadeFields.IsExplosionFrame(HandGrenadeFields.ExplosionTimerTwo(__instance))) return;

        __state = new Latches(HandGrenadeFields.Touched(__instance), HandGrenadeFields.TouchedTwo(__instance));
    }

    static void Postfix(HandGrenadeTwo __instance, Latches __state)
    {
        // Copied out because Harmony's analyzer reads any member access on __state as a write.
        Latches onEntry = __state;

        try
        {
            if (!onEntry.Armed || !__instance.isOwner) return;

            // Self and enemy latch independently, so one grenade that kills an enemy and the
            // thrower reports both. Only the enemy latch credits the pools.
            if (!onEntry.TouchedTwo && HandGrenadeFields.TouchedTwo(__instance))
            {
                string weaponName = KillDetectPatch.ResolveWeaponName(__instance);
                KillFeed.WriteKill(weaponName);
                KillDetectPatch.CreditKill(weaponName);
            }

            if (!onEntry.Touched && HandGrenadeFields.Touched(__instance))
            {
                KillFeed.WriteSelfKill(KillDetectPatch.ResolveWeaponName(__instance));
            }
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[KillDetect] HandGrenadeTwo postfix failed: {error}");
        }
    }
}
