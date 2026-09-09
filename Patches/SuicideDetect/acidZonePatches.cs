using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Utils;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

/// <summary>
/// Maps the lingering damage volume a weapon leaves behind back to that weapon. The pool ticks
/// damage and a later tick kills, so by then nothing about the weapon is on the call stack -
/// but the pool that killed the player is the one they are standing in. Each weapon's volume
/// prefab is recorded as the weapon is built, and looked up by name at death.
/// </summary>
internal static class AcidZones
{
    // Keyed by prefab name, which is what survives into the instance: a spawned "DF_Cyst_Zone"
    // is named "DF_Cyst_Zone(Clone)".
    static readonly Dictionary<string, string> Volumes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    static readonly HashSet<string> Registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static readonly Assembly Game = typeof(Settings).Assembly;

    /// <summary>Records what a weapon leaves behind. Once per weapon name.</summary>
    internal static void Register(ItemBehaviour item)
    {
        string weapon = item == null ? null : item.weaponName;
        if (string.IsNullOrWhiteSpace(weapon) || !Registered.Add(weapon)) return;

        // Gun -> projectile -> volume is the whole chain, so two hops is all this walks.
        // Reflection is fine here: it runs once per weapon name.
        foreach (Component gun in item.GetComponents<Component>())
        {
            foreach (Component projectile in Referenced<Component>(gun, false))
            {
                foreach (GameObject volume in Referenced<GameObject>(projectile, true))
                {
                    // A damage volume has a collider. A muzzle flash or an impact decal does
                    // not, and mapping one of those would let standing near a cosmetic effect
                    // name a death.
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

        // A volume that kills the player is one they are inside, so the query only has to reach
        // as far as their own body.
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

    // Only the game's own classes, so the walk never wanders into Unity or FishNet. `hinted`
    // narrows the second hop to the spawn field, without which every impact decal on a
    // projectile would be considered.
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
                    // An unassigned prefab reference can throw rather than return null; either
                    // way there is nothing to record.
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
/// Records each weapon's damage volume as the weapon is built. ItemBehaviour runs Start for
/// every item, which is the earliest point one exists as a real object with its inspector
/// references filled in.
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
