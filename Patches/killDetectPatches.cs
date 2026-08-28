using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

/*
Central kill detection. Replaces the 13 per-weapon patches this file used to hold.

Why not PauseManager.WriteLog / MatchLogs.WriteLog, which the old comment here
proposed: a patch on either gets __instance of PauseManager, never the weapon.
The weapon only survives as text baked into the string argument, across three
inconsistent message shapes - the plain "was killed with a" form, the
headshot/vowel ternary form (which also has a trailing-space bug in Minigun and
LargeRaycastGun), and Bubble/Obus which hardcode their names with no field to
read. Parsing that is not worth it.

Why not KillShockWave -> Settings.IncreaseKillsAmount: that IS a genuine
universal funnel - all 16 kill emitters reach it exactly once per kill, on the
killer's machine - but both methods take zero arguments, and KillShockWave is
called BEFORE the victim is named in 14 of the 16 branches. No victim means no
per-victim self-kill test, which is what this needs.

What is used instead: every emitter exposes a victim-carrying method, and every
one names the parameter `enemyHealth`, so a single TargetMethods() patch spans
all sixteen with uniform Harmony name injection.

Self-kills are mostly vanilla's job already: each explosion loop branches per
victim on `victim.transform.gameObject == _rootObject` and routes self-hits to
IncreaseSuicidesAmount() instead of KillShockWave()/SendKillLog(). Claymore uses
two independent latches, so one blast killing an enemy AND yourself runs both
branches and the enemy still counts. Bubble and PhysicsProp are the exceptions -
they have no self-check anywhere - which is why the enemyHealth.IsOwner test
below is load-bearing and not just a cheap assertion.

The two hand grenades sit outside that set entirely and are handled separately
further down - see the comment above HandGrenadeScope.

One inherited quirk: Claymore/Bubble/PhysicsProp guard SendKillLog with a
one-shot bool, so one claymore killing two enemies logs once. That mirrors the
in-game kill feed exactly, so it is left alone.
*/

[HarmonyPatch]
public class KillDetectPatch
{
    // Held weapons. All derive from Weapon and kill through
    // KillServer(PlayerHealth enemyHealth).
    static readonly Type[] HeldWeapons =
    {
        typeof(Gun), typeof(Shotgun), typeof(Minigun), typeof(MeleeWeapon),
        typeof(BeamGun), typeof(ChargeGun), typeof(LargeRaycastGun), typeof(RepulsiveGun)
    };

