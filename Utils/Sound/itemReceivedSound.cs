using Straftapelago.Finnegan_McD.org.Archipelago;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// The sound that announces an item the multiworld has just sent - a weapon, a trap or a buff
/// alike. Local in every sense: the item arrives over Archipelago rather than over Mycelium, and
/// the clip is played through an AudioSource on this mod's own overlay host, so only the player
/// it was sent to hears it. The clip comes out of the roulette asset bundle, where Unity has
/// already decoded it, and lives as long as the bundle does - which is never unloaded - so it
/// outlives the overlay host being destroyed and re-created on the first scene load.
/// </summary>
internal static class ItemReceivedSound
{
    /// <summary>
    /// How soon after one play another is dropped. The room can deliver several items in the
    /// same frame, and a dozen copies of one clip on top of each other is noise rather than a
    /// cue.
    /// </summary>
    private const float RetriggerSeconds = 0.2f;

    private static AudioSource source;

    private static float lastPlayedAt = float.NegativeInfinity;
    private static bool warnedAboutMissingClip;
    private static bool warnedAboutMissingSource;

    /// <summary>
    /// Puts the AudioSource on the overlay's host object. Called from
    /// <see cref="ArchipelagoOverlay"/>'s spawn, because that host is re-created after the
    /// frame-0 DontDestroyOnLoad reset and this component has to follow it.
    /// </summary>
    internal static void AttachTo(MonoBehaviour host)
    {
        if (host == null) return;

        source = host.gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = false;

        // Flat 2D: this is a message to the player, not something happening at a place in the
        // map, so it must not be attenuated or panned by where the host object sits.
        source.spatialBlend = 0f;
    }

    /// <summary>
    /// Plays the cue for an item that has just arrived. Silent for the inventory the room
    /// replays on connect, which is not news, and silent at a volume of zero. Main thread only:
    /// it touches the AudioSource and the clock.
    /// </summary>
    internal static void Play()
    {
        // Not news: a fresh process connects with its item index at zero, so the room replays
        // every item the slot has ever been sent.
        if (!ArchipelagoClient.AcceptingNewItems) return;

        AudioClip clip = Plugin.ItemReceivedClip;
        if (clip == null)
        {
            // Plugin.Awake has already said why the bundle asset is missing; once more here
            // ties that to the cue that is not playing, and not once per item after that.
            if (warnedAboutMissingClip) return;

            warnedAboutMissingClip = true;
            Plugin.BepinLogger.LogWarning(
                "[ItemReceivedSound] the item received clip did not load from the asset bundle, " +
                "so no cue will play.");
            return;
        }

        if (source == null)
        {
            if (warnedAboutMissingSource) return;

            warnedAboutMissingSource = true;
            Plugin.BepinLogger.LogWarning(
                "[ItemReceivedSound] an item arrived before the overlay host existed, so there is " +
                "no AudioSource to play the cue through.");
            return;
        }

        float volume = ArchipelagoMenu.ItemReceivedSoundVolume.Value / 100f;
        if (volume <= 0f) return;

        // unscaledTime rather than time: the game slows and stops the clock, and a cue about the
        // multiworld is not part of what it is slowing down.
        float now = Time.unscaledTime;
        if (now - lastPlayedAt < RetriggerSeconds) return;

        lastPlayedAt = now;
        source.PlayOneShot(clip, volume);
    }
}
