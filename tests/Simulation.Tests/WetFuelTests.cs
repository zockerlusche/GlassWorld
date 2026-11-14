using GlassWorld.Contracts;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B5-10 (spec Bogen 30 R45 with R10, D1084): wet fuel does not burn in a combination either - a spark and
/// grass still wet from the rain give nothing (and no question to the judge); dried, they give a fire.
/// </summary>
public sealed class WetFuelTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    [Fact]
    public void ASparkAndWetGrassGiveNoFireButOnceDriedTheyDo()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new SilentPort(), "de", "start_island");
        var weather = host.WeatherSystem!;
        host.World.Run(1);

        // A wet takt on the island, then dry weather; the grass stays wet for the drying hours.
        weather.Force("rain", host.World.Tick);
        host.World.Run(host.Nature!.TaktTicks + 1);
        weather.Force("clear", host.World.Tick);
        host.World.Run(1);
        var data = Content.Value.Fire!;
        host.Fire.Settings = new FireSettings
        {
            ContractVersion = data.ContractVersion,
            FuelProperty = data.FuelProperty,
            Drying = [new FireDryingBand { BelowTenthC = null, Hours = 12 }],
            Remains = data.Remains,
        };

        var inhabitant = host.Inhabitants.All.First();
        var at = new Position(inhabitant.Position.X + 0.3, inhabitant.Position.Z);
        Assert.True(host.Fire.IsWet(host.World, at));
        var spark = host.Objects.Spawn("spark", at);
        var grass = host.Objects.Spawn("dry_grass", new Position(at.X + 0.2, at.Z));
        var fires = host.Objects.All.Count(o => o.TypeId == "fire");

        TryCombine(host, inhabitant, spark, grass);
        Assert.True(host.Objects.TryGet(grass.Id, out _));
        Assert.Equal(fires, host.Objects.All.Count(o => o.TypeId == "fire"));

        // Dried: a spark and grass make a fire.
        host.Fire.Settings = new FireSettings
        {
            ContractVersion = data.ContractVersion,
            FuelProperty = data.FuelProperty,
            Drying = [new FireDryingBand { BelowTenthC = null, Hours = 0 }],
            Remains = data.Remains,
        };

        // New things (the same try rests after it came to nothing, W1696).
        var spark2 = host.Objects.Spawn("spark", new Position(at.X, at.Z + 0.3));
        var grass2 = host.Objects.Spawn("dry_grass", new Position(at.X + 0.2, at.Z + 0.3));
        TryCombine(host, inhabitant, spark2, grass2);
        Assert.False(host.Objects.TryGet(grass2.Id, out _));
        Assert.Equal(fires + 1, host.Objects.All.Count(o => o.TypeId == "fire"));
    }

    private static void TryCombine(SimulationHost host, Inhabitant inhabitant, WorldObject target, WorldObject partner)
    {
        inhabitant.Activity = new Activity("combine", target.Id, target.Position, null, Partners: [partner.Id]);
        for (var i = 0; i < 600 && inhabitant.Activity is { Action: "combine" }; i++)
        {
            host.World.Run(1);
        }

        Assert.False(inhabitant.Activity is { Action: "combine" }, "the try did not end");
    }

    /// <summary>No answers: the decider never replaces the test's activity.</summary>
    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
