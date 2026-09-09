using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// Which way the beat is holding the player over on the current tick.
/// </summary>
internal enum MetronomeBeatLean
{
    None,
    Left,
    Right,
}

/// <summary>
/// The metronome's rhythm: the one beat loop in the mod, swinging the player left and right.
/// <see cref="MetronomeTrap"/> drives it at a fixed interval and <see cref="MadeInHeaven"/> at
/// one that shortens as its countdown runs, so the two share a single phase and never drift.
/// </summary>
internal static class MetronomeBeat
{
    /// <summary>
    /// The four phases, in order, repeating: swing left, upright, swing right, upright.
    /// </summary>
    private static readonly MetronomeBeatLean[] BeatLeans =
    {
        MetronomeBeatLean.Left,
        MetronomeBeatLean.None,
        MetronomeBeatLean.Right,
        MetronomeBeatLean.None,
    };

    /// <summary>
    /// Floor on the interval a driver may ask for, so a hand-edited .cfg cannot make the
    /// catch-up loop in <see cref="Advance"/> run away.
    /// </summary>
    public const float MinimumInterval = 0.05f;

    /// <summary>Which phase the next beat moves to.</summary>
    private static int beatIndex;

    /// <summary>Time spent since the last beat, in the driving countdown's own time.</summary>
    private static float sinceLastBeat;

    private static MetronomeBeatLean currentLean = MetronomeBeatLean.None;

    /// <summary><see cref="Time.frameCount"/> of the last frame <see cref="Advance"/> ran on.</summary>
    private static int lastBeatFrame = -1;

    /// <summary>
    /// Whether a driver currently owns the player's lean. Stays true while a countdown is held
    /// (dead, between rounds), so the player keeps the pose the last beat put them in.
    /// </summary>
    public static bool Running { get; private set; }

    /// <summary>Which way the beat is holding the player right now.</summary>
    public static MetronomeBeatLean CurrentLean => currentLean;

    /// <summary>
    /// Whether the beat is actually moving this frame, which is what
    /// <see cref="MetronomeMovement.Active"/> switches the leaning modifiers on. The previous
    /// frame counts too, because Unity does not order Update between components, and a
    /// one-frame window stops that ordering deciding whether the modifiers are on.
    /// </summary>
    public static bool IsBeating => Running && Time.frameCount - lastBeatFrame <= 1;

    /// <summary>Takes the lean, from upright and at the top of the rhythm.</summary>
    public static void Start()
    {
        Running = true;
        beatIndex = 0;
        sinceLastBeat = 0f;

        // Upright to begin with, so the first swing lands one full interval in rather than
        // the moment the countdown starts.
        currentLean = MetronomeBeatLean.None;
        lastBeatFrame = -1;
    }

    /// <summary>Gives the lean back, standing the player up.</summary>
    public static void Stop()
    {
        Running = false;
        beatIndex = 0;
        sinceLastBeat = 0f;
        currentLean = MetronomeBeatLean.None;
        lastBeatFrame = -1;
    }

    /// <summary>
    /// Spends <paramref name="delta"/> of a driving countdown against the rhythm, swinging the
    /// player as each beat lands.
    /// </summary>
    /// <param name="delta">Time the driver actually spent this frame, so a held countdown holds
    /// the beat with it.</param>
    /// <param name="intervalSeconds">How long this beat lasts. Re-read every call, which is what
    /// lets Made in Heaven shorten it as it goes.</param>
    public static void Advance(float delta, float intervalSeconds)
    {
        if (!Running) return;

        sinceLastBeat += delta;
        lastBeatFrame = Time.frameCount;

        float interval = Mathf.Max(MinimumInterval, intervalSeconds);

        // A loop, not a single test: one long frame can cover more than one beat. Subtracting
        // the interval rather than zeroing keeps the beat on its own clock.
        while (sinceLastBeat >= interval)
        {
            sinceLastBeat -= interval;
            currentLean = BeatLeans[beatIndex];

            // Wrapped here rather than indexed with a modulo, so the counter cannot run away
            // over a long session.
            beatIndex = (beatIndex + 1) % BeatLeans.Length;
        }
    }
}
