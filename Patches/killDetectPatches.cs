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
    and there are emitters here that no list in this file mentions - whatever
    spawns the Blister and Cyst acid pools reaches the suicide counter but is not
    one of the sixteen.
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

    // Keyed by type rather than written as an `is` chain because SuicideSource
    // has to look a name up from a type it decided was a weapon, not from an
    // instance it can pattern match.
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

            // Null for a death with no weapon behind it - falling out of the
            // map, a map hazard - which is the plain message's job.
            KillFeed.WriteSelfKill(SuicideSource.Resolve());
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[KillDetect] Suicide postfix failed: {error}");
        }
    }
}

/// <summary>
/// Names the weapon behind a suicide. IncreaseSuicidesAmount() takes no
/// arguments and is a plain counter, so nothing about the weapon reaches it -
/// but the object whose code is calling it is the weapon, and
/// <see cref="SuicideSourcePatch"/> records that object on the way in.
/// </summary>
internal static class SuicideSource
{
    internal static readonly InstanceScope Emitting = new InstanceScope();

    /// <summary>Weapon name for the suicide being reported, or null if none is identifiable.</summary>
    internal static string Resolve()
    {
        // HandGrenade is already resolved from the live instance by the scope
        // around its explosion, so that name is the better one when it is open.
        string scopedName = HandGrenadeScope.ActiveWeapon;
        if (scopedName != null) return scopedName;

        // An exact name off the instance beats the generic one below it.
        string exact = FromEmitters(allowGenericName: false);
        if (exact != null) return exact;

        // Then a damage volume the player is standing in, which is the only
        // thing that names a weapon that killed on a delay - see AcidZones.
        string standingIn = StandingIn();
        if (standingIn != null) return standingIn;

        // Then the generic per-type name, which is the same word for every
        // weapon that shares an emitter class and so is the weakest answer here.
        string generic = FromEmitters(allowGenericName: true);
        if (generic != null) return generic;

        ReportUnattributed();
        return null;
    }

    static string FromEmitters(bool allowGenericName)
    {
        for (int index = Emitting.Count - 1; index >= 0; index--)
        {
            string name = NameOf(Emitting[index], allowGenericName);
            if (name != null) return name;
        }

        return null;
    }

    static string NameOf(object emitter, bool allowGenericName)
    {
        if (emitter == null) return null;

        string name = KillDetectPatch.ResolveWeaponName(emitter, allowGenericName: false);
        if (name != null || !allowGenericName) return name;

        // Nothing on the instance named it. A generic name is still right for
        // the sixteen known emitters, but not for whatever else the IL search
        // turned up - the counter is reached from FirstPersonController for an
        // acid death, and that would otherwise announce
        // "killed themselves with FirstPersonController".
        // ResolveWeaponName has already dumped this type's fields, so a suicide
        // that lands here is diagnosable from the log without adding anything.
        Type known = KnownEmitter(emitter.GetType());
        return known != null ? KillDetectPatch.FallbackName(known) : null;
    }

    // Suicides are rare, so this logs every time rather than once per type: the
    // useful part is what was on the stack and what the damage tracker held at
    // that moment, and both change from death to death.
    static void ReportUnattributed()
    {
        List<string> emitters = new List<string>();
        for (int index = Emitting.Count - 1; index >= 0; index--)
        {
            emitters.Add(Emitting[index]?.GetType().Name ?? "null");
        }

        string stack = emitters.Count > 0 ? string.Join(" <- ", emitters.ToArray()) : "none";
        Plugin.BepinLogger.LogInfo($"[KillDetect] Suicide not attributed. Emitters: {stack}.");
    }

    // The suicide is reported on the dying player's own machine, so whatever
    // reported it is a component on the player - FirstPersonController, for an
    // acid death - and its transform is where the player is standing.
    static string StandingIn()
    {
        for (int index = Emitting.Count - 1; index >= 0; index--)
        {
            if (Emitting[index] is Component component && component != null)
            {
                return AcidZones.At(component.transform.position);
            }
        }

        return null;
    }

    // Held weapons are in the set because ChargeGun and RepulsiveGun can shove
    // you into your own death; the rest of them simply never show up.
    static readonly HashSet<Type> Emitters = BuildEmitters();

