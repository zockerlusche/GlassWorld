using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

public sealed class WorldTests
{
    /// <summary>
    /// Golden hash: same on every machine and OS (Windows locally, Linux in CI). If this changes,
    /// either the state format changed on purpose (update the value in the same PR, with a reason)
    /// or determinism is broken.
    /// </summary>
    private const string GoldenHashSeed1After3Days = "5b056dfd496eec8fb52de413cc90fd6729b50ceadb5e6d4eb6289f35dc29619a";

    [Fact]
    public void SameSeedSameTicksSameHash()
    {
        var a = NewWorld(7);
        var b = NewWorld(7);
        a.Run(5_000);
        b.Run(5_000);
        Assert.Equal(a.StateHash(), b.StateHash());
    }

    [Fact]
    public void DifferentSeedDifferentHash() => Assert.NotEqual(NewWorld(1).StateHash(), NewWorld(2).StateHash());

    [Fact]
    public void HashChangesWithEveryTick()
    {
        var world = NewWorld(1);
        var before = world.StateHash();
        world.Step();
        Assert.NotEqual(before, world.StateHash());
    }

    [Fact]
    public void StreamCreationOrderDoesNotMatter()
    {
        var a = NewWorld(3);
        var b = NewWorld(3);
        a.Random.Get("x");
        a.Random.Get("y");
        b.Random.Get("y");
        b.Random.Get("x");
        Assert.Equal(a.StateHash(), b.StateHash());
    }

    [Fact]
    public void RestoredWorldContinuesWithSameHash()
    {
        var originalNoise = new NoiseSystem();
        var original = NewWorld(11).Add(originalNoise);
        original.Run(1_000);
        var restored = World.Restore(original.Seed, original.Calendar, original.Tick, original.Random.Snapshot())
            .Add(new NoiseSystem { Value = originalNoise.Value });
        Assert.Equal(original.StateHash(), restored.StateHash());

        original.Run(2_000);
        restored.Run(2_000);
        Assert.Equal(original.StateHash(), restored.StateHash());
    }

    [Fact]
    public void BoundariesAreReportedInOrder()
    {
        var world = NewWorld(1);
        var events = new List<(long Tick, TimeBoundary Kind)>();
        world.BoundaryReached += (w, kind) => events.Add((w.Tick, kind));
        world.Run(world.Calendar.TicksPerYear);

        Assert.Equal(28 * 24, events.Count(e => e.Kind == TimeBoundary.Hour));
        Assert.Equal(28, events.Count(e => e.Kind == TimeBoundary.Day));
        Assert.Equal(4, events.Count(e => e.Kind == TimeBoundary.Season));
        Assert.Single(events, e => e.Kind == TimeBoundary.Year);
        Assert.Equal([TimeBoundary.Hour, TimeBoundary.Day, TimeBoundary.Season, TimeBoundary.Year], events.Where(e => e.Tick == world.Calendar.TicksPerYear).Select(e => e.Kind));
    }

    [Fact]
    public void SystemsRunInRegistrationOrder()
    {
        var log = new List<string>();
        var world = NewWorld(1).Add(new LogSystem("b", log)).Add(new LogSystem("a", log));
        world.Step();
        Assert.Equal(["b", "a"], log);
        Assert.Throws<InvalidOperationException>(() => world.Add(new LogSystem("a", log)));
    }

    [Fact]
    public void GoldenHashIsStableAcrossMachines()
    {
        var world = NewWorld(1).Add(new NoiseSystem());
        world.Run(3 * world.Calendar.TicksPerDay);
        var actual = world.StateHash();
        Assert.True(actual == GoldenHashSeed1After3Days, $"golden hash changed: {actual}");
    }

    private static World NewWorld(ulong seed) => new(seed, TestData.Calendar());

    /// <summary>Uses random numbers and doubles so the golden hash covers RNG and float encoding.</summary>
    private sealed class NoiseSystem : IWorldSystem
    {
        public double Value { get; set; }

        public string Name => "noise";

        public void Advance(World world) => Value = (Value * 0.5) + world.Random.Get("noise").NextDouble();

        public void WriteState(StateHasher hasher) => hasher.Write(Value);
    }

    private sealed class LogSystem(string name, List<string> log) : IWorldSystem
    {
        public string Name => name;

        public void Advance(World world) => log.Add(name);

        public void WriteState(StateHasher hasher) => hasher.Write(name);
    }
}
