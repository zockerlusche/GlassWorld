using System.Numerics;
using GlassWorld.Presentation.Audio;

/// <summary>
/// Keeps normalising from raising noise (TL #19652, Marcel W1667): a gentle high-pass against rumble for
/// every file, and for files that are boosted a lot or whose noise floor would end up high a gentle
/// spectral noise gate (stationary noise per frequency band from the quietest frames, at most
/// <see cref="MaxReductionDb"/> less, so rain or waves keep their body).
/// </summary>
internal static class Cleaning
{
    /// <summary>Most a band is turned down by the noise gate.</summary>
    public const double MaxReductionDb = 12;

    private const int Size = 2048;
    private const int Hop = 512;

    /// <summary>Second-order Butterworth high-pass.</summary>
    public static float[] HighPass(float[] samples, int rate, double cutoffHz)
    {
        var w = 2 * Math.PI * cutoffHz / rate;
        var cos = Math.Cos(w);
        var alpha = Math.Sin(w) / Math.Sqrt(2);
        var a0 = 1 + alpha;
        double b0 = (1 + cos) / 2 / a0, b1 = -(1 + cos) / a0, b2 = (1 + cos) / 2 / a0, a1 = -2 * cos / a0, a2 = (1 - alpha) / a0;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        var result = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i];
            var y = (b0 * x) + (b1 * x1) + (b2 * x2) - (a1 * y1) - (a2 * y2);
            (x2, x1, y2, y1) = (x1, x, y1, y);
            result[i] = (float)y;
        }

        return result;
    }

    /// <summary>Spectral noise gate (Hann STFT, overlap-add); short files are returned as they are.</summary>
    public static float[] Denoise(float[] samples)
    {
        if (samples.Length < Size * 2)
        {
            return samples;
        }

        var window = new double[Size];
        for (var i = 0; i < Size; i++)
        {
            window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / Size));
        }

        var frames = new List<Complex[]>();
        for (var start = 0; start + Size <= samples.Length; start += Hop)
        {
            var spectrum = new Complex[Size];
            for (var i = 0; i < Size; i++)
            {
                spectrum[i] = samples[start + i] * window[i];
            }

            Fft(spectrum, inverse: false);
            frames.Add(spectrum);
        }

        // Stationary noise per band: the 20th percentile of the magnitudes over all frames.
        var noise = new double[(Size / 2) + 1];
        var column = new double[frames.Count];
        for (var k = 0; k < noise.Length; k++)
        {
            for (var f = 0; f < frames.Count; f++)
            {
                column[f] = frames[f][k].Magnitude;
            }

            Array.Sort(column);
            noise[k] = column[(int)(column.Length * 0.2)];
        }

        var least = Math.Pow(10, -MaxReductionDb / 20);
        var output = new double[samples.Length];
        var weight = new double[samples.Length];
        for (var f = 0; f < frames.Count; f++)
        {
            var spectrum = frames[f];
            for (var k = 0; k < noise.Length; k++)
            {
                var magnitude = spectrum[k].Magnitude;
                var gain = magnitude <= 0 ? least : Math.Clamp((magnitude - (1.5 * noise[k])) / magnitude, least, 1);
                spectrum[k] *= gain;
                if (k > 0 && k < Size / 2)
                {
                    spectrum[Size - k] = Complex.Conjugate(spectrum[k]);
                }
            }

            Fft(spectrum, inverse: true);
            var start = f * Hop;
            for (var i = 0; i < Size; i++)
            {
                output[start + i] += spectrum[i].Real * window[i];
                weight[start + i] += window[i] * window[i];
            }
        }

        var result = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            // Where no frame covered the sample (the very end), keep it as it was.
            result[i] = weight[i] > 1e-6 ? (float)(output[i] / weight[i]) : samples[i];
        }

        return result;
    }

    private static void Fft(Complex[] data, bool inverse)
    {
        var n = data.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (data[i], data[j]) = (data[j], data[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = 2 * Math.PI / length * (inverse ? 1 : -1);
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += length)
            {
                var w = Complex.One;
                for (var k = 0; k < length / 2; k++)
                {
                    var u = data[i + k];
                    var v = data[i + k + (length / 2)] * w;
                    data[i + k] = u + v;
                    data[i + k + (length / 2)] = u - v;
                    w *= step;
                }
            }
        }

        if (inverse)
        {
            for (var i = 0; i < n; i++)
            {
                data[i] /= n;
            }
        }
    }

    /// <summary>The noise floor of samples (see <see cref="Pcm.NoiseFloorDbfs"/>).</summary>
    public static double Floor(float[] samples, int rate) => Pcm.NoiseFloorDbfs(new MonoSound(samples, rate));
}
