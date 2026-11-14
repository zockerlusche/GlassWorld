using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1697 (B2736, Marcel W2107): a fire follows its fuel - it kindles, burns full, fades, lies as embers that still
/// give a heat level, and leaves ash; heat scales with its strength; fuel on embers kindles it again.
/// </summary>
public sealed class FireCourseTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    [Fact]
    public void AFireKindlesBurnsFullFadesGlowsAsEmbersAndLeavesAsh()
    {
        var (host, at) = Island();
        var course = Content.Value.Fire!.Course!;
        var perMinute = host.World.Calendar.Settings.TicksPerHour / 60;
        var burn = (long)Content.Value.Objects.Params("fire").BurnMinutes!.Value * perMinute;
        var fire = host.Objects.Spawn("fire", at);
        var levels = Content.Value.Processes!.HeatLevels.ToList();

        // Kindling: weaker than full at first.
        host.World.Run(1);
        Assert.InRange(host.Fire.StrengthPermille(fire.Id), course.EmbersPermille, 999);

        // Full in the middle of its fuel, with the flame's heat level.
        host.World.Run(burn / 2);
        Assert.Equal(1000, host.Fire.StrengthPermille(fire.Id));
        var full = host.Fire.HeatAt(at);
        Assert.Equal(levels.IndexOf(Content.Value.Objects.Params("fire").HeatLevel!), host.Fire.HeatLevelOf(fire, levels));

        // Embers once the fuel is used up: no flame, less heat, the embers' heat level.
        host.World.Run((burn / 2) + 1);
        Assert.True(host.Fire.IsEmbers(fire.Id));
        Assert.Equal(course.EmbersPermille, host.Fire.StrengthPermille(fire.Id));
        Assert.True(host.Fire.HeatAt(at) < full);
        Assert.Equal(levels.IndexOf(course.EmbersHeatLevel), host.Fire.HeatLevelOf(fire, levels));

        // Burnt down after the embers: ash remains.
        host.World.Run(((long)course.EmbersMinutes * perMinute) + 1);
        Assert.False(host.Objects.TryGet(fire.Id, out _));
        Assert.Contains(host.Objects.All, o => o.TypeId == "ash" && o.Position == at);
    }

    [Fact]
    public void FuelOnEmbersKindlesTheFireAgain()
    {
        var (host, at) = Island();
        var perMinute = host.World.Calendar.Settings.TicksPerHour / 60;
        var burn = (long)Content.Value.Objects.Params("fire").BurnMinutes!.Value * perMinute;
        var fire = host.Objects.Spawn("fire", at);
        host.World.Run(burn + 2);
        Assert.True(host.Fire.IsEmbers(fire.Id));

        var wood = host.Objects.Spawn("dry_grass", new Position(at.X + 0.3, at.Z));
        Assert.True(host.Fire.Feed(host.World, fire.Id, wood.Id));
        host.World.Run(1);
        Assert.False(host.Fire.IsEmbers(fire.Id));
        Assert.True(host.Fire.StrengthPermille(fire.Id) < 1000, "kindling again rises from the embers");
    }

    [Fact]
    public void WithoutACourseAFireIsFullUntilItIsGone()
    {
        // Worlds without fire data (placeholder) keep the fire on or off, as before.
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 7, new MockDecisionPort(), "de");
        var fire = host.Objects.Spawn("fire", new Position(3, 0));
        host.World.Run(1);
        Assert.Equal(1000, host.Fire.StrengthPermille(fire.Id));
        Assert.False(host.Fire.IsEmbers(fire.Id));
    }

    /// <summary>A start island in clear weather and a cleared place far from the inhabitants.</summary>
    private static (SimulationHost Host, Position At) Island()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        host.WeatherSystem!.Force("clear", host.World.Tick);
        host.World.Run(1);
        var first = host.Inhabitants.All.First().Position;
        var at = new Position(first.X + 40, first.Z);
        var near = new List<WorldObject>();
        host.Objects.Within(at, 6, near);
        foreach (var thing in near)
        {
            host.Objects.Remove(thing.Id);
        }

        return (host, at);
    }
}
