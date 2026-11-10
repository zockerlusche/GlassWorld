using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>Night look (A1367): moonlight, stars and a minimum ambient light from content/look/daylight.json.</summary>
public sealed class DaylightMixTests
{
    private static readonly LookDaylight Daylight = ContractSerializer.Deserialize<LookDaylight>(
        File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "daylight.json")));

    [Fact]
    public void TheNightIsLitByTheMoonFromAboveTheHorizonAndNeverPitchBlack()
    {
        var night = DaylightMix.Of(Daylight, "night");

        // The old night light came from the sun below the horizon (-30°), i.e. from underneath.
        Assert.Equal(1, night.Moon);
        Assert.Equal(Daylight.Moon.ElevationDeg, night.LightElevationDeg, 6);
        Assert.True(night.LightElevationDeg > 0);
        Assert.Equal(Rgb.Parse(Daylight.Moon.LightColor), night.LightColor);
        Assert.Equal(Daylight.Moon.LightEnergy, night.LightEnergy, 6);
        Assert.True(night.Stars > 0);
        Assert.All(Daylight.Phases, p => Assert.True(DaylightMix.Of(Daylight, p.Phase).AmbientEnergy >= Daylight.NightMinBrightness));
    }

    [Fact]
    public void TheDayIsTheSunAndTwilightBlendsCalmly()
    {
        var day = DaylightMix.Of(Daylight, "day");
        var sun = Daylight.Phases.Single(p => p.Phase == "day");
        Assert.Equal((0.0, 0.0), (day.Moon, day.Stars));
        Assert.Equal((sun.SunElevationDeg, Daylight.SunAzimuthDeg), (day.LightElevationDeg, day.LightAzimuthDeg));
        Assert.Equal(Rgb.Parse("#ffffff"), day.WaterLight);

        // Dusk shows a few stars and a faint moon; half way to the night everything lies in between.
        var (dusk, night) = (DaylightMix.Of(Daylight, "dusk"), DaylightMix.Of(Daylight, "night"));
        Assert.InRange(dusk.Stars, 0.01, night.Stars - 0.01);
        var half = dusk.Lerp(night, 0.5);
        Assert.InRange(half.Stars, dusk.Stars, night.Stars);
        Assert.InRange(half.LightEnergy, 0, Math.Max(dusk.LightEnergy, night.LightEnergy));
        Assert.InRange(half.LightElevationDeg, 0, 90);
        Assert.Equal(night, dusk.Lerp(night, 1));
        Assert.Equal(dusk, dusk.Lerp(night, 0));
    }

    [Fact]
    public void AnUnknownPhaseIsADataError() =>
        Assert.Throws<InvalidDataException>(() => DaylightMix.Of(Daylight, "noon"));
}
