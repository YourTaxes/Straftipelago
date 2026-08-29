using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

/*
Suicide detection. The other half of killDetectPatches, which handles kills and
owns the weapon-name resolution both files share.

A suicide is harder to name than a kill for one reason: the signal carries no
weapon. Settings.IncreaseSuicidesAmount() is an argument-less counter, so unlike
the kill emitters - which all expose a victim-carrying method this mod can read
__instance from - there is nothing in the call itself to identify what did it.

Three mechanisms cover the three shapes a suicide comes in, cheapest first:

  1. The emitter is on the call stack. Most weapons route their own self-hits to
     the counter from inside their explosion loop, so the object calling it IS
     the weapon. SuicideScopes patches every caller of the counter to record its
     instance, and SuicideSource reads it back.

  2. The emitter is on the call stack but shares its class with other weapons.
     PredictedProjectile is both a rocket launcher and a Mortini, so the type
     names nothing; the instance is what holds the gun that fired it. That is
     KillDetectPatch.ResolveWeaponName's job, and only when it comes up empty
     does the generic per-type name in FallbackNames get used.

  3. Nothing is on the call stack at all. The Blister and Cyst leave an acid pool
     that ticks damage; a later tick kills, and by then FirstPersonController is
     what notices the death. AcidZones covers that one - see the comment above it.

Environmental deaths - falling out of the map, map hazards - reach none of the
three, which is correct: they have no weapon, and the message drops the clause.
*/

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
            Plugin.BepinLogger.LogError($"[SuicideDetect] Suicide postfix failed: {error}");
        }
    }
}

/// <summary>
/// Names the weapon behind a suicide, working from the strongest evidence to the
/// weakest. <see cref="SuicideScopes"/> supplies the emitter instances.
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
        Type known = KnownEmitter(emitter.GetType());
        return known != null ? KillDetectPatch.FallbackName(known) : null;
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

    // Suicides are rare, so this logs every time rather than once per type: what
    // was on the stack is the useful part, and it changes from death to death.
    static void ReportUnattributed()
    {
        List<string> emitters = new List<string>();
        for (int index = Emitting.Count - 1; index >= 0; index--)
        {
            emitters.Add(Emitting[index]?.GetType().Name ?? "null");
        }

        string stack = emitters.Count > 0 ? string.Join(" <- ", emitters.ToArray()) : "none";
        Plugin.BepinLogger.LogInfo($"[SuicideDetect] Suicide not attributed. Emitters: {stack}.");
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
reporting nothing, which is the failure mode these files go out of their way to
avoid. And more importantly a name list can only contain emitters that are
known: the suicide counter is reached from FirstPersonController for an acid
death, a type no list here mentions.

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
                Plugin.BepinLogger.LogError($"[SuicideDetect] Could not scan {type.FullName} for callers: {error}");
            }
        }

        Plugin.BepinLogger.LogInfo($"[SuicideDetect] Found {callers.Count} caller(s) of {description}.");
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
            Plugin.BepinLogger.LogWarning($"[SuicideDetect] Some game types failed to load while scanning: {error.Message}");
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
                    Plugin.BepinLogger.LogInfo($"[SuicideDetect] \"{weapon}\" leaves {volume.name} behind.");
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
            Plugin.BepinLogger.LogError($"[SuicideDetect] Could not register {__instance?.weaponName}'s volume: {error}");
        }
    }
}

/// <summary>
/// The scope hooks themselves. Plain static methods rather than a
/// [HarmonyPatch] class because their targets are discovered at runtime and
/// installed one at a time - see <see cref="SuicideScopes"/>.
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
                    $"[SuicideDetect] Could not scope {target.DeclaringType?.Name}.{target.Name}: {error}");
            }
        }

        Plugin.BepinLogger.LogInfo($"[SuicideDetect] Scoped {patched} of {targets.Count} target(s) for {enter}.");
    }

    static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(ScopeHooks), name);
}
