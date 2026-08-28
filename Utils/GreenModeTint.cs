using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// Green Mode: tints the player's camera green while the config option is on.
/// </summary>
/// <remarks>
/// <para>The tint goes through the game's own post-processing rather than an overlay drawn on
/// top of it. Every player camera already carries a PPv2 <c>PostProcessVolume</c>, and
/// <c>FirstPersonController.Awake___UserLogic</c> pulls the <c>ColorGrading</c> settings off
/// its profile into a public field. Writing <c>colorFilter</c> there tints everything the
/// camera renders, in the same pass the game already runs — no shader, no material, no second
/// camera.</para>
/// <para><b>Why colorFilter and not one of the neighbouring knobs.</b> The game writes
/// <c>saturation</c> every frame (FirstPersonController lerps it back to 0, and every damage
/// source slams it to -100 for the grey flash) and <c>gamma</c> every frame from the
/// brightness setting. Anything written to those would be overwritten within a frame.
/// Nothing in the game touches <c>colorFilter</c>, so it is ours alone and survives.</para>
/// <para>Note that a damage flash still greys the tint out briefly: PPv2 applies saturation
/// after the colour filter, so -100 saturation drains the green along with everything else.
/// That is the vanilla effect doing its job, not the tint failing.</para>
/// </remarks>
internal static class GreenModeTint
{
    /// <summary>
    /// Multiplied over everything the camera renders, from the hidden Tint RGB config entry.
    /// Read fresh on every apply rather than cached, so an edit to the config file takes
    /// effect the moment BepInEx reloads it.
    /// </summary>
    /// <remarks>
    /// Alpha is fixed at 1: PPv2 multiplies the filter channel by channel and never reads its
    /// alpha, which is why the entry itself is a Vector3.
    /// </remarks>
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
    /// multiplied over the image — so this is what "off" restores. Nothing in the game writes
    /// the field, so there is no other value that could have been there to preserve.
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
        // a profile that never touches it - without this the value would be set and ignored.
        grading.colorFilter.overrideState = true;
        grading.colorFilter.value = ArchipelagoMenu.GreenMode.Value ? Tint : Neutral;
    }

    /// <summary>
    /// Re-applies the setting to every player currently in the scene, so toggling the option
    /// mid-match is visible immediately instead of at the next spawn.
    /// </summary>
    /// <remarks>
    /// Every controller, rather than <c>FirstPersonController.instance</c>: that static is
    /// assigned by each player's Awake in turn, so in a match it names whichever player
    /// happened to spawn last, not the local one. Applying to all of them is correct and
    /// costs nothing - a remote player's camera is not rendering anyway.
    /// </remarks>
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