    // Detached killers - thrown, placed or fired things that are not in the
    // player's hand at kill time. No common base below MonoBehaviour, but they
    // all kill through SendKillLog(PlayerHealth enemyHealth).
    static readonly Type[] DetachedWeapons =
    {
        typeof(Claymore), typeof(ProximityMine), typeof(PhysicsGrenade), typeof(Obus),
        typeof(Bubble), typeof(ShrapnelBallistic), typeof(PredictedProjectile), typeof(PhysicsProp)
    };

    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (MethodBase m in Resolve(HeldWeapons, "KillServer")) yield return m;
        foreach (MethodBase m in Resolve(DetachedWeapons, "SendKillLog")) yield return m;
    }

    // A null target makes Harmony throw and takes the whole PatchAll() down with
    // it, so a method renamed by a future game build is reported and skipped
    // rather than killing every other patch in the mod.
    static IEnumerable<MethodBase> Resolve(Type[] types, string methodName)
    {
        foreach (Type t in types)
        {
            MethodBase m = AccessTools.Method(t, methodName);
            if (m != null)
            {
                yield return m;
            }
            else
            {
                Plugin.BepinLogger.LogError(
                    $"[KillDetect] {t.Name}.{methodName} not found - kills with it will not be reported.");
            }
        }
    }

    // Postfix, not prefix: a throw here cannot suppress vanilla's kill handling.
    // __instance is typed as object because the 16 targets share no base type
    // below MonoBehaviour - Weapon, NetworkBehaviour and MonoBehaviour are all
    // represented in the set.
    static void Postfix(object __instance, PlayerHealth enemyHealth)
    {
        try
        {
            if (enemyHealth == null) return;

            // PhysicsProp's kill branch (OnControllerColliderHit) has no
            // ownership check at all, unlike every other emitter, so without
            // this a non-owning peer could report someone else's prop kill.
            if (__instance is PhysicsProp prop && !prop.IsOwner) return;

            // True only when the victim is the local player. Vanilla already
            // routes self-hits away from these methods for 14 of the 16
            // emitters; Bubble and PhysicsProp are the two that do not.
            if (enemyHealth.IsOwner)
            {
                KillFeed.WriteSelfKill();
                return;
            }

            KillFeed.WriteKill(ResolveWeaponName(__instance));
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError($"[KillDetect] Postfix failed for {__instance?.GetType().Name}: {e}");
        }
    }

    // Field names and types are inconsistent across the emitters - weaponName is
    // a string on some, the ItemBehaviour is spelled `behaviour` on Weapon but
    // `behavior` on ShrapnelBallistic, and `weapon` / `_gun` are references
    // rather than names. Rather than hard-coding one shape per type, try each
    // candidate field and interpret whatever comes back.
    static readonly string[] NameFields = { "weaponName", "behaviour", "behavior", "weapon", "_gun" };

    internal static string ResolveWeaponName(object emitter)
    {
        if (emitter == null) return "something";

        foreach (string field in NameFields)
        {
            string name = Interpret(Traverse.Create(emitter).Field(field).GetValue());
            if (name == null) continue;

            // Obus and Bubble are both spawned by the same DualLauncher and
            // hold it in _gun, so the resolved name identifies the gun and is
            // identical for both. The projectile type is what tells them apart.
            return emitter is Bubble ? $"{name} (Bublee)" : name;
        }

        return Fallback(emitter);
    }

    // Turns whatever a candidate field held into a weapon name, or null if it
    // held nothing useful. Note the explicit `uo == null` tests: a destroyed
    // UnityEngine.Object is a "fake null" that still matches a type pattern, so
    // without them GetComponent would throw on a projectile whose weapon has
    // already been despawned - which is exactly when a kill is being reported.
    static string Interpret(object value)
    {
        if (value is UnityEngine.Object uo && uo == null) return null;

        switch (value)
        {
            case null: return null;
            case string s: return Clean(s);
            case ItemBehaviour ib: return Clean(ib.weaponName);
            case GameObject go: return Clean(go.GetComponent<ItemBehaviour>()?.weaponName);
            case Component c: return Clean(c.GetComponent<ItemBehaviour>()?.weaponName);
            default: return null;
        }
    }

    // Used when no field yielded a name: an unset inspector field, or Obus and
    // Bubble, which hardcode their names in the game's own log strings and have
    // no name data at all to read.
    static string Fallback(object emitter)
    {
        switch (emitter)
        {
            case Obus _: return "grenade launcher";
            case Bubble _: return "the Bublee";
            case Claymore _: return "claymore";
            case ProximityMine _: return "proximity mine";
            case PhysicsGrenade _: return "grenade";
            case ShrapnelBallistic _: return "shrapnel";
            case PredictedProjectile _: return "projectile";
            case PhysicsProp p: return Clean(p.popupText?.ToLower()) ?? "physics prop";
            case HandGrenade _: return "hand grenade";
            case HandGrenadeTwo _: return "hand grenade";
            default: return emitter.GetType().Name;
        }
    }

    // Empty and whitespace-only names are as useless as null, and unset
    // inspector fields produce them, so collapse both so the caller falls
    // through to the next candidate.
    static string Clean(string name) => string.IsNullOrWhiteSpace(name) ? null : name;
}

/// <summary>
/// Fires once per suicide, on the dying player's own machine, so the victim is
/// by definition the local player and no argument is needed. Every per-weapon
/// IncreaseSuicidesAmount() wrapper and every direct caller funnels here, which
/// covers both environmental self-damage and dying to your own explosive -
/// including Obus and HandGrenade, which credit a suicide but never set
/// PlayerHealth.suicide, and so are silent in vanilla.
/// </summary>
[HarmonyPatch(typeof(Settings), "IncreaseSuicidesAmount")]
public class SuicideDetectPatch
{
    static void Postfix()
    {
        try
        {
            // Every other caller of this counter is owner-gated by vanilla, so
            // it only ever runs on the dying player's machine. HandGrenade is
            // the exception - see HandGrenadeScope.SuppressSuicide.
            if (HandGrenadeScope.SuppressSuicide) return;

            KillFeed.WriteSelfKill();
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError($"[KillDetect] Suicide postfix failed: {e}");
        }
    }
}

/*
The two hand grenades are the odd ones out and need their own handling.

HandGrenade has no SendKillLog and no KillShockWave. Its HandleExplosion loops
over victims and calls Settings.IncreaseKillsAmount() inline for each enemy and
IncreaseSuicidesAmount() for itself, so the only per-victim signal is that
counter. HandGrenadeExplosionPatch opens a scope around the explosion and
HandGrenadeKillPatch reports each increment that lands inside it - which handles
a grenade killing two people correctly, since vanilla increments twice.

HandGrenadeTwo is worse: it kills but calls NEITHER counter and writes no log, so
it is completely silent in vanilla. It does latch `touched` (self) and `touched2`
(enemy) one-shot, so the transition of those two bools across HandleExplosion is
the kill signal. At most one of each per grenade, by vanilla's own design.
*/

/// <summary>
/// Marks the window during which a <see cref="HandGrenade"/> is resolving its
/// victims, so kill-counter increments can be attributed to it.
/// </summary>
internal static class HandGrenadeScope
{
    static string weaponName;
    static bool thrownByLocalPlayer;
    static int frame = -1;

    // Frame-stamped as well as explicitly closed: if the original method throws,
    // Harmony skips the postfix and Exit never runs, so the stamp is what stops
    // a leaked scope from affecting an unrelated kill later on.
    static bool IsOpen => frame == Time.frameCount;

