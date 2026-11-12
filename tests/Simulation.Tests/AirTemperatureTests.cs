using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Time;
using GlassWorld.Simulation.Weather;

namespace GlassWorld.Simulation.Tests;

/// <summary>B5 R1/R28: the air temperature of a climate - daily curve, biome offsets, season transition.</summary>
public sealed class AirTemperatureTests
{
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(Path.Combine(TestData.Root, "content")));

    private static WorldCalendar Calendar => Content.Value.Calendar;

    private static int HoursPerDay => Calendar.Settings.HoursPerDay;

    private static string[] Seasons => [.. Calendar.Settings.Seasons.Select(s => s.Id)];

    /// <summary>A copy of the shipped temperature table, so a test can add the optional parts.</summary>
    private static ClimateTemperature Shipped() =>
        ContractSerializer.Deserialize<ClimateSettings>(ContractSerializer.Serialize(Content.Value.WorldGen.Climates["temperate"])).Weather.Temperature;

    private static WorldTime At(string season, int dayOfSeason, int hour, int minute = 0)
    {
        var ticksPerHour = Calendar.Settings.TicksPerHour;
        var tick = (Array.IndexOf(Seasons, season) * Calendar.TicksPerSeason)
            + ((dayOfSeason - 1) * Calendar.TicksPerDay)
            + (hour * ticksPerHour) + (minute * ticksPerHour / 60);
        return Calendar.At(tick);
    }

    [Fact]
    public void WithoutTheOptionalPartsTheTemperatureIsAsBefore()
    {
        var data = Shipped();
        var air = new AirTemperature(data, HoursPerDay);
        foreach (var season in Seasons)
        {
            for (var hour = 0; hour < HoursPerDay; hour++)
            {
                var time = At(season, 1, hour);
                var expected = data.BaseTenthC
                    + data.Seasons.Single(s => s.Season == season).OffsetTenthC
                    + data.TimesOfDay.Single(t => t.TimeOfDay == DayPhases.IdOf(time.Phase)).OffsetTenthC
                    + data.Weathers.Single(w => w.Weather == "rain").OffsetTenthC;
                Assert.Equal(expected, air.At(time, "rain"));
                Assert.Equal(expected, air.At(time, "rain", "forest"));
            }
        }

        Assert.False(air.HasBiomeOffsets);
    }

    [Fact]
    public void TheHourlyCurveIsLinearByTheMinuteAndWrapsAroundMidnight()
    {
        var data = Shipped();
        data.Hourly = [new TemperatureHourlyCurve
        {
            Season = "summer",
            Points = [new TemperatureHourPoint { Hour = 4, OffsetTenthC = -40 }, new TemperatureHourPoint { Hour = 14, OffsetTenthC = 60 }],
        }];
        var air = new AirTemperature(data, HoursPerDay);
        var summer = data.BaseTenthC + data.Seasons.Single(s => s.Season == "summer").OffsetTenthC;

        Assert.Equal(summer - 40, air.At(At("summer", 2, 4), null));
        Assert.Equal(summer + 60, air.At(At("summer", 2, 14), null));
        Assert.Equal(summer + 10, air.At(At("summer", 2, 9), null));          // half way between 4 h and 14 h
        Assert.Equal(summer + 15, air.At(At("summer", 2, 9, 30), null));      // by the minute

        // From 14 h back down to 4 h of the next day: 14 hours for 100 tenths.
        var span = (HoursPerDay - 14) + 4;
        var expectedMidnight = summer + 60 + (int)((long)(-40 - 60) * (HoursPerDay - 14) / span);
        Assert.Equal(expectedMidnight, air.At(At("summer", 2, 0), null));

        // Other seasons keep the time-of-day offsets.
        var spring = At("spring", 2, 9);
        Assert.Equal(
            data.BaseTenthC + data.Seasons.Single(s => s.Season == "spring").OffsetTenthC + data.TimesOfDay.Single(t => t.TimeOfDay == DayPhases.IdOf(spring.Phase)).OffsetTenthC,
            air.At(spring, null));
    }

    [Fact]
    public void BiomeOffsetsAddUpForAllSeasonsAndForOneSeason()
    {
        var data = Shipped();
        data.Biomes =
        [
            new TemperatureBiomeOffset { Biome = "forest", OffsetTenthC = -10 },
            new TemperatureBiomeOffset { Biome = "forest", Season = "summer", OffsetTenthC = -20 },
        ];
        var air = new AirTemperature(data, HoursPerDay);
        var summer = At("summer", 3, 12);
        var winter = At("winter", 3, 12);

        Assert.True(air.HasBiomeOffsets);
        Assert.Equal(air.At(summer, "clear") - 30, air.At(summer, "clear", "forest"));
        Assert.Equal(air.At(winter, "clear") - 10, air.At(winter, "clear", "forest"));
        Assert.Equal(air.At(summer, "clear"), air.At(summer, "clear", "meadow"));
    }

    [Fact]
    public void AtTheStartOfASeasonItsPartBlendsInFromThePreviousSeason()
    {
        var data = Shipped();
        var hard = new AirTemperature(data, HoursPerDay);
        data.SeasonTransitionHours = 24;
        var soft = new AirTemperature(data, HoursPerDay);
        int Hard(string season, int hour) => hard.At(At(season, 3, hour), "clear");

        // At the boundary the season's part is still the previous one's; after the transition it is its own.
        Assert.Equal(Hard("spring", 0), soft.At(At("summer", 1, 0), "clear"));
        Assert.Equal(Hard("summer", 0), soft.At(At("summer", 2, 0), "clear"));

        // Half way (12 of 24 hours) it lies exactly between, in integer maths.
        var from = Hard("spring", 12);
        var to = Hard("summer", 12);
        Assert.Equal(from + ((to - from) * 720 / 1440), soft.At(At("summer", 1, 12), "clear"));
        Assert.NotEqual(to, soft.At(At("summer", 1, 12), "clear"));

        // The first season of the cycle blends in from the last one (winter before spring).
        // (Noon: the same phase of the day in both seasons; the blend uses the phase of the current time.)
        var winter = Hard("winter", 12);
        var spring = Hard("spring", 12);
        Assert.Equal(winter + ((spring - winter) * 720 / 1440), soft.At(At("spring", 1, 12), "clear"));
    }

    [Theory]
    [InlineData("hours_not_ascending")]
    [InlineData("hour_after_the_day")]
    [InlineData("unknown_season")]
    [InlineData("two_curves_for_one_season")]
    [InlineData("unknown_biome")]
    [InlineData("biome_season_twice")]
    [InlineData("transition_out_of_calendar_order")]
    public void BrokenOptionalTemperatureDataFailsAtLoad(string broken)
    {
        var directory = CopyContent();
        try
        {
            var path = Path.Combine(directory, "climate", "temperate.json");
            var climate = ContractSerializer.Deserialize<ClimateSettings>(File.ReadAllText(path));
            var t = climate.Weather.Temperature;
            TemperatureHourPoint Point(int hour) => new() { Hour = hour, OffsetTenthC = 0 };
            TemperatureHourlyCurve Curve(string season, params int[] hours) => new() { Season = season, Points = [.. hours.Select(Point)] };
            switch (broken)
            {
                case "hours_not_ascending": t.Hourly = [Curve("summer", 10, 4)]; break;
                case "hour_after_the_day": t.Hourly = [Curve("summer", 4, HoursPerDay)]; break;
                case "unknown_season": t.Hourly = [Curve("monsoon", 4)]; break;
                case "two_curves_for_one_season": t.Hourly = [Curve("summer", 4), Curve("summer", 8)]; break;
                case "unknown_biome": t.Biomes = [new TemperatureBiomeOffset { Biome = "lava_field", OffsetTenthC = 10 }]; break;
                case "biome_season_twice":
                    t.Biomes = [new TemperatureBiomeOffset { Biome = "forest", OffsetTenthC = 10 }, new TemperatureBiomeOffset { Biome = "forest", OffsetTenthC = 20 }];
                    break;
                default:
                    t.SeasonTransitionHours = 12;
                    t.Seasons = [.. t.Seasons.Reverse()];
                    break;
            }

            File.WriteAllText(path, ContractSerializer.Serialize(climate));
            Assert.Throws<InvalidDataException>(() => ContentSet.Load(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CopyContent()
    {
        var source = Path.Combine(TestData.Root, "content");
        var target = Path.Combine(Path.GetTempPath(), "glassworld-air-content-" + Path.GetRandomFileName());
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        return target;
    }
}
