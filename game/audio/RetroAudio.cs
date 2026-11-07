using System.Reflection;
using GlassWorld.Presentation.Audio;
using Godot;

namespace GlassWorld.Game.Audio;

/// <summary>
/// Godot side of the retro fallback (B8 R3): loads the sound data embedded from content/sound, turns
/// rendered PCM into <see cref="AudioStreamWav"/> streams and keeps them per sound variant. The audio
/// itself is rendered once and cached (pre-rendered streams are cheaper than live generators).
/// </summary>
public static class RetroAudio
{
    private static readonly Dictionary<(string Id, int Variant, ulong WorldSeed), AudioStreamWav> Streams = [];

    /// <summary>The loaded library; null if the data was invalid.</summary>
    public static SoundLibrary? Library { get; private set; }

    /// <summary>Loads and validates content/sound; logs an error and returns false if broken.</summary>
    public static bool Load()
    {
        try
        {
            var target = GlassWorld.Contracts.Validation.ContractSerializer.Deserialize<GlassWorld.Contracts.BaseSoundLibrary>(Read("content.sound.library.json")).Target;
            Library = SoundLibrary.FromJson(Read("content.sound.sound_map.json"), Read("content.sound.synth_recipes.json"), loudness: target);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or GlassWorld.Contracts.Validation.ContractViolationException)
        {
            GD.PushError($"audio: {e.Message}");
            return false;
        }
    }

    /// <summary>A playable stream for one variant of a sound.</summary>
    public static AudioStreamWav Stream(string soundId, int variant, ulong worldSeed)
    {
        if (Library is null)
        {
            throw new InvalidOperationException("audio data not loaded");
        }

        if (Streams.TryGetValue((soundId, variant, worldSeed), out var stream))
        {
            return stream;
        }

        var pcm = Library.Render(soundId, variant, worldSeed);
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("PCM export expects a little-endian platform");
        }

        stream = new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = Library.SampleRate,
            Stereo = false,
            Data = bytes,
            LoopMode = Library.Recipe(soundId).Loop ? AudioStreamWav.LoopModeEnum.Forward : AudioStreamWav.LoopModeEnum.Disabled,
            LoopEnd = pcm.Length,
        };
        Streams[(soundId, variant, worldSeed)] = stream;
        return stream;
    }

    /// <summary>
    /// Smoke run only: every sound the map uses becomes a stream of the expected length.
    /// </summary>
    /// <returns>Empty if fine, otherwise the first problem.</returns>
    public static string SelfCheck()
    {
        if (Library is null)
        {
            return "audio data not loaded";
        }

        foreach (var id in Library.UsedSoundIds())
        {
            var stream = Stream(id, 0, 1);
            var expected = Library.Recipe(id).DurationS;
            if (Math.Abs(stream.GetLength() - expected) > 0.01)
            {
                return $"'{id}' is {stream.GetLength():0.000} s, expected {expected:0.000} s";
            }

            // Same level as the library files (GW1 review #62 K1): on target, or quieter only when the
            // peak limit caps it.
            if (Library.Loudness is { } target)
            {
                var pcm = Library.Render(id, 0, 1);
                var wanted = Library.Recipe(id).Bus == GlassWorld.Contracts.SynthRecipeBus.Ambience ? target.LoopDbfs : target.OneShotDbfs;
                var loudness = Library.LoudnessOf(pcm);
                var peak = 20 * Math.Log10(Math.Max(1, pcm.Max(s => Math.Abs((int)s))) / 32768.0);
                if (loudness > wanted + target.ToleranceDb || (loudness < wanted - target.ToleranceDb && Math.Abs(peak - target.PeakMaxDbfs) > 0.2))
                {
                    return $"'{id}' loudness {loudness:0.0} dBFS, target {wanted:0.0}";
                }
            }
        }

        return string.Empty;
    }

    private static string Read(string name)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"'{name}' is not embedded");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }
}