    static HashSet<Type> BuildEmitters()
    {
        HashSet<Type> emitters = new HashSet<Type>(KillDetectPatch.HeldWeapons);
        foreach (Type detached in KillDetectPatch.DetachedWeapons) emitters.Add(detached);
        emitters.Add(typeof(HandGrenade));
        emitters.Add(typeof(HandGrenadeTwo));
        return emitters;
    }

    // The recorded instance is not always the emitter itself: a self-branch
    // inside a coroutine runs from a compiler generated type nested in it, and a
    // weapon may be a subclass. Walk both chains so either shape still matches.
    static Type KnownEmitter(Type instanceType)
    {
        for (Type nesting = instanceType; nesting != null; nesting = nesting.DeclaringType)
        {
            for (Type type = nesting; type != null; type = type.BaseType)
            {
                if (Emitters.Contains(type)) return type;
            }
        }

        return null;
    }

}

/// <summary>The objects whose code is currently running, innermost last.</summary>
internal sealed class InstanceScope
{
    // A stack, not a slot: one object's method can call into another's, and the
    // innermost is the one that acted.
    readonly List<object> active = new List<object>();

    // A desync can only come from a prefix that ran without its finalizer, which
    // should be impossible - but an unbounded list pushed to every frame by some
    // Update() would be a slow leak, so cap it.
    const int MaxDepth = 32;

    internal int Count => active.Count;

    internal object this[int index] => active[index];

    internal object Innermost => active.Count > 0 ? active[active.Count - 1] : null;

    internal void Enter(object instance)
    {
        if (active.Count >= MaxDepth) active.Clear();

        active.Add(instance);
    }

    internal void Exit()
    {
        if (active.Count > 0) active.RemoveAt(active.Count - 1);
    }
}

/*
Finds the methods that call a given method, by reading IL rather than by listing
names.

Two reasons this is a search and not a list. The self-branches live in
thirteen-plus differently named methods, each one a rename away from silently
reporting nothing, which is the failure mode this file already goes out of its
way to avoid. And more importantly a name list can only contain emitters that
are known: the suicide counter is reached from FirstPersonController for an acid
death, and the acid itself is a type no list in this file mentions.

The scan is a raw byte search for a call opcode followed by the callee's
metadata token, not a real IL walk, so an operand can masquerade as an opcode. A
false positive costs one extra patched method whose instance is pushed and
popped and then fails to resolve a name - harmless, and much cheaper than
parsing every method body in the assembly at startup.
*/
internal static class CallerSearch
{
    internal const BindingFlags Declared =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    const byte Call = 0x28;
    const byte CallVirt = 0x6F;

    internal static List<MethodBase> Of(ICollection<MethodBase> callees, string description)
    {
        List<MethodBase> callers = new List<MethodBase>();
        if (callees == null || callees.Count == 0) return callers;

        HashSet<int> tokens = new HashSet<int>();
        HashSet<Type> owners = new HashSet<Type>();
        foreach (MethodBase callee in callees)
        {
            tokens.Add(callee.MetadataToken);
            if (callee.DeclaringType != null) owners.Add(callee.DeclaringType);
        }

        foreach (Type type in GameTypes())
        {
            try
            {
                // A callee's own class calls itself internally, and a generic
                // definition has no callable method to patch.
                if (owners.Contains(type) || type.ContainsGenericParameters) continue;

                foreach (MethodInfo method in type.GetMethods(Declared))
                {
                    // A static method has no instance to record, which is the
                    // only thing these scopes exist to capture.
                    if (method.IsStatic || method.IsAbstract || method.ContainsGenericParameters) continue;
                    if (Calls(method, tokens)) callers.Add(method);
                }
            }
            catch (Exception error)
            {
                Plugin.BepinLogger.LogError($"[KillDetect] Could not scan {type.FullName} for callers: {error}");
            }
        }

        Plugin.BepinLogger.LogInfo($"[KillDetect] Found {callers.Count} caller(s) of {description}.");
        return callers;
    }

    // A half-loadable assembly still yields the types that did load, and the
    // emitters are plain MonoBehaviours that will be among them.
    static IEnumerable<Type> GameTypes()
    {
        try
        {
            return typeof(Settings).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            Plugin.BepinLogger.LogWarning($"[KillDetect] Some game types failed to load while scanning: {error.Message}");
            return Array.FindAll(error.Types, type => type != null);
        }
    }

