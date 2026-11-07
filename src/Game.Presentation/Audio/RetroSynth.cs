using GlassWorld.Contracts;

namespace GlassWorld.Presentation.Audio;

/// <summary>
/// Retro fallback synthesis (B8 R3): renders a recipe from content/sound/synth_recipes.json into
/// 16-bit mono PCM. The same recipe, sample rate and seed always give the same samples; variants of a
/// sound differ only through the seed (pitch and gain variation).
/// </summary>
public static class RetroSynth
{
    /// <summary>Renders one variant of a recipe.</summary>
    public static short[] Render(SynthRecipe recipe, int sampleRate, double peak, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        var random = new SplitMix64(seed);
        var pitch = 1 + (recipe.PitchVariation * random.NextSigned());
        var gain = 1 + (recipe.GainVariation * random.NextSigned());
        var length = Math.Max(1, (int)Math.Round(recipe.DurationS * sampleRate));
        var mix = new double[length];

        foreach (var layer in recipe.Layers)
        {
            RenderLayer(layer, sampleRate, pitch, recipe.DurationS, random, mix);
        }

        var loudest = mix.Max(Math.Abs);
        var scale = loudest > 0 ? peak / loudest * Math.Min(gain, 1) : 0;
        var pcm = new short[length];
        for (var i = 0; i < length; i++)
        {
            pcm[i] = (short)Math.Round(Math.Clamp(mix[i] * scale, -1, 1) * short.MaxValue);
        }

        return pcm;
    }

    private static void RenderLayer(SynthLayer layer, int sampleRate, double pitch, double duration, SplitMix64 random, double[] mix)
    {
        var start = (int)Math.Round(layer.OffsetS * sampleRate);
        var layerDuration = Math.Max(1.0 / sampleRate, duration - layer.OffsetS);
        var phase = 0.0;
        var lowpass = 0.0;
        var alpha = layer.LowpassHz is { } cutoff ? 1 - Math.Exp(-2 * Math.PI * cutoff / sampleRate) : 1.0;
        for (var i = start; i < mix.Length; i++)
        {
            var t = (double)(i - start) / sampleRate;
            var progress = Math.Min(1, t / layerDuration);
            var frequency = pitch * layer.StartHz * Math.Pow(layer.EndHz / layer.StartHz, progress);
            phase += frequency / sampleRate;
            phase -= Math.Floor(phase);
            var sample = Wave(layer.Wave, phase, random);
            lowpass += alpha * (sample - lowpass);
            var envelope = Envelope(layer, t, layerDuration);
            var tremolo = layer.TremoloHz is { } hz ? 0.5 + (0.5 * Math.Cos(2 * Math.PI * hz * t)) : 1.0;
            mix[i] += lowpass * envelope * tremolo * layer.Gain;
        }
    }

    private static double Wave(SynthLayerWave wave, double phase, SplitMix64 random) => wave switch
    {
        SynthLayerWave.Sine => Math.Sin(2 * Math.PI * phase),
        SynthLayerWave.Triangle => 1 - (4 * Math.Abs(phase - 0.5)),
        SynthLayerWave.Square => phase < 0.5 ? 1 : -1,
        SynthLayerWave.Saw => (2 * phase) - 1,
        SynthLayerWave.Noise => random.NextSigned(),
        _ => 0,
    };

    /// <summary>ADSR; the release ends exactly at the end of the layer.</summary>
    private static double Envelope(SynthLayer layer, double t, double duration)
    {
        var releaseStart = Math.Max(0, duration - layer.ReleaseS);
        double level;
        if (t < layer.AttackS)
        {
            level = t / layer.AttackS;
        }
        else if (t < layer.AttackS + layer.DecayS)
        {
            level = 1 - ((1 - layer.Sustain) * (t - layer.AttackS) / layer.DecayS);
        }
        else
        {
            level = layer.Sustain;
        }

        if (t >= releaseStart && layer.ReleaseS > 0)
        {
            level *= Math.Max(0, 1 - ((t - releaseStart) / layer.ReleaseS));
        }

        return level;
    }
}

/// <summary>Small deterministic random generator (SplitMix64); never <see cref="Random"/> (differs between .NET versions).</summary>
/// <param name="seed">Start value.</param>
public sealed class SplitMix64(ulong seed)
{
    private ulong _state = seed;

    /// <summary>Next raw value.</summary>
    public ulong Next()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform value in [-1, 1).</summary>
    public double NextSigned() => ((Next() >> 11) * (1.0 / (1UL << 53)) * 2) - 1;

    /// <summary>Stable 64-bit seed from text (FNV-1a), e.g. world seed + sound id + variant.</summary>
    public static ulong SeedOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hash = 14695981039346656037UL;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return hash;
    }
}
