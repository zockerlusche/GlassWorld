using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Weather;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1382 S1 (plan D998, review GW1 #23950): rough days are part of the climate. An extreme weather begins by the
/// weather's own daily roll, holds for its hours (the usual changes rest), is followed by a calm, and a wish of the
/// player ends it; a heat wave lifts the air over the curve. Nothing outside the weather sets it.
/// </summary>
public sealed class WeatherExtremesTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    [Fact]
    public void AStormThatBeginsHoldsForItsHoursAndACalmFollows()
    {
        var climate = Climate(storm: 1000, heat: 0);
        var (world, weather) = Bare(climate);
        var day = world.Calendar.TicksPerDay;

        world.Run(day + 1);
        Assert.True(weather.InExtreme);
        Assert.Equal("storm", weather.Current);
        var hours = climate.Weather.Extremes!.Weather.First().HoursMax;

        // Held for at most its hours although the weather would change every few hours meanwhile.
        world.Run((hours * world.Calendar.Settings.TicksPerHour) - 2);
        Assert.Equal("storm", weather.Current);
        world.Run(2);
        Assert.False(weather.InExtreme);

        // The calm: with a chance of 1000 the next storm begins only once the cooldown days have passed.
        var ended = world.Tick;
        for (var i = 0; i < day * 10 && !weather.InExtreme; i++)
        {
            world.Run(1);
        }

        Assert.True(weather.InExtreme);
        Assert.True(world.Tick - ended >= day * climate.Weather.Extremes.CooldownDays, $"{world.Tick - ended} ticks of calm");
    }

    [Fact]
    public void AWishOfThePlayerEndsARunningStorm()
    {
        var (world, weather) = Bare(Climate(storm: 1000, heat: 0));
        world.Run(world.Calendar.TicksPerDay + 1);
        Assert.True(weather.InExtreme);

        weather.Force("clear", world.Tick);
        Assert.False(weather.InExtreme);
        Assert.Equal("clear", weather.Current);
    }

    [Fact]
    public void AHeatWaveLiftsTheAirByItsOffset()
    {
        var climate = Climate(storm: 0, heat: 1000);
        var (world, weather) = Bare(climate);
        var before = weather.TemperatureAt(world.Calendar.At(world.Calendar.TicksPerDay + 5), null);
        world.Run(world.Calendar.TicksPerDay + 1);
        Assert.True(weather.InHeatWave);
        var during = weather.TemperatureAt(world.Calendar.At(world.Calendar.TicksPerDay + 5), null);
        Assert.Equal(climate.Weather.Extremes!.HeatWaves.First().OffsetTenthC, during - before);
    }

    [Fact]
    public void ARunningExtremeSurvivesSavingAndLoading()
    {
        var climate = Climate(storm: 1000, heat: 0);
        var (world, weather) = Bare(climate);
        world.Run(world.Calendar.TicksPerDay + 1);
        var saved = weather.Capture();
        Assert.NotNull(saved.ExtremeUntil);

        var (world2, weather2) = Bare(climate);
        weather2.Restore(saved, world.Tick);
        Assert.True(weather2.InExtreme);
        Assert.Equal(saved.ExtremeUntil, weather2.Capture().ExtremeUntil);
        _ = world2;
    }

    [Fact]
    public void AClimateWithoutExtremesHasNone()
    {
        var climate = Copy();
        climate.Weather.Extremes = null;
        var (world, weather) = Bare(climate);
        world.Run(world.Calendar.TicksPerDay * 5);
        Assert.False(weather.InExtreme);
        Assert.False(weather.InHeatWave);
    }

    private static ClimateSettings Copy() =>
        ContractSerializer.Deserialize<ClimateSettings>(ContractSerializer.Serialize(Content.Value.WorldGen.Climates["temperate"]));

    /// <summary>The shipped climate with every season's storm and heat-wave chance set (spring is the first season).</summary>
    private static ClimateSettings Climate(int storm, int heat)
    {
        var climate = Copy();
        var extremes = climate.Weather.Extremes!;
        extremes.Weather = [.. extremes.Weather.Where(e => e.Weather == "storm").Select(e => { e.ChancePermille = storm; e.HoursMin = e.HoursMax; return e; })];
        foreach (var season in climate.Weather.Seasons.Select(s => s.Season))
        {
            if (!extremes.HeatWaves.Any(h => h.Season == season))
            {
                extremes.HeatWaves.Add(new ClimateHeatWave { Season = season, ChancePermille = heat, DaysMin = 1, DaysMax = 1, OffsetTenthC = 50 });
            }
        }

        foreach (var wave in extremes.HeatWaves)
        {
            wave.ChancePermille = heat;
        }

        return climate;
    }

    private static (World World, WeatherSystem Weather) Bare(ClimateSettings climate)
    {
        var world = new World(5, Content.Value.Calendar);
        var settings = world.Calendar.Settings;
        var weather = new WeatherSystem(climate, 0, settings.TicksPerHour, settings.HoursPerDay, 5);
        world.Add(weather);
        return (world, weather);
    }
}
