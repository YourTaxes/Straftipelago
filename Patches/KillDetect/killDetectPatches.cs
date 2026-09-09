using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

/// <summary>
/// Central kill detection. Every kill emitter exposes a victim-carrying method and every one
/// names the parameter <c>enemyHealth</c>, so a single TargetMethods patch spans all sixteen
/// with uniform Harmony name injection. Suicides live in suicideDetectPatches, which reuses
/// this class's name resolution and <see cref="KillFeed"/>.
/// </summary>
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

    // Detached killers - thrown, placed or fired things that are not in the player's hand at
    // kill time. No common base below MonoBehaviour, but they all kill through
    // SendKillLog(PlayerHealth enemyHealth).
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

    // A null target makes Harmony throw and takes the whole PatchAll down with it, so a method
    // renamed by a future game build is reported and skipped instead.
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

    // Postfix, not prefix, so a throw here cannot suppress vanilla's kill handling.
    // __instance is typed as object because the 16 targets share no base type below
    // MonoBehaviour.
    static void Postfix(object __instance, PlayerHealth enemyHealth)
    {
        try
        {
            if (enemyHealth == null) return;

            // PhysicsProp's kill branch has no ownership check at all, unlike every other
            // emitter, so without this a non-owning peer could report someone else's prop kill.
            if (__instance is PhysicsProp prop && !prop.IsOwner) return;

            // Resolved once and used twice: the pool name is what RouletteState matches on, the
            // decorated one is what the player reads.
            string poolName = ResolvePoolName(__instance);

            // True only when the victim is the local player. Vanilla routes self-hits away from
            // these methods for 14 of the 16 emitters; Bubble and PhysicsProp do not. A
            // self-kill earns no Archipelago check.
            if (enemyHealth.IsOwner)
            {
                KillFeed.WriteSelfKill(Decorate(__instance, poolName, true));
                return;
            }

            KillFeed.WriteKill(Decorate(__instance, poolName, true));
            CreditKill(poolName);
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[KillDetect] Postfix failed for {__instance?.GetType().Name}: {error}");
        }
    }

    /// <summary>
    /// Tells the roulette pools a kill happened with this weapon. The first kill with an
    /// unlocked weapon earns an Archipelago check; every later one is a no-op inside RecordKill.
    /// Called from the kill paths only, never from a self-kill.
    /// </summary>
    internal static void CreditKill(string poolWeaponName)
    {
        try
        {
            if (poolWeaponName == null) return;
            Plugin.RouletteState?.RecordKill(poolWeaponName);
        }
        catch (Exception error)
        {
            // The kill itself is vanilla's business and has already happened.
            Plugin.BepinLogger.LogError($"[KillDetect] Could not credit a kill with '{poolWeaponName}': {error}");
        }
    }

    // Field names and types are inconsistent across the emitters, so each candidate is tried in
    // turn and whatever comes back is interpreted.
    //
    // Order matters, and the references come first: a projectile's own weaponName describes the
    // projectile, not the weapon that fired it, while the ItemBehaviour is what the game itself
    // displays.
    static readonly string[] NameFields = { "behaviour", "behavior", "weapon", "_gun", "weaponName" };

    /// <param name="allowGenericName">
    /// False for suicides, where the emitter is discovered rather than handed over: a generic
    /// name would be wrong for anything the search turned up that is not a weapon at all.
    /// </param>
    internal static string ResolveWeaponName(object emitter, bool allowGenericName = true)
    {
        if (emitter == null) return allowGenericName ? "something" : null;

        return Decorate(emitter, ResolvePoolName(emitter), allowGenericName);
    }

    /// <summary>
    /// The undecorated name, exactly as the game itself carries it, or null when the emitter
    /// has none. This is what RouletteState matches its pools against, so it is kept separate
    /// from <see cref="ResolveWeaponName"/>, whose decoration would break that match.
    /// </summary>
    internal static string ResolvePoolName(object emitter)
    {
        if (emitter == null) return null;

        List<string> trail = new List<string>();
        string name = ScanForName(emitter, 0, trail);

        Diagnose(emitter, name, trail);
        return name;
    }

    static string Decorate(object emitter, string name, bool allowGenericName)
    {
        if (name != null)
        {
            // Obus and Bubble are both spawned by the same DualLauncher and hold it in _gun, so
            // the resolved name is identical for both and only the projectile type tells them
            // apart.
            return emitter is Bubble ? $"{name} (Bublee)" : name;
        }

        return allowGenericName ? Fallback(emitter) : null;
    }

    /// <summary>
    /// Walks an emitter for the name of the weapon behind it: the known field spellings first,
    /// then any field holding an ItemBehaviour, then one hop through the references that
    /// plausibly lead to the gun. One class serves many weapons, so only the instance can name
    /// the weapon.
    /// </summary>
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

        // Then one hop through the references that plausibly lead to the gun. A coroutine's
        // `<>4__this` is why "this" is in the hint list: when the self-branch lives in an
        // iterator, the recorded instance is the state machine and the weapon is a field on it.
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

    // Two hops is emitter -> gun -> ItemBehaviour, which is as deep as any known shape goes.
    const int MaxScanDepth = 2;

    // Deliberately narrow. Following an owner or a root object would reach the player and then
    // whatever they happen to be holding, which is a plausible looking but wrong name.
    static readonly string[] SourceHints = { "gun", "weapon", "item", "launcher", "this" };

    static bool IsSourceHint(string fieldName)
    {
        foreach (string hint in SourceHints)
        {
            if (fieldName.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }

        return false;
    }

    // Only the game's own types, which includes compiler generated state machine types. Unity
    // and BCL types lead into the scene graph and never to a name.
    static readonly Assembly GameAssembly = typeof(Settings).Assembly;

    static bool IsGameObjectWorthFollowing(object value)
    {
        if (value == null) return false;
        if (value is UnityEngine.Object unityObject && unityObject == null) return false;

        return value.GetType().Assembly == GameAssembly;
    }

    // Cached: an unknown emitter is walked field by field, and a kill can land in the middle of
    // a firefight.
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

    // Turns whatever a candidate field held into a weapon name, or null if it held nothing
    // useful. The explicit `unityObject == null` test matters: a destroyed UnityEngine.Object
    // still matches a type pattern, so without it GetComponent would throw on a projectile
    // whose weapon has already been despawned.
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

    // The same reading, minus the bare string, for the pass that walks fields whose names mean
    // nothing: a string reached that way is as likely to be a mesh or prefab name as a weapon
    // name. A string is trustworthy in the NameFields pass because that field is called
    // weaponName.
    static string InterpretUnnamedField(object value) => value is string ? null : Interpret(value);

    // One line per emitter type per session, naming the field the name came out of.
    static readonly HashSet<Type> Diagnosed = new HashSet<Type>();

    static void Diagnose(object emitter, string name, List<string> trail)
    {
        Type type = emitter.GetType();
        if (!Diagnosed.Add(type)) return;

        Plugin.BepinLogger.LogInfo(name != null
            ? $"[KillDetect] {type.Name} named \"{name}\" via {string.Join(" -> ", trail.ToArray())}"
            : $"[KillDetect] {type.Name} carries no weapon name.");
    }

    // Used when no field yielded a name: an unset inspector field, or Obus and Bubble, which
    // hardcode their names in the game's own log strings and have no name data to read.
    static string Fallback(object emitter)
    {
        // The only fallback that needs the instance rather than just the type.
        if (emitter is PhysicsProp prop) return Clean(prop.popupText?.ToLower()) ?? "physics prop";

        return FallbackName(emitter.GetType()) ?? emitter.GetType().Name;
    }

    // Keyed by type rather than written as an `is` chain, because SuicideSource has to look a
    // name up from a type it decided was a weapon rather than from an instance.
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

    // Dictionary lookups are exact, so walk the base chain to keep subclasses matching.
    internal static string FallbackName(Type emitterType)
    {
        for (Type type = emitterType; type != null; type = type.BaseType)
        {
            if (FallbackNames.TryGetValue(type, out string name)) return name;
        }

        return null;
    }

    // Empty and whitespace-only names are as useless as null, and unset inspector fields
    // produce them, so both collapse and the caller falls through to the next candidate.
    static string Clean(string name) => string.IsNullOrWhiteSpace(name) ? null : name;
}

