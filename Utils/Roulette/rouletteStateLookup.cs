using System;
using System.Collections.Generic;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

// Weapon names reach the pools from three different namespaces: the game's prefab names
// ("glock"), the names it displays ("Glock"), and the room's item and location names ("Sawed
// Off"). Each gets a lookup, and ResolveByAnyName tries them in that order.
public partial class RouletteState
{
    private Dictionary<string, GameObject> nameLookup;
    private Dictionary<string, GameObject> displayNameLookup;
    private Dictionary<string, GameObject> normalizedLookup;

    /// <summary>Case-insensitive lookup over the game's own name-to-prefab dictionary.</summary>
    public GameObject Lookup(string weaponName)
    {
        if (string.IsNullOrEmpty(weaponName)) return null;

        if (nameLookup == null)
        {
            Dictionary<string, GameObject> source = SpawnerManager.NameToWeaponDict;
            if (source == null) return null;

            nameLookup = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, GameObject> pair in source)
            {
                if (pair.Key != null && pair.Value != null) nameLookup[pair.Key] = pair.Value;
            }
        }

        return nameLookup.TryGetValue(weaponName, out GameObject weapon) ? weapon : null;
    }

    /// <summary>
    /// Lookup by the name the game displays (ItemBehaviour.weaponName) rather than by the
    /// prefab's GameObject.name. Kill detection and the floor item's popup both speak display
    /// names.
    /// </summary>
    public GameObject LookupByDisplayName(string displayName)
    {
        if (string.IsNullOrEmpty(displayName)) return null;

        if (displayNameLookup == null)
        {
            GameObject[] allWeapons = SpawnerManager.AllWeapons;
            if (allWeapons == null) return null;

            displayNameLookup = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            foreach (GameObject weapon in allWeapons)
            {
                if (weapon == null) continue;

                ItemBehaviour behaviour = weapon.GetComponent<ItemBehaviour>();
                if (behaviour == null || string.IsNullOrEmpty(behaviour.weaponName)) continue;

                // First one wins, and the collision is reported rather than hidden: two prefabs
                // sharing a display name would make kill credit ambiguous.
                if (displayNameLookup.TryGetValue(behaviour.weaponName, out GameObject existing))
                {
                    Plugin.BepinLogger.LogWarning(
                        $"[RouletteState] two weapon prefabs share the display name " +
                        $"'{behaviour.weaponName}' ({existing.name} and {weapon.name}); " +
                        $"keeping {existing.name}.");
                    continue;
                }

                displayNameLookup[behaviour.weaponName] = weapon;
            }
        }

        return displayNameLookup.TryGetValue(displayName, out GameObject match) ? match : null;
    }

    /// <summary>
    /// Resolves any name the mod might be handed - a prefab name, a display name, or an
    /// Archipelago item name - to the pool entry it belongs to. The two exact lookups come
    /// first, so an exact match is never lost to a fuzzy one.
    /// </summary>
    public GameObject ResolveByAnyName(string weaponName) =>
        Lookup(weaponName) ?? LookupByDisplayName(weaponName) ?? LookupByNormalizedName(weaponName);

    /// <summary>
    /// Lookup with case, spaces, hyphens and underscores all ignored, which is what makes the
    /// room's item names land: Archipelago writes "Dual Launcher" and "AAA-12" where the
    /// prefabs are "DualLauncher" and "AAA12". Built over both namespaces.
    /// </summary>
    public GameObject LookupByNormalizedName(string weaponName)
    {
        string key = Normalize(weaponName);
        if (string.IsNullOrEmpty(key)) return null;

        if (normalizedLookup == null)
        {
            GameObject[] allWeapons = SpawnerManager.AllWeapons;
            if (allWeapons == null) return null;

            normalizedLookup = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            foreach (GameObject weapon in allWeapons)
            {
                if (weapon == null) continue;

                // Prefab name first so that it wins a collision, matching the precedence
                // ResolveByAnyName uses between the two exact lookups.
                Add(weapon.name, weapon);
                Add(weapon.GetComponent<ItemBehaviour>()?.weaponName, weapon);
            }

            void Add(string name, GameObject weapon)
            {
                string normalized = Normalize(name);
                if (string.IsNullOrEmpty(normalized)) return;

                // Silently first-wins, unlike the display-name collision above: squashing
                // punctuation is expected to make a prefab and its own display name collide.
                if (!normalizedLookup.ContainsKey(normalized)) normalizedLookup[normalized] = weapon;
            }
        }

        return normalizedLookup.TryGetValue(key, out GameObject match) ? match : null;
    }

    /// <summary>Lowercases and strips everything that is not a letter or a digit.</summary>
    private static string Normalize(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        var normalized = new System.Text.StringBuilder(name.Length);
        foreach (char character in name)
        {
            if (char.IsLetterOrDigit(character)) normalized.Append(char.ToLowerInvariant(character));
        }

        return normalized.ToString();
    }

    /// <summary>
    /// Maps a live item in the world back to the prefab the pools hold, or null when it is not
    /// a pool weapon at all. Callers must treat null as "not restricted" rather than "locked".
    /// </summary>
    public GameObject ResolvePrefab(ItemBehaviour item)
    {
        if (item == null) return null;

        // Instantiated copies keep the prefab's name with "(Clone)" appended, so the prefab
        // name is the more precise of the two keys and is tried first.
        string objectName = item.gameObject.name;
        const string cloneSuffix = "(Clone)";
        if (objectName != null && objectName.EndsWith(cloneSuffix, StringComparison.Ordinal))
        {
            objectName = objectName.Substring(0, objectName.Length - cloneSuffix.Length).TrimEnd();
        }

        return Lookup(objectName) ?? LookupByDisplayName(item.weaponName);
    }

    /// <summary>
    /// The name the game displays for a pool weapon, falling back to the prefab name when the
    /// prefab carries no ItemBehaviour.
    /// </summary>
    public static string DisplayNameOf(GameObject weapon)
    {
        if (weapon == null) return null;

        ItemBehaviour behaviour = weapon.GetComponent<ItemBehaviour>();
        return string.IsNullOrEmpty(behaviour?.weaponName) ? weapon.name : behaviour.weaponName;
    }
}
