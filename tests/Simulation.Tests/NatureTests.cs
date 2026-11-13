using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Nature;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1380 N1 (plan D989, review GW1 #23580, values B2700): the nature takt keeps integer growth sums per group by
/// season (and drought), so a used-up place grows back once the sum since then reaches regrow_hours x 1000; a
/// group with 1000 everywhere grows exactly by the clock and keeps nothing.
/// </summary>
public sealed class NatureTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");

    [Fact]
    public void AGroupThatGrowsAtFullSpeedGrowsByTheClockAndKeepsNothing()
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 3, new MockDecisionPort(), "de");
        var nature = new NatureSystem(Settings(1000), host.World.Calendar.Settings.TicksPerHour, null);
        Step(host, nature, 5 * nature.TaktTicks);

        Assert.False(nature.HasState);
        Assert.True(nature.Grown("apple", 10, 10 + 48, 48));
        Assert.False(nature.Grown("apple", 10, 10 + 47, 48));
    }

    [Fact]
    public void AtHalfSpeedAPlaceNeedsTwiceAsLong()
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 3, new MockDecisionPort(), "de");
        var nature = new NatureSystem(Settings(500), host.World.Calendar.Settings.TicksPerHour, null);
        var regrow = 3 * nature.TaktTicks;
        Step(host, nature, (2 * regrow) + nature.TaktTicks);

        Assert.True(nature.HasState);
        Assert.False(nature.Grown("apple", 0, regrow, regrow));
        Assert.False(nature.Grown("apple", 0, (2 * regrow) - 1, regrow));
        Assert.True(nature.Grown("apple", 0, 2 * regrow, regrow));

        // A type without a group grows by the clock.
        Assert.True(nature.Grown("dry_wood", 0, regrow, regrow));
    }

    [Fact]
    public void TheAnswerDoesNotDependOnWhenItIsAsked()
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 3, new MockDecisionPort(), "de");
        var nature = new NatureSystem(Settings(300), host.World.Calendar.Settings.TicksPerHour, null);
        var regrow = 2 * nature.TaktTicks;
        Step(host, nature, 4 * nature.TaktTicks);
        var early = nature.Grown("apple", 5, 5 + (regrow * 3), regrow);
        Step(host, nature, 6 * nature.TaktTicks);
        Assert.Equal(early, nature.Grown("apple", 5, 5 + (regrow * 3), regrow));
    }

    [Fact]
    public void TheSumsSurviveSavingAndLoading()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        host.World.Run(host.World.Calendar.TicksPerDay / 2);
        Assert.NotNull(host.Nature);
        Assert.True(host.Nature!.HasState);

        var path = Path.Combine(Path.GetTempPath(), "gw-nature-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
            var loaded = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new MockDecisionPort(), "de"), SaveGameStore.Load(path));
            Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
            Assert.Equal(host.Nature.Grown("apple", 0, host.World.Tick, 72), loaded.Nature!.Grown("apple", 0, loaded.World.Tick, 72));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static NatureSettings Settings(int permille) => new()
    {
        ContractVersion = "0.2",
        TaktMinutes = 60,
        Groups = [new NatureGroup { Id = "fruit", Seasons = [new SeasonGrowth { Season = "spring", Permille = permille }] }],
        Types = [new NatureType { Type = "apple", Group = "fruit" }],
    };

    private static void Step(SimulationHost host, NatureSystem nature, long ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            nature.Advance(host.World);
            host.World.Run(1);
        }

        nature.Advance(host.World);
    }
}
