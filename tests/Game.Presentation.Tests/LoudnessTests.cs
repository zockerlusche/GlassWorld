using GlassWorld.Presentation.Audio;

namespace GlassWorld.Presentation.Tests;

/// <summary>Integrated loudness after ITU-R BS.1770 (TL #19678).</summary>
public sealed class LoudnessTests
{
    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(22050)]
    public void AFullScaleSineAt997HzMeasuresAboutMinus3Lufs(int rate)
    {
        // The reference of BS.1770: K-weighting is about +0.69 dB at 1 kHz, which the -0.691 offsets.
        var samples = new float[rate * 3];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)Math.Sin(2 * Math.PI * 997 * i / rate);
        }

        Assert.InRange(Pcm.Loudness(new MonoSound(samples, rate), -70), -3.1, -2.9);
    }

    [Fact]
    public void RumbleHardlyCountsButHissDoes()
    {
        const int rate = 48000;
        float[] Tone(double hz, double amplitude) => [.. Enumerable.Range(0, rate * 2).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate)))];
        var rumble = Pcm.Loudness(new MonoSound(Tone(25, 0.5), rate), -70);
        var middle = Pcm.Loudness(new MonoSound(Tone(1000, 0.5), rate), -70);
        var hiss = Pcm.Loudness(new MonoSound(Tone(8000, 0.5), rate), -70);
        Assert.True(middle - rumble > 10, $"25 Hz {rumble:0.0} vs 1 kHz {middle:0.0}");
        Assert.True(hiss > middle, $"8 kHz {hiss:0.0} vs 1 kHz {middle:0.0}");
    }

    [Fact]
    public void SilenceAndQuietPausesDoNotLowerTheLoudness()
    {
        const int rate = 48000;
        var tone = Enumerable.Range(0, rate).Select(i => (float)(0.3 * Math.Sin(2 * Math.PI * 997 * i / rate))).ToArray();
        float[] withPause = [.. tone, .. new float[rate * 3]];
        // Silent blocks fall below the gates; only the blocks across the edge pull it down a little.
        var difference = Pcm.Loudness(new MonoSound(tone, rate), -70) - Pcm.Loudness(new MonoSound(withPause, rate), -70);
        Assert.InRange(difference, -1.0, 1.0);
        Assert.Equal(-120, Pcm.Loudness(new MonoSound(new float[rate], rate), -70));
    }
}