    /// <summary>Weapon name if the local player's hand grenade is exploding right now, else null.</summary>
    internal static string ActiveWeapon => IsOpen && thrownByLocalPlayer ? weaponName : null;

    /// <summary>
    /// True while someone else's hand grenade is exploding on this machine.
    /// Unlike every other emitter, HandGrenade.HandleExplosion has no ownership
    /// gate, so each peer simulates its own copy and its IncreaseSuicidesAmount()
    /// call fires everywhere - which would otherwise announce a thrower's
    /// self-kill on every player's screen.
    /// </summary>
    internal static bool SuppressSuicide => IsOpen && !thrownByLocalPlayer;

    internal static void Enter(HandGrenade grenade)
    {
        Exit();
        if (grenade == null) return;

        frame = Time.frameCount;

        // HandGrenade has no isOwner field, so gate on the thrower instead:
        // _rootObject is the player the grenade came from, and its controller is
        // a NetworkBehaviour.
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

[HarmonyPatch(typeof(HandGrenade), "HandleExplosion")]
public class HandGrenadeExplosionPatch
{
    static void Prefix(HandGrenade __instance)
    {
        try
        {
            HandGrenadeScope.Enter(__instance);
        }
        catch (Exception e)
        {
            // A throwing prefix would suppress the explosion entirely.
            HandGrenadeScope.Exit();
            Plugin.BepinLogger.LogError($"[KillDetect] HandGrenade prefix failed: {e}");
        }
    }

    static void Postfix() => HandGrenadeScope.Exit();
}

/// <summary>
/// Reports hand-grenade kills only. Every other weapon reaches this counter too,
/// but is already reported by <see cref="KillDetectPatch"/>, so anything outside
/// an open hand-grenade scope is ignored here to avoid double-printing.
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
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError($"[KillDetect] HandGrenade kill postfix failed: {e}");
        }
    }
}

[HarmonyPatch(typeof(HandGrenadeTwo), "HandleExplosion")]
public class HandGrenadeTwoExplosionPatch
{
    // __state carries the latches as they were on entry: { touched, touched2 }.
    // HandleExplosion is called from Update and early-returns until the fuse
    // window opens, so comparing the transition is what distinguishes the one
    // call that actually killed from the many that did nothing.
    static void Prefix(HandGrenadeTwo __instance, out bool[] __state)
    {
        __state = new[] { Latch(__instance, "touched"), Latch(__instance, "touched2") };
    }

    static void Postfix(HandGrenadeTwo __instance, bool[] __state)
    {
        try
        {
            if (__state == null || !__instance.isOwner) return;

            // Self and enemy latch independently, so one grenade that kills an
            // enemy and the thrower reports both - a kill and a no-credit line.
            if (!__state[1] && Latch(__instance, "touched2"))
            {
                KillFeed.WriteKill(KillDetectPatch.ResolveWeaponName(__instance));
            }

            if (!__state[0] && Latch(__instance, "touched"))
            {
                KillFeed.WriteSelfKill();
            }
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError($"[KillDetect] HandGrenadeTwo postfix failed: {e}");
        }
    }

    static bool Latch(HandGrenadeTwo grenade, string field) =>
        Traverse.Create(grenade).Field<bool>(field).Value;
}

/// <summary>
/// Debug output for kill detection: the in-game kill feed, plus LogOutput.log so
/// lines survive the session and can be diffed between host and client.
/// </summary>
internal static class KillFeed
{
    // Every emitter reports on the killer's own machine, so the local player is
    // always the subject of the line.
    static string LocalPlayerName => ClientInstance.Instance?.PlayerName ?? "Player";

    internal static void WriteKill(string weaponName) =>
        Write($"{LocalPlayerName} got a kill with {weaponName}");

    internal static void WriteSelfKill() =>
        Write($"{LocalPlayerName} killed themselves, no credit for kill");

    internal static void Write(string message)
    {
        // Deliberately not ArchipelagoConsole.LogMessage - it forwards to
        // BepinLogger itself, which would double-print to the console, and its
        // overlay would duplicate the kill feed on screen.
        Plugin.BepinLogger.LogInfo($"[KillDetect] {message}");

        try
        {
            // WriteLocalLog instantiates a chat line locally with no network
            // traffic, so this stays on the killer's screen instead of spamming
            // the lobby. MatchLogs is a NetworkBehaviour singleton and is null
            // in menus; MatchLogsOffline is the live one in singleplayer.
            if (MatchLogs.Instance != null)
            {
                MatchLogs.Instance.WriteLocalLog(message);
            }
            else if (MatchLogsOffline.Instance != null)
            {
                MatchLogsOffline.Instance.WriteLog(message);
            }
        }
        catch (Exception e)
        {
            // The console line above already landed; a missing or half-built
            // feed must never take down the kill path.
            Plugin.BepinLogger.LogError($"[KillDetect] Could not write to the kill feed: {e}");
        }
    }
}
