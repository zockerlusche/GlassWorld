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
/// B2687 K3 (spec Bogen 30 R7): fire is a thing of the world - it burns down after its type's minutes, goes
/// out in the rain and warms the air around it, all from the type data.
/// </summary>
public sealed class FireTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    [Fact]
    public void AFireBurnsDownAfterTheMinutesOfItsType()
    {
        var host = World();
        var fire = host.Objects.Spawn("fire", new Position(3, 0));
        var minutes = Content.Value.Objects.Params("fire").BurnMinutes!.Value;
        var ticks = (long)minutes * host.World.Calendar.Settings.TicksPerHour / 60;

        host.World.Run(ticks - 2);
        Assert.True(host.Objects.TryGet(fire.Id, out _));
        Assert.True(host.Fire.HasState);

        host.World.Run(3);
        Assert.False(host.Objects.TryGet(fire.Id, out _));
        Assert.False(host.Fire.HasState);
    }

    [Fact]
    public void ASparkLastsLongEnoughToBePutToTheGrass()
    {
        // The spark must outlast a combine try with it (K1: spark + dry grass), or the try fails as partner_gone.
        var spark = Content.Value.Objects.Params("spark").BurnMinutes;
        Assert.NotNull(spark);
        var combine = Content.Value.Actions.Timing("combine").DurationTicks;
        Assert.True((long)spark!.Value * World().World.Calendar.Settings.TicksPerHour / 60 > combine);
    }

    [Fact]
    public void AFireWarmsTheAirAroundItFallingOffToNothingAtItsRadius()
    {
        var host = World();
        host.Objects.Spawn("fire", new Position(0, 0));
        var fire = Content.Value.Objects.Params("fire");
        var heat = fire.HeatTenthC!.Value;
        var radius = fire.HeatRadiusCm!.Value / 100.0;

        Assert.Equal(heat, host.Fire.HeatAt(new Position(0, 0)));
        Assert.Equal((int)Math.Round(heat / 2.0, MidpointRounding.AwayFromZero), host.Fire.HeatAt(new Position(radius / 2, 0)));
        Assert.Equal(0, host.Fire.HeatAt(new Position(radius + 0.1, 0)));

        // Two fires add up; a thing without heat params adds nothing.
        host.Objects.Spawn("fire", new Position(0, 0));
        host.Objects.Spawn("flint", new Position(0, 0));
        Assert.Equal(2 * heat, host.Fire.HeatAt(new Position(0, 0)));
    }

    [Fact]
    public void RainPutsAFireOutAtOnce()
    {
        var host = World();
        var wet = new FireSystem(Content.Value.Objects, host.Objects, _ => null, host.World.Calendar.Settings.TicksPerHour)
        {
            AirAt = (_, _) => new Air(100, Precipitation: true),
        };
        var fire = host.Objects.Spawn("fire", new Position(3, 0));

        wet.Advance(host.World);
        Assert.False(host.Objects.TryGet(fire.Id, out _));
        Assert.False(wet.HasState);
    }

    [Fact]
    public void BurningThingsSurviveSavingAndLoadingWithTheSameHash()
    {
        var host = World();
        var fire = host.Objects.Spawn("fire", new Position(3, 0));
        host.World.Run(5);
        var until = Assert.Single(host.Fire.Burning).Value;

        var path = Path.Combine(Path.GetTempPath(), "gw-fire-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var loaded = StateCodec.Restore(Factory(), SaveGameStore.Load(path));
            Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
            Assert.Equal(until, Assert.Single(loaded.Fire.Burning).Value);
            Assert.True(loaded.Objects.TryGet(fire.Id, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static SimulationHost World() => SimulationHost.CreateEmpty(ContentDirectory, 17, new MockDecisionPort(), "de");

    private static SimulationHostFactory Factory()
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new MockDecisionPort(), "de");
    }
}
