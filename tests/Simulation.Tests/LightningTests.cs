using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Objects;

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
    public void InAStormWithRainLightningNeverStrikes()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        var trees = TallBurnable(host).Select(o => o.Id).ToList();
        for (var hour = 0; hour < 12; hour++)
        {
            host.WeatherSystem!.Force("storm", host.World.Tick);
            host.World.Run(host.World.Calendar.Settings.TicksPerHour);
        }

        Assert.All(trees, id => Assert.True(host.Objects.TryGetExists(id), $"{id} was struck in the rain"));
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