    static bool Calls(MethodInfo method, HashSet<int> tokens)
    {
        byte[] instructions;
        try
        {
            instructions = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch
        {
            // Abstract, extern and runtime-provided methods have no body to read.
            return false;
        }

        if (instructions == null) return false;

        for (int index = 0; index + 4 < instructions.Length; index++)
        {
            if (instructions[index] != Call && instructions[index] != CallVirt) continue;
            if (tokens.Contains(BitConverter.ToInt32(instructions, index + 1))) return true;
        }

        return false;
    }
}

/// <summary>
/// Maps the lingering damage volume a weapon leaves behind back to that weapon.
/// </summary>
/*
Every link in this chain is a field, and every one has been read off a live
weapon: DualLauncher, whose ItemBehaviour.weaponName is "Cyst", holds
PredictedProjectile _projectile "CystBullet", which holds GameObject objToSpawn
"DF_Cyst_Zone" - the acid pool. The pool ticks damage and a later tick kills, so
by then nothing about the weapon is on the call stack and FirstPersonController
credits a plain suicide. The game does not know either: its own match log prints
"commited suicide" with the weapon slot left unfilled.

What is knowable is that the pool which killed you is the one you are standing
in. So each weapon's volume prefab is recorded when the weapon is built, and at
death the volumes actually touching the player are looked up by name. That is
also what separates the Cyst from the Blister: same gun class, same projectile
class, different volume prefab.
*/
internal static class AcidZones
{
    // Keyed by prefab name, which is what survives into the instance: a spawned
    // "DF_Cyst_Zone" is named "DF_Cyst_Zone(Clone)".
    static readonly Dictionary<string, string> Volumes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    static readonly HashSet<string> Registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static readonly Assembly Game = typeof(Settings).Assembly;

    /// <summary>Records what a weapon leaves behind. Once per weapon name.</summary>
    internal static void Register(ItemBehaviour item)
    {
        string weapon = item == null ? null : item.weaponName;
        if (string.IsNullOrWhiteSpace(weapon) || !Registered.Add(weapon)) return;

        // Gun -> projectile -> volume is the whole chain, so two hops is all
        // this walks. Reflection is fine here: it runs once per weapon name.
        foreach (Component gun in item.GetComponents<Component>())
        {
            foreach (Component projectile in Referenced<Component>(gun, false))
            {
                foreach (GameObject volume in Referenced<GameObject>(projectile, true))
                {
                    // A damage volume has a collider. A muzzle flash or an
                    // impact decal does not, and mapping one of those would let
                    // standing near a cosmetic effect name a death.
                    if (volume.GetComponentInChildren<Collider>() == null) continue;
                    if (Volumes.ContainsKey(volume.name)) continue;

                    Volumes[volume.name] = weapon;
                    Plugin.BepinLogger.LogInfo($"[KillDetect] \"{weapon}\" leaves {volume.name} behind.");
                }
            }
        }
    }

    /// <summary>Weapon whose volume is touching this point, or null.</summary>
    internal static string At(Vector3 position)
    {
        if (Volumes.Count == 0) return null;

        // A volume that kills you is one you are inside, so the query only has
        // to reach as far as the player's own body.
        foreach (Collider touching in Physics.OverlapSphere(position, 1f, ~0, QueryTriggerInteraction.Collide))
        {
            // The collider can sit on a child of the spawned volume, so walk up.
            for (Transform part = touching == null ? null : touching.transform; part != null; part = part.parent)
            {
                if (Volumes.TryGetValue(Prefab(part.name), out string weapon)) return weapon;
            }
        }

        return null;
    }

