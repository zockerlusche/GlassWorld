using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Weather;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1382 (R24, TL #28525 A): lightning comes only in a dry thunderstorm (no rain, so the fire can take hold) - it sets
/// one dry, tall burnable thing (a tree) alight, by the data; in a storm with rain it never strikes.
/// </summary>
public sealed class LightningTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    [Fact]
    public void InADryThunderstormLightningSetsATallTreeAlight()
    {
        var lightning = Content.Value.Fire!.Lightning!;
        Assert.Equal(0, Content.Value.WorldGen.Climates["temperate"].Weather.States.Single(s => s.Weather == lightning.Weather.First()).PrecipitationPermille);

        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        var trees = TallBurnable(host).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(trees);

        host.WeatherSystem!.Force(lightning.Weather.First(), host.World.Tick);
        var hours = 0;
        while (trees.All(host.Objects.TryGetExists) && hours < 24)
        {
            host.World.Run(host.World.Calendar.Settings.TicksPerHour);
            host.WeatherSystem.Force(lightning.Weather.First(), host.World.Tick);
            hours++;
        }

        var struck = trees.Where(id => !host.Objects.TryGetExists(id)).ToList();
        Assert.NotEmpty(struck);
        Assert.Contains(host.Objects.All, o => o.TypeId == lightning.Becomes);
    }

    [Fact]
    public void LightningSparesAWetTreeAndTheWishNeverOffersADryThunderstorm()
    {
        // Review GW1 #591 (3): in a dry thunderstorm right after rain everything is still wet - no tree catches fire.
        var thunder = Content.Value.Fire!.Lightning!.Weather.First();
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        var hour = host.World.Calendar.Settings.TicksPerHour;
        host.WeatherSystem!.Force("rain", host.World.Tick);
        host.World.Run(hour);
        var trees = TallBurnable(host).Select(o => o.Id).ToList();
        host.WeatherSystem.Force(thunder, host.World.Tick);
        host.World.Run(hour);
        Assert.All(trees, id => Assert.True(host.Objects.TryGetExists(id), $"{id} was struck while wet"));

        // (1) The player's wish offers only weathers of the tables, never one only an extreme leads to.
        Assert.DoesNotContain(thunder, host.WeatherSystem.Weathers);
    }

    [Fact]
    public void ADryThunderstormComesAtItsHourAndEndsWithItsExtreme()
    {
        // Review GW1 #591 (2): it begins at start_hour and lasts exactly its hours - the table goes on at once after.
        var climate = ContractSerializer.Deserialize<ClimateSettings>(ContractSerializer.Serialize(Content.Value.WorldGen.Climates["temperate"]));
        var extremes = climate.Weather.Extremes!;
        var thunder = extremes.Weather.First(e => e.Weather == Content.Value.Fire!.Lightning!.Weather.First());
        thunder.ChancePermille = 1000;
        thunder.HoursMin = thunder.HoursMax;
        extremes.Weather = [thunder];
        extremes.HeatWaves = [];
        var world = new World(5, Content.Value.Calendar);
        var settings = world.Calendar.Settings;
        var weather = new WeatherSystem(climate, 0, settings.TicksPerHour, settings.HoursPerDay, 5);
        world.Add(weather);

        for (var i = 0; i < world.Calendar.TicksPerDay * 28 && weather.Current != thunder.Weather; i++)
        {
            world.Step();
        }

        Assert.Equal(thunder.Weather, weather.Current);
        Assert.Equal(thunder.StartHour ?? 0, (int)(world.Tick % world.Calendar.TicksPerDay / settings.TicksPerHour));
        world.Run((thunder.HoursMax * settings.TicksPerHour) - 1);
        Assert.Equal(thunder.Weather, weather.Current);
        world.Run(1);
        Assert.NotEqual(thunder.Weather, weather.Current);
    }

    private static List<WorldObject> TallBurnable(SimulationHost host)
    {
        var min = Content.Value.Fire!.Lightning!.MinHeightCm;
        return [.. host.Objects.All.Where(o => Content.Value.Objects.Contains(o.TypeId)
            && Content.Value.Objects.TrueProperties(o.TypeId).Contains(Content.Value.Fire!.FuelProperty)
            && (Content.Value.Objects.Params(o.TypeId).ShadeHeightCm ?? 0) >= min)];
    }
}

/// <summary>Test helper: whether an object of this id still exists.</summary>
internal static class WorldObjectsTestExtensions
{
    public static bool TryGetExists(this WorldObjects objects, string id) => objects.TryGet(id, out _);
}
