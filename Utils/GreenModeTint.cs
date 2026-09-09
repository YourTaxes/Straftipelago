using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// Green Mode: tints the player's camera green while the config option is on.
/// The tint goes through the game's own post-processing: every player camera carries a PPv2
/// volume whose ColorGrading settings FirstPersonController pulls into a public field, so
/// writing <c>colorFilter</c> tints everything the camera renders in a pass the game already
/// runs. colorFilter specifically, because the game writes saturation and gamma every frame and
/// would overwrite anything put there. A damage flash still greys the tint out briefly, since
/// PPv2 applies saturation after the colour filter.
/// </summary>
internal static class GreenModeTint
{
    /// <summary>
    /// Multiplied over everything the camera renders, from the hidden Tint RGB config entry.
    /// Read fresh on every apply, so an edit to the config file takes effect the moment BepInEx
    /// reloads it. Alpha is fixed at 1, because PPv2 multiplies channel by channel and never
    /// reads it - which is why the entry is a Vector3.
    /// </summary>
    private static Color Tint
    {
        get
        {
            Vector3 rgb = ArchipelagoMenu.GreenModeTintRgb.Value;
            return new Color(rgb.x, rgb.y, rgb.z, 1f);
        }
    }

    /// <summary>
    /// PPv2's own default for <c>colorFilter</c>, and the identity value for a filter that is
    /// multiplied over the image, so this is what "off" restores.
    /// </summary>
    private static readonly Color Neutral = Color.white;

    /// <summary>
    /// Applies the current setting to one player's camera. Safe to call for any
    /// <see cref="FirstPersonController"/>, local or remote: each player camera's volume holds
    /// its own runtime profile clone, so this never leaks into another player's view or into
    /// the profile asset on disk.
    /// </summary>
    public static void Apply(FirstPersonController controller)
    {
        if (controller == null) return;

        // Assigned from volume.profile.TryGetSettings<ColorGrading>(), which can legitimately
        // come back empty if the profile ever ships without the effect.
        ColorGrading grading = controller.colorGrading;
        if (grading == null) return;

        // A PPv2 parameter is only read when its override is on, and colorFilter's is off in
        // a profile that never touches it, without this the value would be set and ignored.
        grading.colorFilter.overrideState = true;
        grading.colorFilter.value = ArchipelagoMenu.GreenMode.Value ? Tint : Neutral;
    }

    /// <summary>
    /// Re-applies the setting to every player currently in the scene, so toggling the option
    /// mid-match is visible immediately instead of at the next spawn. Every controller rather
    /// than FirstPersonController.instance, which names whichever player spawned last; a remote
    /// player's camera is not rendering anyway.
    /// </summary>
    public static void RefreshAll()
    {
        FirstPersonController[] controllers = Object.FindObjectsOfType<FirstPersonController>();
        foreach (FirstPersonController controller in controllers)
        {
            Apply(controller);
        }

        Plugin.BepinLogger.LogInfo(
            $"[GreenMode] {(ArchipelagoMenu.GreenMode.Value ? "on" : "off")}, " +
            $"applied to {controllers.Length} player camera(s)");
    }
}
