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
/// (any channel count, mixed to mono), writes 16-bit PCM mono, and measures integrated loudness (LUFS, BS.1770) and
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
    /// Integrated loudness in LUFS after ITU-R BS.1770 (TL #19678): K-weighting (high shelf plus RLB
    /// high-pass, coefficients for any sample rate as in libebur128), 400 ms blocks overlapping by 75 %,
    /// an absolute gate at <paramref name="gateLufs"/> and the relative gate 10 LU below the gated mean.
    /// Low rumble hardly counts, so cutting it no longer makes normalising lift the hiss. A sound shorter
    /// than one block is measured as one block. -120 for silence.
    /// </summary>
    public static double Loudness(MonoSound sound, double gateLufs)
    {
        ArgumentNullException.ThrowIfNull(sound);
        if (sound.Samples.Length == 0)
        {
            return -120;
        }

        var weighted = KWeighted(sound);
        var block = Math.Min(weighted.Length, (int)(0.4 * sound.SampleRate));
        var step = Math.Max(1, block / 4);
        var powers = new List<double>();
        for (var start = 0; start + block <= weighted.Length; start += step)
        {
            double sum = 0;
            for (var i = start; i < start + block; i++)
            {
                sum += weighted[i] * weighted[i];
            }

            powers.Add(sum / block);
        }

        static double Lufs(double power) => -0.691 + (10 * Math.Log10(power + 1e-30));
        var absolute = powers.Where(p => Lufs(p) > gateLufs).ToList();
        if (absolute.Count == 0)
        {
            return -120;
        }

        var relative = Lufs(absolute.Average()) - 10;
        var gated = absolute.Where(p => Lufs(p) > relative).ToList();
        return Lufs(gated.Average());
    }

    /// <summary>The K-weighted signal (BS.1770 stage 1 high shelf, stage 2 RLB high-pass).</summary>
    private static double[] KWeighted(MonoSound sound)
    {
        var rate = (double)sound.SampleRate;
        double k = Math.Tan(Math.PI * 1681.974450955533 / rate), q = 0.7071752369554196;
        var vh = Math.Pow(10, 3.999843853973347 / 20);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1 + (k / q) + (k * k);
        var shelf = (B0: (vh + (vb * k / q) + (k * k)) / a0, B1: 2 * ((k * k) - vh) / a0, B2: (vh - (vb * k / q) + (k * k)) / a0, A1: 2 * ((k * k) - 1) / a0, A2: (1 - (k / q) + (k * k)) / a0);
        k = Math.Tan(Math.PI * 38.13547087602444 / rate);
        q = 0.5003270373238773;
        a0 = 1 + (k / q) + (k * k);
        var highPass = (B0: 1.0, B1: -2.0, B2: 1.0, A1: 2 * ((k * k) - 1) / a0, A2: (1 - (k / q) + (k * k)) / a0);
        var x = new double[sound.Samples.Length];
        for (var i = 0; i < x.Length; i++)
        {
            x[i] = sound.Samples[i];
        }

        return Biquad(Biquad(x, shelf), highPass);
    }

    private static double[] Biquad(double[] x, (double B0, double B1, double B2, double A1, double A2) f)
    {
        var y = new double[x.Length];
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (var i = 0; i < x.Length; i++)
        {
            var value = (f.B0 * x[i]) + (f.B1 * x1) + (f.B2 * x2) - (f.A1 * y1) - (f.A2 * y2);
            (x2, x1, y2, y1) = (x1, x[i], y1, value);
            y[i] = value;
        }

        return y;
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
