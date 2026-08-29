using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

/*
Central kill detection. Replaces the 13 per-weapon patches this file used to
hold. Suicides live in suicideDetectPatches, which reuses this file's
ResolveWeaponName and KillFeed - naming a weapon is the same problem either way,
it is only the signal that differs.

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
    internal static readonly Type[] HeldWeapons =
    {
        typeof(Gun), typeof(Shotgun), typeof(Minigun), typeof(MeleeWeapon),
        typeof(BeamGun), typeof(ChargeGun), typeof(LargeRaycastGun), typeof(RepulsiveGun)
    };

    // Detached killers - thrown, placed or fired things that are not in the
    // player's hand at kill time. No common base below MonoBehaviour, but they
    // all kill through SendKillLog(PlayerHealth enemyHealth).
    internal static readonly Type[] DetachedWeapons =
    {
        typeof(Claymore), typeof(ProximityMine), typeof(PhysicsGrenade), typeof(Obus),
        typeof(Bubble), typeof(ShrapnelBallistic), typeof(PredictedProjectile), typeof(PhysicsProp)
    };

    static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (MethodBase method in Resolve(HeldWeapons, "KillServer")) yield return method;
        foreach (MethodBase method in Resolve(DetachedWeapons, "SendKillLog")) yield return method;
    }

    // A null target makes Harmony throw and takes the whole PatchAll() down with
    // it, so a method renamed by a future game build is reported and skipped
    // rather than killing every other patch in the mod.
    static IEnumerable<MethodBase> Resolve(Type[] types, string methodName)
    {
        foreach (Type weaponType in types)
        {
            MethodBase method = AccessTools.Method(weaponType, methodName);
            if (method != null)
            {
                yield return method;
            }
            else
            {
                Plugin.BepinLogger.LogError(
                    $"[KillDetect] {weaponType.Name}.{methodName} not found - kills with it will not be reported.");
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
                KillFeed.WriteSelfKill(ResolveWeaponName(__instance));
                return;
            }

            KillFeed.WriteKill(ResolveWeaponName(__instance));
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[KillDetect] Postfix failed for {__instance?.GetType().Name}: {error}");
        }
    }

    // Field names and types are inconsistent across the emitters - weaponName is
    // a string on some, the ItemBehaviour is spelled `behaviour` on Weapon but
    // `behavior` on ShrapnelBallistic, and `weapon` / `_gun` are references
    // rather than names. Rather than hard-coding one shape per type, try each
    // candidate field and interpret whatever comes back.
    //
    // Order matters, and the references come first. A projectile's own
    // weaponName describes the projectile, not the weapon that fired it:
    // ShrapnelBallistic sets it to "shrapnel", so checking it first named every
    // HK_caws death "shrapnel" while the gun's real name sat one field over in
    // `behavior`. The ItemBehaviour is what the game itself displays, so it wins
    // wherever both exist, and weaponName is the last resort it always was.
    static readonly string[] NameFields = { "behaviour", "behavior", "weapon", "_gun", "weaponName" };

    /// <param name="allowGenericName">
    /// False for suicides, where the emitter is discovered rather than handed
    /// over: a generic name would be wrong for anything the search turned up
    /// that is not a weapon at all, so those report nothing instead.
    /// </param>
    internal static string ResolveWeaponName(object emitter, bool allowGenericName = true)
    {
        if (emitter == null) return allowGenericName ? "something" : null;

        List<string> trail = new List<string>();
        string name = ScanForName(emitter, 0, trail);

        Diagnose(emitter, name, trail);

        if (name != null)
        {
            // Obus and Bubble are both spawned by the same DualLauncher and
            // hold it in _gun, so the resolved name identifies the gun and is
            // identical for both. The projectile type is what tells them apart.
            return emitter is Bubble ? $"{name} (Bublee)" : name;
        }

        return allowGenericName ? Fallback(emitter) : null;
    }

    /*
    One class serves many weapons - a rocket launcher and a Mortini are both
    PredictedProjectile, both hand grenades are both PhysicsGrenade - so the type
    can never name the weapon. Only the instance can: it holds the gun that fired
    it, and that gun's ItemBehaviour carries the name the game shows.

    NameFields covers the emitters whose field spelling is known. Past that the
    search is generic, because the alternative is a hardcoded shape per emitter
    and there are emitters that no list in these files mentions.
    */
    static string ScanForName(object emitter, int depth, List<string> trail)
    {
        foreach (string field in NameFields)
        {
            string name = Interpret(Traverse.Create(emitter).Field(field).GetValue());
            if (name == null) continue;

            trail.Add(field);
            return name;
        }

        if (depth >= MaxScanDepth) return null;

        // Anything holding an ItemBehaviour directly, whatever it is called.
        foreach (FieldInfo field in InstanceFields(emitter.GetType()))
        {
            string name = InterpretUnnamedField(ReadField(emitter, field));
            if (name == null) continue;

            trail.Add(field.Name);
            return name;
        }

        // Then one hop through the references that plausibly lead to the gun.
        // A coroutine's `<>4__this` is why "this" is in the hint list: when the
        // self-branch lives in an iterator, the recorded instance is the state
        // machine and the weapon is a field on it.
        foreach (FieldInfo field in InstanceFields(emitter.GetType()))
        {
            if (!IsSourceHint(field.Name)) continue;

            object value = ReadField(emitter, field);
            if (!IsGameObjectWorthFollowing(value)) continue;

            trail.Add(field.Name);

            string name = ScanForName(value, depth + 1, trail);
            if (name != null) return name;

            trail.RemoveAt(trail.Count - 1);
        }

        return null;
    }

    // Two hops is emitter -> gun -> ItemBehaviour, which is as deep as any known
    // shape goes. Deeper only buys the chance of surfacing an unrelated weapon.
    const int MaxScanDepth = 2;

    // Deliberately narrow. Following an owner or a root object would reach the
    // player and then whatever they happen to be holding, which is a plausible
    // looking but wrong name - worse in a kill feed than no name at all.
    static readonly string[] SourceHints = { "gun", "weapon", "item", "launcher", "this" };

    static bool IsSourceHint(string fieldName)
    {
        foreach (string hint in SourceHints)
        {
            if (fieldName.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }

        return false;
    }

    // Only the game's own types, which includes compiler generated state machine
    // types. Unity and BCL types lead into the scene graph and never to a name.
    static readonly Assembly GameAssembly = typeof(Settings).Assembly;

    static bool IsGameObjectWorthFollowing(object value)
    {
        if (value == null) return false;
        if (value is UnityEngine.Object unityObject && unityObject == null) return false;

        return value.GetType().Assembly == GameAssembly;
    }

    // Cached: an unknown emitter is walked field by field, and a suicide can
    // land in the middle of a firefight.
    static readonly Dictionary<Type, FieldInfo[]> FieldCache = new Dictionary<Type, FieldInfo[]>();

    static FieldInfo[] InstanceFields(Type type)
    {
        if (FieldCache.TryGetValue(type, out FieldInfo[] cached)) return cached;

        const BindingFlags Declared =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        List<FieldInfo> fields = new List<FieldInfo>();

        // Stopping at MonoBehaviour keeps Unity's own internals out of the walk.
        for (Type current = type;
             current != null && current != typeof(MonoBehaviour) && current != typeof(object);
             current = current.BaseType)
        {
            foreach (FieldInfo field in current.GetFields(Declared))
            {
                if (!field.FieldType.IsPrimitive && !field.FieldType.IsEnum) fields.Add(field);
            }
        }

        FieldInfo[] resolved = fields.ToArray();
        FieldCache[type] = resolved;
        return resolved;
    }

    static object ReadField(object owner, FieldInfo field)
    {
        try
        {
            return field.GetValue(owner);
        }
        catch
        {
            // A field on a destroyed object can throw rather than return null.
            return null;
        }
    }

    // Turns whatever a candidate field held into a weapon name, or null if it
    // held nothing useful. Note the explicit `unityObject == null` test: a
    // destroyed UnityEngine.Object is a "fake null" that still matches a type
    // pattern, so without it GetComponent would throw on a projectile whose
    // weapon has already been despawned - which is exactly when a kill is being
    // reported.
    static string Interpret(object value)
    {
        if (value is UnityEngine.Object unityObject && unityObject == null) return null;

        switch (value)
        {
            case null: return null;
            case string text: return Clean(text);
            case ItemBehaviour itemBehaviour: return Clean(itemBehaviour.weaponName);
            case GameObject gameObject: return Clean(gameObject.GetComponent<ItemBehaviour>()?.weaponName);
            case Component component: return Clean(component.GetComponent<ItemBehaviour>()?.weaponName);
            default: return null;
        }
    }

    // The same reading, minus the bare string, for the pass that walks fields
    // whose names mean nothing. A string reached that way is as likely to be a
    // mesh or prefab name as a weapon name - this is what made the Blister and
    // Cyst acid report "Cube.016" - so only a real ItemBehaviour counts there.
    // A string is trustworthy in the NameFields pass because the field it came
    // from is literally called weaponName.
    static string InterpretUnnamedField(object value) => value is string ? null : Interpret(value);

    // One line per emitter type per session. A name resolved from an unexpected
    // field is what this catches: "Cube.016" looked like a weapon name in the
    // kill feed and nothing in the message said which field produced it.
    static readonly HashSet<Type> Diagnosed = new HashSet<Type>();

    static void Diagnose(object emitter, string name, List<string> trail)
    {
        Type type = emitter.GetType();
        if (!Diagnosed.Add(type)) return;

        Plugin.BepinLogger.LogInfo(name != null
            ? $"[KillDetect] {type.Name} named \"{name}\" via {string.Join(" -> ", trail.ToArray())}"
            : $"[KillDetect] {type.Name} carries no weapon name.");
    }

    // Used when no field yielded a name: an unset inspector field, or Obus and
    // Bubble, which hardcode their names in the game's own log strings and have
    // no name data at all to read.
    static string Fallback(object emitter)
    {
        // The only fallback that needs the instance rather than just the type,
        // so it cannot live in the type-keyed table below.
        if (emitter is PhysicsProp prop) return Clean(prop.popupText?.ToLower()) ?? "physics prop";

        return FallbackName(emitter.GetType()) ?? emitter.GetType().Name;
    }

    // Keyed by type rather than written as an `is` chain because SuicideSource,
    // over in suicideDetectPatches, has to look a name up from a type it decided
    // was a weapon rather than from an instance it can pattern match.
    static readonly Dictionary<Type, string> FallbackNames = new Dictionary<Type, string>
    {
        { typeof(Obus), "grenade launcher" },
        { typeof(Bubble), "the Bublee" },
        { typeof(Claymore), "claymore" },
        { typeof(ProximityMine), "proximity mine" },
        { typeof(PhysicsGrenade), "grenade" },
        { typeof(ShrapnelBallistic), "shrapnel" },
        { typeof(PredictedProjectile), "projectile" },
        { typeof(PhysicsProp), "physics prop" },
        { typeof(HandGrenade), "hand grenade" },
        { typeof(HandGrenadeTwo), "hand grenade" }
    };

    // Dictionary lookups are exact, so walk the base chain to keep the subclass
    // matching the old `case Obus _:` chain had.
    internal static string FallbackName(Type emitterType)
    {
        for (Type type = emitterType; type != null; type = type.BaseType)
        {
            if (FallbackNames.TryGetValue(type, out string name)) return name;
        }

        return null;
    }

    // Empty and whitespace-only names are as useless as null, and unset
    // inspector fields produce them, so collapse both so the caller falls
    // through to the next candidate.
    static string Clean(string name) => string.IsNullOrWhiteSpace(name) ? null : name;
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

Both live here rather than in suicideDetectPatches because the scope they need
spans a kill and a self-kill at once: one blast can do both, and the suicide side
only reads what this sets.
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
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[KillDetect] HandGrenade kill postfix failed: {error}");
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

/// <summary>
/// Debug output for kill detection: the in-game kill feed, plus LogOutput.log so
/// lines survive the session and can be diffed between host and client. Shared
/// with suicideDetectPatches, which writes the self-kill lines.
/// </summary>
internal static class KillFeed
{
    // Every emitter reports on the killer's own machine, so the local player is
    // always the subject of the line.
    static string LocalPlayerName => ClientInstance.Instance?.PlayerName ?? "Player";

    internal static void WriteKill(string weaponName) =>
        Write("KillDetect", $"{LocalPlayerName} got a kill with {weaponName}");

    // weaponName is null when nothing identifiable killed the player - falling
    // out of the map, a map hazard - so the line drops the clause rather than
    // naming something it does not know.
    //
    // Tagged SuicideDetect even though two of the three callers are in this
    // file: the tag says which half of the feature a line came from, and a
    // self-kill is the suicide half wherever it was noticed.
    internal static void WriteSelfKill(string weaponName = null) =>
        Write("SuicideDetect", string.IsNullOrWhiteSpace(weaponName)
            ? $"{LocalPlayerName} killed themselves, no credit for kill"
            : $"{LocalPlayerName} killed themselves with {weaponName}, no credit for kill");

    // The tag prefixes the console line only. The in-game feed gets the bare
    // message, which is what the player actually reads.
    internal static void Write(string tag, string message)
    {
        // Deliberately not ArchipelagoConsole.LogMessage - it forwards to
        // BepinLogger itself, which would double-print to the console, and its
        // overlay would duplicate the kill feed on screen.
        Plugin.BepinLogger.LogInfo($"[{tag}] {message}");

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
        catch (Exception error)
        {
            // The console line above already landed; a missing or half-built
            // feed must never take down the kill path.
            Plugin.BepinLogger.LogError($"[KillDetect] Could not write to the kill feed: {error}");
        }
    }
}
