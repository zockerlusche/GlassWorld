using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Presentation.Tests;

/// <summary>The light follows the world clock (B2-5, A1367): phase choice and blend by time of day, smooth at x16, no shadow pop.</summary>
public sealed class DaylightClockTests
{
    private static readonly LookDaylight Daylight = ContractSerializer.Deserialize<LookDaylight>(
        File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "daylight.json")));

    private static readonly WorldCalendar Calendar = WorldCalendar.FromJson(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "world", "time.json")));

    // Sunrise 06:00, sunset 19:00, twilight 45 min, blend 40 min (a short twilight blends over half of itself).
    private static DaylightClock.Blend At(double minute) => DaylightClock.BlendAt(minute, 1440, 360, 1140, 45, 40);

    [Theory]
    [InlineData(0, "night")]
    [InlineData(300, "night")]
    [InlineData(382.5, "dawn")]
    [InlineData(720, "day")]
    [InlineData(1162.5, "dusk")]
    [InlineData(1400, "night")]
    public void InTheMiddleOfAPhaseItsOwnLookShows(double minute, string phase) =>
        Assert.Equal(new DaylightClock.Blend(phase, phase, 0), At(minute));

    [Fact]
    public void PhaseChangesBlendAroundTheCoreBoundaries()
    {
        // Sunrise: night -> dawn, 40 min of night before, 22.5 min of dawn after.
        Assert.Equal(new DaylightClock.Blend("night", "dawn", 40 / 62.5), At(360));
        Assert.Equal(("dawn", "day"), (At(400).From, At(400).To));
        Assert.Equal(("day", "dusk"), (At(1130).From, At(1130).To));
        Assert.Equal(("dusk", "night"), (At(1190).From, At(1190).To));

        // The minute wraps around midnight; no blend means a fixed phase.
        Assert.Equal(At(10), At(1450));
        Assert.Equal(new DaylightClock.Blend("dawn", "dawn", 0), DaylightClock.BlendAt(360.5, 1440, 360, 1140, 45, 0));
    }

    [Fact]
    public void TheLightChangesSmoothlyOverTheWholeDayAtFastForward()
    {
        // x16 for one game day: frames of 1/60 s; the light may never jump between frames.
        var ticksPerFrame = 16.0 / 60 / Calendar.RealSecondsPerTickAtNormalSpeed;
        var previous = DaylightClock.At(Daylight, Calendar, 0, 0);
        for (var time = ticksPerFrame; time < Calendar.TicksPerDay * 2; time += ticksPerFrame)
        {
            var tick = (long)time;
            var mix = DaylightClock.At(Daylight, Calendar, tick, time - tick);
            Assert.InRange(Math.Abs(mix.LightEnergy - previous.LightEnergy), 0, 0.02);
            Assert.InRange(Math.Abs(mix.Stars - previous.Stars), 0, 0.02);
            Assert.InRange(Math.Abs(mix.ShadowOpacity - previous.ShadowOpacity), 0, 0.02);
            Assert.InRange(Math.Abs(mix.AmbientEnergy - previous.AmbientEnergy), 0, 0.02);
            if (mix.MoonLit == previous.MoonLit)
            {
                Assert.InRange(Math.Abs(mix.LightElevationDeg - previous.LightElevationDeg), 0, 2);
            }
            else
            {
                // The direction jumps only at the hand-over, where the light is (almost) out.
                Assert.InRange(Math.Max(mix.LightEnergy, previous.LightEnergy), 0, 0.02);
            }

            Assert.True(mix.AmbientEnergy >= Daylight.NightMinBrightness);
            previous = mix;
        }
    }

    [Theory]
    [InlineData("dusk", "night")]
    [InlineData("night", "dawn")]
    public void TheHandOverFadesOutJumpsAndFadesInWithoutPassingTheZenith(string from, string to)
    {
        var (a, b) = (DaylightMix.Of(Daylight, from), DaylightMix.Of(Daylight, to));
        Assert.NotEqual(a.MoonLit, b.MoonLit);
        var highest = Math.Max(a.LightElevationDeg, b.LightElevationDeg);
        var (previous, minimum) = (a, double.MaxValue);
        for (var t = 0.001; t <= 1; t += 0.001)
        {
            var mix = a.Lerp(b, t);

            // Only the two directions themselves, never one in between (sun 35° and moon 200° would cross the zenith).
            Assert.InRange(mix.LightElevationDeg, 0, highest + 1e-9);
            Assert.Contains((mix.LightElevationDeg, mix.LightAzimuthDeg), new[] { (a.LightElevationDeg, a.LightAzimuthDeg), (b.LightElevationDeg, b.LightAzimuthDeg) });

            // Light and shadow change continuously; at the jump both are out.
            Assert.InRange(Math.Abs(mix.LightEnergy - previous.LightEnergy), 0, 0.01);
            Assert.InRange(Math.Abs(mix.ShadowOpacity - previous.ShadowOpacity), 0, 0.01);
            if (mix.LightAzimuthDeg != previous.LightAzimuthDeg)
            {
                Assert.InRange(mix.LightEnergy + previous.LightEnergy, 0, 0.01);
                Assert.InRange(mix.ShadowOpacity + previous.ShadowOpacity, 0, 0.01);
            }

            Assert.True(mix.AmbientEnergy >= Daylight.NightMinBrightness);
            minimum = Math.Min(minimum, mix.LightEnergy);
            previous = mix;
        }

        Assert.InRange(minimum, 0, 0.01);
        Assert.Equal(b, a.Lerp(b, 1));
    }

    [Fact]
    public void FollowingTheClockIsTheDefaultAndNoPhaseIsCalledClock()
    {
        var look = ContractSerializer.Deserialize<LookWorld>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "world.json")));
        Assert.Equal(DaylightClock.FollowClock, look.DayPhase);
        Assert.DoesNotContain(Daylight.Phases, p => p.Phase == DaylightClock.FollowClock);
    }
}
