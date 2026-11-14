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
/// B9-1a (R1, D1049 v2): a tick of birth per inhabitant; age and life phase follow from it and the data; a change of
/// phase is reported; the phase decides which actions may be offered. Every number comes from the data.
/// </summary>
public sealed class AgingTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    private static LifeAging Data => Content.Value.Aging!;

    [Fact]
    public void StartInhabitantsAreBornBeforeTheWorldWithinTheDataAgesAndHaveTheirPhase()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 9, new MockDecisionPort(), "de");
        host.World.Step();
        var perYear = Data.DaysPerLifeYear * host.World.Calendar.TicksPerDay;
        foreach (var inhabitant in host.Inhabitants.All)
        {
            Assert.NotNull(inhabitant.BornTick);
            var years = (host.World.Tick - inhabitant.BornTick!.Value) / perYear;
            Assert.InRange(years, Data.StartAgeYears.Min - 1e-6, Data.StartAgeYears.Max + 1e-6);
            Assert.Equal(Data.Phases.Where(p => p.FromYears <= inhabitant.AgeYears).MaxBy(p => p.FromYears)!.Id, inhabitant.LifeStage);
        }

        // The same seed gives the same ages.
        var again = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 9, new MockDecisionPort(), "de");
        again.World.Step();
        Assert.Equal(host.Inhabitants.All.Select(i => i.BornTick), again.Inhabitants.All.Select(i => i.BornTick));
    }

    [Fact]
    public void GrowingIntoANewPhaseIsReportedAtTheDayBoundary()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 9, new MockDecisionPort(), "de");
        host.World.Step();
        var who = host.Inhabitants.All.First();
        var next = Data.Phases.OrderBy(p => p.FromYears).First(p => p.FromYears > who.AgeYears);
        var perYear = Data.DaysPerLifeYear * host.World.Calendar.TicksPerDay;

        // Born so that it reaches the next phase half a day from now.
        who.BornTick = host.World.Tick - (long)(next.FromYears * perYear) + (host.World.Calendar.TicksPerDay / 2);
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        host.World.Run(host.World.Calendar.TicksPerDay + 1);
        var changed = Assert.Single(events, e => e.Kind == AgingSystem.StageChanged && e.InhabitantId == who.Id);
        Assert.Equal(next.Id, changed.Detail);
        Assert.Equal(next.Id, who.LifeStage);
    }

    [Fact]
    public void TheTickOfBirthIsSavedAndHashed()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 9, new MockDecisionPort(), "de");
        host.World.Step();
        var path = Path.Combine(Path.GetTempPath(), "gw-aging-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var loaded = StateCodec.Restore(Factory(), SaveGameStore.Load(path));
            Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
            Assert.Equal(host.Inhabitants.All.Select(i => i.BornTick), loaded.Inhabitants.All.Select(i => i.BornTick));
            loaded.World.Step();
            Assert.Equal(host.Inhabitants.All.Select(i => i.LifeStage), loaded.Inhabitants.All.Select(i => i.LifeStage));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".sha256");
        }
    }

    [Fact]
    public void APhaseAllowsOnlyTheActionsTheDataGiveIt()
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 9, new MockDecisionPort(), "de");
        var narrow = new LifeAging
        {
            ContractVersion = Data.ContractVersion,
            DaysPerLifeYear = Data.DaysPerLifeYear,
            StartAgeYears = Data.StartAgeYears,
            Phases = [new LifePhase { Id = Data.Phases.First().Id, FromYears = 0, Actions = ["idle"] }],
        };
        var aging = new AgingSystem(narrow, host.Inhabitants, 9, host.World.Calendar.TicksPerDay);
        var model = Content.Value.InhabitantModel;
        var who = new Inhabitant("inh-000001", "female", 1, new Position(0, 0),
            model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
            model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal),
            model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal),
            model.WillToLive.Start);
        host.Inhabitants.Add(who);
        Assert.True(aging.Allows(who, "eat")); // no phase yet: everything
        aging.Advance(host.World);
        Assert.True(aging.Allows(who, "idle"));
        Assert.False(aging.Allows(who, "eat"));
    }

    private static SimulationHostFactory Factory()
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new MockDecisionPort(), "de");
    }
}
