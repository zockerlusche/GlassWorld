using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B5-11 (spec Bogen 30 R29/R30, D1084): rain wets an inhabitant out in the open and being wet feels colder; it
/// dries in dry weather; a shelter keeps the rain off; the sun warms where no tree's shade falls.
/// </summary>
public sealed class BodyTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    [Fact]
    public void RainWetsAnInhabitantOutInTheOpenItFeelsColderAndItDriesAgain()
    {
        var (host, inhabitant) = Island();
        var weather = host.WeatherSystem!;
        var body = host.Body!;
        var hour = host.World.Calendar.Settings.TicksPerHour;

        weather.Force("rain", host.World.Tick);
        host.World.Run(hour);
        Assert.Equal(1000, body.WetPermille(inhabitant.Id));
        var cool = Content.Value.Body!.Wetness.CoolTenthC;
        Assert.Equal(body.SunTenthC(host.World, inhabitant.Position) - cool, body.FeltTenthC(host.World, inhabitant));

        weather.Force("clear", host.World.Tick);
        host.World.Run(hour);
        Assert.InRange(body.WetPermille(inhabitant.Id), 0, 999);
        host.World.Run(12 * hour);
        Assert.Equal(0, body.WetPermille(inhabitant.Id));
    }

    [Fact]
    public void AShelterKeepsTheRainOff()
    {
        var (host, inhabitant) = Island();
        host.Objects.Spawn("rock_niche", new Position(inhabitant.Position.X + 1, inhabitant.Position.Z));
        host.WeatherSystem!.Force("rain", host.World.Tick);
        host.World.Run(host.World.Calendar.Settings.TicksPerHour);
        Assert.Equal(0, host.Body!.WetPermille(inhabitant.Id));
    }

    [Fact]
    public void ATreeCastsItsShadeAwayFromTheSunAndTheSunWarmsOnlyOutsideIt()
    {
        var (host, inhabitant) = Island();
        host.WeatherSystem!.Force("clear", host.World.Tick);

        // Noon: the shadow falls the way the sun table says.
        for (var i = 0; i < 24 * host.World.Calendar.Settings.TicksPerHour && host.World.Now.Hour != 12; i++)
        {
            host.World.Run(1);
        }

        var sun = Content.Value.Body!.Sun.Positions.Where(p => p.Season == host.World.Now.SeasonId && p.Hour <= 12).MaxBy(p => p.Hour)!;
        var at = new Position(inhabitant.Position.X + 60, inhabitant.Position.Z);
        var cleared = new List<WorldObject>();
        host.Objects.Within(at, 60, cleared);
        foreach (var thing in cleared)
        {
            host.Objects.Remove(thing.Id);
        }

        host.Objects.Spawn("deciduous_tree", at);
        var crownM = Content.Value.Objects.Params("deciduous_tree").CrownRadiusCm!.Value / 100.0;
        var lengthM = Content.Value.Objects.Params("deciduous_tree").ShadeHeightCm!.Value * sun.ShadowPermille / 1000 / 100.0;
        var step = crownM + Math.Min(lengthM, 1.0);
        var behind = new Position(at.X + (sun.TowardXPermille!.Value * step / 1000), at.Z + (sun.TowardZPermille!.Value * step / 1000));
        var before = new Position(at.X - (sun.TowardXPermille!.Value * step / 1000), at.Z - (sun.TowardZPermille!.Value * step / 1000));

        Assert.True(host.Body!.InShade(host.World, behind));
        Assert.False(host.Body.InShade(host.World, before));
        Assert.Equal(0, host.Body.SunTenthC(host.World, behind));
        Assert.True(host.Body.SunTenthC(host.World, before) > 0);
    }

    [Fact]
    public void AWetInhabitantStaysWetAcrossSavingAndLoading()
    {
        var (host, inhabitant) = Island();
        host.WeatherSystem!.Force("rain", host.World.Tick);
        host.World.Run(10);
        var wet = host.Body!.WetPermille(inhabitant.Id);
        Assert.True(wet > 0);

        var path = Path.Combine(Path.GetTempPath(), "gw-body-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
            var loaded = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new SilentPort(), "de"), SaveGameStore.Load(path));
            Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
            Assert.Equal(wet, loaded.Body!.WetPermille(inhabitant.Id));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ThePlaceholderWorldHasNoBodyWeather()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 7, new SilentPort(), "de");
        Assert.Null(host.Body);
    }

    /// <summary>A start island whose inhabitants get no answers (they stay where they are), the first one out in the open.</summary>
    private static (SimulationHost Host, Inhabitant Inhabitant) Island()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new SilentPort(), "de", "start_island");
        host.World.Run(1);
        var inhabitant = host.Inhabitants.All.First();
        var near = new List<WorldObject>();
        host.Objects.Within(inhabitant.Position, 5, near);
        foreach (var thing in near.Where(t => host.Objects.Catalog.TrueProperties(t.TypeId).Contains("sheltering")))
        {
            host.Objects.Remove(thing.Id);
        }

        return (host, inhabitant);
    }

    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
