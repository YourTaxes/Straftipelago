using System;
using System.Collections.Generic;
using Straftapelago.Finnegan_McD.org.Patches;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// Names the weapon behind a suicide, working from the strongest evidence to the weakest.
/// <see cref="SuicideScopes"/> supplies the emitter instances.
/// </summary>
internal static class SuicideSource
{
    internal static readonly InstanceScope Emitting = new InstanceScope();

    /// <summary>Weapon name for the suicide being reported, or null if none is identifiable.</summary>
    internal static string Resolve()
    {
        // A hand grenade is already resolved from the live instance by the scope around its
        // explosion, so that name is the better one when it is open.
        string scopedName = HandGrenadeScope.ActiveWeapon;
        if (scopedName != null) return scopedName;

        // An exact name off the instance beats the generic one below it.
        string exact = FromEmitters(allowGenericName: false);
        if (exact != null) return exact;

        // Then a damage volume the player is standing in, which is the only thing that names a
        // weapon that killed on a delay.
        string standingIn = StandingIn();
        if (standingIn != null) return standingIn;

        // Then the generic per-type name, which is the same word for every weapon that shares
        // an emitter class and so is the weakest answer here.
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

        // Nothing on the instance named it. A generic name is right for the sixteen known
        // emitters, but not for whatever else the IL search turned up: the counter is reached
        // from FirstPersonController for an acid death.
        Type known = KnownEmitter(emitter.GetType());
        return known != null ? KillDetectPatch.FallbackName(known) : null;
    }

    // The suicide is reported on the dying player's own machine, so whatever reported it is a
    // component on the player and its transform is where the player is standing.
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

    // Suicides are rare, so this logs every time rather than once per type: what was on the
    // stack is the useful part, and it changes from death to death.
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

    // Held weapons are in the set because ChargeGun and RepulsiveGun can shove the player into
    // their own death; the rest of them simply never show up.
    static readonly HashSet<Type> Emitters = BuildEmitters();

    static HashSet<Type> BuildEmitters()
    {
        HashSet<Type> emitters = new HashSet<Type>(KillDetectPatch.HeldWeapons);
        foreach (Type detached in KillDetectPatch.DetachedWeapons) emitters.Add(detached);
        emitters.Add(typeof(HandGrenade));
        emitters.Add(typeof(HandGrenadeTwo));
        return emitters;
    }

    // The recorded instance is not always the emitter itself: a self-branch inside a coroutine
    // runs from a compiler generated type nested in it, and a weapon may be a subclass. Walk
    // both chains so either shape still matches.
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
    // A stack, not a slot: one object's method can call into another's, and the innermost is
    // the one that acted.
    readonly List<object> active = new List<object>();

    // A desync can only come from a prefix that ran without its finalizer, but an unbounded
    // list pushed to every frame by some Update would be a slow leak, so cap it.
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
