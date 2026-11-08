using System.Buffers.Binary;
using System.Text;

namespace GlassWorld.Presentation.Audio;

/// <summary>Mono sound as samples in -1..1 with its sample rate.</summary>
/// <param name="Samples">Samples.</param>
/// <param name="SampleRate">Samples per second.</param>
public sealed record MonoSound(float[] Samples, int SampleRate)
{
    /// <summary>Length in seconds.</summary>
    public double Seconds => (double)Samples.Length / SampleRate;
}

/// <summary>
/// WAV files and loudness for the base sound library (B8-4): reads 16-bit PCM and 32-bit float WAV
/// (any channel count, mixed to mono), writes 16-bit PCM mono, and measures gated RMS loudness and
/// peak. No external library; the same code serves the library tool and the tests.
/// </summary>
public static class Pcm
{
    /// <summary>Reads a WAV file (PCM 16-bit or IEEE float 32-bit) and mixes it to mono.</summary>
    /// <exception cref="InvalidDataException">Not a supported WAV file.</exception>
    public static MonoSound ReadWav(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 12 || Encoding.ASCII.GetString(data, 0, 4) != "RIFF" || Encoding.ASCII.GetString(data, 8, 4) != "WAVE")
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        int format = 0, channels = 0, rate = 0, bits = 0;
        var position = 12;
        while (position + 8 <= data.Length)
        {
            var id = Encoding.ASCII.GetString(data, position, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position + 4));
            var body = position + 8;
            if (size < 0 || body + size > data.Length)
            {
                throw new InvalidDataException($"Chunk '{id}' runs past the end of the file.");
            }

            if (id == "fmt ")
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 14));
                if (format == 0xFFFE && size >= 26)
                {
                    // WAVE_FORMAT_EXTENSIBLE: the real format is the first two bytes of the sub-format GUID.
                    format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 24));
                }
            }
            else if (id == "data")
            {
                return Decode(data.AsSpan(body, size), format, channels, rate, bits);
            }

            position = body + size + (size & 1);
        }

        throw new InvalidDataException("No data chunk.");
    }

    /// <summary>Writes 16-bit PCM mono (values outside -1..1 are clipped).</summary>
    public static byte[] WriteWav16(MonoSound sound)
    {
        ArgumentNullException.ThrowIfNull(sound);
        var dataBytes = sound.Samples.Length * 2;
        var bytes = new byte[44 + dataBytes];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 36 + dataBytes);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(bytes, 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), sound.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), sound.SampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), dataBytes);
        for (var i = 0; i < sound.Samples.Length; i++)
        {
            var value = (short)Math.Round(Math.Clamp(sound.Samples[i], -1f, 1f) * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + (i * 2)), value);
        }

        return bytes;
    }

    /// <summary>
    /// Gated RMS loudness in dBFS: mean power of the 50 ms frames above <paramref name="gateDbfs"/>
    /// (silence before and after a sound does not lower it). -120 if every frame is below the gate.
    /// </summary>
    public static double LoudnessDbfs(MonoSound sound, double gateDbfs)
    {
        ArgumentNullException.ThrowIfNull(sound);
        var frame = Math.Max(1, sound.SampleRate / 20);
        var gate = Math.Pow(10, gateDbfs / 10);
        double sum = 0;
        var count = 0;
        for (var start = 0; start < sound.Samples.Length; start += frame)
        {
            var end = Math.Min(sound.Samples.Length, start + frame);
            double power = 0;
            for (var i = start; i < end; i++)
            {
                power += (double)sound.Samples[i] * sound.Samples[i];
            }

            power /= end - start;
            if (power > gate)
            {
                sum += power;
                count++;
            }
        }

        return count == 0 ? -120 : 10 * Math.Log10(sum / count);
    }

    /// <summary>
    /// Noise floor in dBFS (TL #19652): the 10th percentile of the RMS of 50 ms frames, ignoring digital
    /// silence (below -90 dBFS). For a one-shot that is the hiss before and after the sound; for a loop the
    /// quietest moments. -120 for silence.
    /// </summary>
    public static double NoiseFloorDbfs(MonoSound sound)
    {
        ArgumentNullException.ThrowIfNull(sound);
        var frame = Math.Max(1, sound.SampleRate / 20);
        var levels = new List<double>();
        for (var start = 0; start + frame <= sound.Samples.Length; start += frame)
        {
            double sum = 0;
            for (var i = start; i < start + frame; i++)
            {
                sum += sound.Samples[i] * (double)sound.Samples[i];
            }

            var db = 10 * Math.Log10((sum / frame) + 1e-24);
            if (db > -90)
            {
                levels.Add(db);
            }
        }

        if (levels.Count == 0)
        {
            return -120;
        }

        levels.Sort();
        return levels[(int)(levels.Count * 0.1)];
    }

    /// <summary>Highest absolute sample in dBFS (-120 for silence).</summary>
    public static double PeakDbfs(MonoSound sound)
    {
        ArgumentNullException.ThrowIfNull(sound);
        var peak = sound.Samples.Length == 0 ? 0 : sound.Samples.Max(Math.Abs);
        return peak <= 0 ? -120 : 20 * Math.Log10(peak);
    }

    private static MonoSound Decode(ReadOnlySpan<byte> data, int format, int channels, int rate, int bits)
    {
        if (channels < 1 || rate < 1)
        {
            throw new InvalidDataException("Missing or broken fmt chunk.");
        }

        var bytesPerSample = (format, bits) switch
        {
            (1, 16) => 2,
            (3, 32) => 4,
            _ => throw new InvalidDataException($"Unsupported WAV format {format} with {bits} bits."),
        };
        var frames = data.Length / (bytesPerSample * channels);
        var samples = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var at = ((f * channels) + c) * bytesPerSample;
                sum += bytesPerSample == 2
                    ? BinaryPrimitives.ReadInt16LittleEndian(data[at..]) / 32768f
                    : BinaryPrimitives.ReadSingleLittleEndian(data[at..]);
            }

            samples[f] = sum / channels;
        }

        return new MonoSound(samples, rate);
    }
}