    // Only the game's own classes, so the walk never wanders into Unity or
    // FishNet. `hinted` narrows the second hop to the spawn field: without it
    // every impact decal on a projectile would be considered.
    static List<T> Referenced<T>(Component owner, bool hinted) where T : UnityEngine.Object
    {
        List<T> found = new List<T>();
        if (owner == null || owner.GetType().Assembly != Game) return found;

        for (Type type = owner.GetType(); type != null && type.Assembly == Game; type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(CallerSearch.Declared))
            {
                if (!typeof(T).IsAssignableFrom(field.FieldType)) continue;
                if (hinted && field.Name.IndexOf("spawn", StringComparison.OrdinalIgnoreCase) < 0) continue;

                T value = null;
                try
                {
                    value = field.GetValue(owner) as T;
                }
                catch
                {
                    // An unassigned prefab reference can throw rather than
                    // return null; either way there is nothing to record.
                }

                if (value != null) found.Add(value);
            }
        }

        return found;
    }

    static string Prefab(string name)
    {
        int clone = name.IndexOf("(Clone)", StringComparison.Ordinal);
        return clone < 0 ? name : name.Substring(0, clone).TrimEnd();
    }
}

/// <summary>
/// Records each weapon's damage volume as the weapon is built. ItemBehaviour
/// runs Start for every item, which is the earliest point one exists as a real
/// object with its inspector references filled in.
/// </summary>
[HarmonyPatch(typeof(ItemBehaviour), "Start")]
public class AcidZoneRegisterPatch
{
    static void Postfix(ItemBehaviour __instance)
    {
        try
        {
            AcidZones.Register(__instance);
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[KillDetect] Could not register {__instance?.weaponName}'s volume: {error}");
        }
    }
}

/// <summary>
/// The scope hooks themselves. Plain static methods rather than a
/// [HarmonyPatch] class because their targets are discovered at runtime and
/// installed one at a time - see <see cref="KillDetectScopes"/>.
/// </summary>
internal static class ScopeHooks
{
    internal static void EnterSuicideSource(object __instance) => SuicideSource.Emitting.Enter(__instance);

    internal static void ExitSuicideSource() => SuicideSource.Emitting.Exit();
}

/// <summary>
/// Installs the emitter scope. Called from Plugin after PatchAll rather than by
/// it: these targets come from an IL search and there are dozens of them, so
/// each is patched inside its own try/catch instead of letting one unpatchable
/// method abort the mod's whole initialization.
/// </summary>
internal static class KillDetectScopes
{
    internal static void Install(Harmony harmony)
    {
        MethodInfo counter = AccessTools.Method(typeof(Settings), "IncreaseSuicidesAmount");
        if (counter == null)
        {
            Plugin.BepinLogger.LogError(
                "[KillDetect] Settings.IncreaseSuicidesAmount not found - suicides will not name a weapon.");
            return;
        }

        Scope(harmony,
            CallerSearch.Of(new MethodBase[] { counter }, "the suicide counter"),
            nameof(ScopeHooks.EnterSuicideSource),
            nameof(ScopeHooks.ExitSuicideSource));
    }

    // Prefix and finalizer, not prefix and postfix: a finalizer runs on the
    // exception path too, so a throwing explosion cannot leave an instance on
    // the scope stack forever.
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
                    $"[KillDetect] Could not scope {target.DeclaringType?.Name}.{target.Name}: {error}");
            }
        }

        Plugin.BepinLogger.LogInfo($"[KillDetect] Scoped {patched} of {targets.Count} target(s) for {enter}.");
    }

    static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(ScopeHooks), name);
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
/// lines survive the session and can be diffed between host and client.
/// </summary>
internal static class KillFeed
{
    // Every emitter reports on the killer's own machine, so the local player is
    // always the subject of the line.
    static string LocalPlayerName => ClientInstance.Instance?.PlayerName ?? "Player";

    internal static void WriteKill(string weaponName) =>
        Write($"{LocalPlayerName} got a kill with {weaponName}");

    // weaponName is null when nothing identifiable killed the player - falling
    // out of the map, a map hazard - so the line drops the clause rather than
    // naming something it does not know.
    internal static void WriteSelfKill(string weaponName = null) =>
        Write(string.IsNullOrWhiteSpace(weaponName)
            ? $"{LocalPlayerName} killed themselves, no credit for kill"
            : $"{LocalPlayerName} killed themselves with {weaponName}, no credit for kill");

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
        catch (Exception error)
        {
            // The console line above already landed; a missing or half-built
            // feed must never take down the kill path.
            Plugin.BepinLogger.LogError($"[KillDetect] Could not write to the kill feed: {error}");
        }
    }
}
