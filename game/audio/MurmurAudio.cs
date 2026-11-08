using System.Reflection;
using GlassWorld.Presentation.Audio;
using Godot;

namespace GlassWorld.Game.Audio;

/// <summary>
/// Godot side of the murmur (B8 R5): loads content/voice/murmur.json (embedded) and turns a sentence
/// of an inhabitant into an <see cref="AudioStreamWav"/> for the voices bus. The speech bubbles (B6,
/// A1342) ask for the stream of the sentence they show; the same sentence of the same inhabitant is
/// rendered once and kept for a while.
/// </summary>
public static class MurmurAudio
{
    /// <summary>Streams kept at most (one per recent sentence); older ones are rendered again when needed.</summary>
    private const int Kept = 64;

    private static readonly Dictionary<(string Inhabitant, string Sex, string Text), AudioStreamWav> Streams = [];
    private static readonly Queue<(string Inhabitant, string Sex, string Text)> Order = new();

    /// <summary>The loaded murmur; null if the data was invalid.</summary>
    public static Murmur? Voice { get; private set; }

    /// <summary>Loads and validates content/voice/murmur.json; logs an error and returns false if broken.</summary>
    public static bool Load()
    {
        try
        {
            Voice = new Murmur(GlassWorld.Contracts.Validation.ContractSerializer.Deserialize<GlassWorld.Contracts.MurmurSettings>(Read("content.voice.murmur.json")));
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or GlassWorld.Contracts.Validation.ContractViolationException)
        {
            GD.PushError($"murmur: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// The murmur of a sentence. Body values are placeholders from the inhabitant id until the core
    /// has them (B9, GW1 #18477).
    /// </summary>
    public static AudioStreamWav Stream(string inhabitantId, string sex, string text)
    {
        var voice = Voice ?? throw new InvalidOperationException("murmur data not loaded");
        var key = (inhabitantId, sex, text);
        if (Streams.TryGetValue(key, out var stream))
        {
            return stream;
        }

        var pcm = voice.Render(voice.Plan(inhabitantId, voice.VoiceOf(sex, voice.PlaceholderBody(inhabitantId)), text));
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("PCM export expects a little-endian platform");
        }

        stream = new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = voice.SampleRate,
            Stereo = false,
            Data = bytes,
        };
        Streams[key] = stream;
        Order.Enqueue(key);
        while (Order.Count > Kept)
        {
            Streams.Remove(Order.Dequeue());
        }

        return stream;
    }

    /// <summary>
    /// Smoke run only: two inhabitants give two different murmurs of a sentence in DE and EN, each of a
    /// plausible length, and the same input gives the same samples.
    /// </summary>
    /// <returns>Empty if fine, otherwise the first problem.</returns>
    public static string SelfCheck()
    {
        if (Voice is null)
        {
            return "murmur data not loaded";
        }

        foreach (var text in new[] { "Wo ist das Wasser?", "Where is the water?" })
        {
            var first = Stream("inh-000001", "female", text);
            var second = Stream("inh-000002", "male", text);
            if (first.GetLength() <= 0.1 || first.GetLength() > 10 || second.GetLength() <= 0.1)
            {
                return $"murmur of '{text}' is {first.GetLength():0.00} s / {second.GetLength():0.00} s";
            }

            if (first.Data.AsSpan().SequenceEqual(second.Data))
            {
                return "two inhabitants sound the same";
            }

            Streams.Clear();
            Order.Clear();
            if (!Stream("inh-000001", "female", text).Data.AsSpan().SequenceEqual(first.Data))
            {
                return "the same sentence sounds different";
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
