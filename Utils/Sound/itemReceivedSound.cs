using System;
using System.IO;
using System.Text;
using Straftapelago.Finnegan_McD.org.Archipelago;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// The sound that announces an item the multiworld has just sent - a weapon, a trap or a buff
/// alike. Local in every sense: the item arrives over Archipelago rather than over Mycelium, and
/// the clip is played through an AudioSource on this mod's own overlay host, so only the player
/// it was sent to hears it. The wav is an embedded resource rather than a file beside the dll,
/// because build.ps1 moves only the build output into BepInEx/plugins.
/// </summary>
internal static class ItemReceivedSound
{
    private const string ResourceName = "Straftapelago.Finnegan_McD.org.Assets.suryasounds.wav";

    /// <summary>
    /// How soon after one play another is dropped. The room can deliver several items in the
    /// same frame, and a dozen copies of one clip on top of each other is noise rather than a
    /// cue.
    /// </summary>
    private const float RetriggerSeconds = 0.2f;

    /// <summary>Uncompressed PCM, the only <c>fmt </c> encoding <see cref="ReadWav"/> reads.</summary>
    private const int PcmFormat = 1;

    private static AudioClip clip;
    private static AudioSource source;

    /// <summary>
    /// Set by the first <see cref="AttachTo"/>, whether or not the clip was built. A wav that
    /// does not decode is not going to decode on the next scene load either, and retrying would
    /// repeat the warning every time the overlay host is re-created.
    /// </summary>
    private static bool loadAttempted;

    private static float lastPlayedAt = float.NegativeInfinity;
    private static bool warnedAboutMissingSource;

    /// <summary>
    /// Puts the AudioSource on the overlay's host object and builds the clip the first time
    /// through. Called from <see cref="ArchipelagoOverlay"/>'s spawn, because that host is
    /// re-created after the frame-0 DontDestroyOnLoad reset and this component has to follow it.
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

        if (loadAttempted) return;

        loadAttempted = true;
        clip = BuildClip();
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

        if (clip == null) return;

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

    /// <summary>
    /// Reads the embedded wav and turns it into a clip, or answers null having said why. Never
    /// throws: this runs from the overlay's spawn, which the rest of the overlay depends on.
    /// </summary>
    private static AudioClip BuildClip()
    {
        try
        {
            byte[] data = ReadResource();
            if (data == null) return null;

            if (!ReadWav(data, out float[] samples, out int channels, out int sampleRate)) return null;

            // AudioClip counts length in frames, and a frame is one sample per channel.
            AudioClip built = AudioClip.Create(
                "Straftapelago_ItemReceived", samples.Length / channels, channels, sampleRate, false);

            // The overlay host is destroyed and re-created on the first scene load, and the clip
            // has to outlive that.
            built.hideFlags = HideFlags.HideAndDontSave;
            built.SetData(samples, 0);

            Plugin.BepinLogger.LogInfo(
                $"[ItemReceivedSound] clip built: {channels}ch {sampleRate}Hz " +
                $"{samples.Length / channels} frames ({built.length:0.000}s)");

            return built;
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[ItemReceivedSound] could not build the clip: {error}");
            return null;
        }
    }

    /// <summary>The embedded wav's bytes, or null having said which part of that failed.</summary>
    private static byte[] ReadResource()
    {
        using Stream stream = typeof(ItemReceivedSound).Assembly.GetManifestResourceStream(ResourceName);

        if (stream == null)
        {
            Plugin.BepinLogger.LogError(
                $"[ItemReceivedSound] embedded resource '{ResourceName}' not found; no cue will play. " +
                "Check the EmbeddedResource entry in the csproj.");
            return null;
        }

        byte[] data = new byte[stream.Length];

        // A Stream may answer a short read, and the resource is over a hundred kilobytes.
        int filled = 0;
        while (filled < data.Length)
        {
            int read = stream.Read(data, filled, data.Length - filled);
            if (read <= 0) break;

            filled += read;
        }

        if (filled == data.Length) return data;

        Plugin.BepinLogger.LogError(
            $"[ItemReceivedSound] only {filled} of {data.Length} bytes of '{ResourceName}' could be read.");
        return null;
    }

    /// <summary>
    /// Reads a 16-bit PCM wav into the interleaved -1..1 samples <see cref="AudioClip.SetData"/>
    /// wants. The chunk table is walked rather than the usual 44-byte header assumed, because
    /// the shipped file carries a LIST chunk between <c>fmt </c> and <c>data</c> and a fixed
    /// offset would play that metadata as a burst of noise.
    /// </summary>
    private static bool ReadWav(byte[] data, out float[] samples, out int channels, out int sampleRate)
    {
        samples = null;
        channels = 0;
        sampleRate = 0;

        if (data.Length < 12 || ChunkId(data, 0) != "RIFF" || ChunkId(data, 8) != "WAVE")
        {
            return Reject("it is not a RIFF/WAVE file");
        }

        int audioFormat = 0;
        int bitsPerSample = 0;
        int dataStart = -1;
        int dataLength = 0;

        // Every chunk is an eight byte header and a body padded to an even length.
        int position = 12;
        while (position + 8 <= data.Length)
        {
            string chunkId = ChunkId(data, position);
            int chunkSize = BitConverter.ToInt32(data, position + 4);
            int body = position + 8;

            if (chunkSize < 0 || body + chunkSize > data.Length)
            {
                return Reject($"the '{chunkId}' chunk claims {chunkSize} bytes, which runs past the end of the file");
            }

            switch (chunkId)
            {
                case "fmt " when chunkSize >= 16:
                    audioFormat = BitConverter.ToInt16(data, body);
                    channels = BitConverter.ToInt16(data, body + 2);
                    sampleRate = BitConverter.ToInt32(data, body + 4);
                    bitsPerSample = BitConverter.ToInt16(data, body + 14);
                    break;

                case "data":
                    dataStart = body;
                    dataLength = chunkSize;
                    break;
            }

            position = body + chunkSize + (chunkSize & 1);
        }

        if (audioFormat != PcmFormat || bitsPerSample != 16)
        {
            return Reject($"it is format {audioFormat} at {bitsPerSample} bits, and only " +
                $"uncompressed 16-bit PCM is read");
        }

        if (channels < 1 || sampleRate < 1 || dataStart < 0 || dataLength < 2)
        {
            return Reject($"its header is unusable: {channels} channels, {sampleRate}Hz, " +
                $"{dataLength} bytes of samples");
        }

        int sampleCount = dataLength / 2;

        // Trailing bytes that do not make up a whole frame would put the channels out of step.
        sampleCount -= sampleCount % channels;

        samples = new float[sampleCount];
        for (int index = 0; index < sampleCount; index++)
        {
            // 32768 rather than 32767, so the most negative sample maps to exactly -1 and the
            // scale stays symmetric.
            samples[index] = BitConverter.ToInt16(data, dataStart + index * 2) / 32768f;
        }

        return true;
    }

    private static string ChunkId(byte[] data, int offset) => Encoding.ASCII.GetString(data, offset, 4);

    /// <summary>Reports why the wav was not read, and answers false so callers can return it.</summary>
    private static bool Reject(string reason)
    {
        Plugin.BepinLogger.LogError(
            $"[ItemReceivedSound] the embedded sound was not loaded because {reason}; no cue will play.");
        return false;
    }
}