/// <summary>
/// The kill feed this mod writes to: the in-game feed, plus LogOutput.log so lines survive the
/// session and can be diffed between host and client. Shared with suicideDetectPatches, which
/// writes the self-kill lines.
/// </summary>
internal static class KillFeed
{
    // Every emitter reports on the killer's own machine, so the local player is always the
    // subject of the line.
    internal static string LocalPlayerName => ClientInstance.Instance?.PlayerName ?? "Player";

    internal static void WriteKill(string weaponName) =>
        Write("KillDetect", $"{LocalPlayerName} got a kill with {weaponName}");

    // weaponName is null when nothing identifiable killed the player - falling out of the map,
    // a map hazard - so the line drops the clause rather than naming something it does not know.
    // Tagged SuicideDetect wherever it was noticed, because a self-kill is that half of the
    // feature.
    internal static void WriteSelfKill(string weaponName = null) =>
        Write("SuicideDetect", string.IsNullOrWhiteSpace(weaponName)
            ? $"{LocalPlayerName} killed themselves, no credit for kill"
            : $"{LocalPlayerName} killed themselves with {weaponName}, no credit for kill");

    // The tag prefixes the console line only. The in-game feed gets the bare message.
    internal static void Write(string tag, string message)
    {
        // Deliberately not ArchipelagoConsole.LogMessage: that forwards to BepinLogger itself,
        // which would double-print, and its overlay would duplicate the kill feed on screen.
        Plugin.BepinLogger.LogInfo($"[{tag}] {message}");

        try
        {
            // WriteLocalLog instantiates a chat line locally with no network traffic, so this
            // stays on the killer's screen. MatchLogs is a NetworkBehaviour singleton and is
            // null in menus; MatchLogsOffline is the live one in singleplayer.
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
            // The console line above already landed; a missing or half-built feed must never
            // take down the kill path.
            Plugin.BepinLogger.LogError($"[KillDetect] Could not write to the kill feed: {error}");
        }
    }
}
