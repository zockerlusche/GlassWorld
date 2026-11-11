using GlassWorld.Contracts;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A world that starts with more inhabitants than the data lists (measurements, B2 acceptance 3: the step
/// with 100 inhabitants), and the probe that watches the parts of a step.
/// </summary>
public sealed class StartPopulationTests
{
    private const string Preset = "start_island";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");

    private static StartModel TwoPairs() => new()
    {
        Inhabitants =
        [
            new StartInhabitant { Sex = StartInhabitantSex.Female, Group = 1 },
            new StartInhabitant { Sex = StartInhabitantSex.Male, Group = 1 },
            new StartInhabitant { Sex = StartInhabitantSex.Female, Group = 2 },
            new StartInhabitant { Sex = StartInhabitantSex.Male, Group = 2 },
        ],
    };

    [Fact]
    public void WithoutACountTheStartersAreThoseOfTheData()
    {
        var start = TwoPairs();
        Assert.Equal(start.Inhabitants, InhabitantsSystem.StartersFor(start, null));
        Assert.Equal(start.Inhabitants, InhabitantsSystem.StartersFor(start, 4));
        Assert.Equal(start.Inhabitants.Take(2), InhabitantsSystem.StartersFor(start, 2));
    }

    [Fact]
    public void FurtherStartersRepeatTheDataAsGroupsOfTheirOwn()
    {
        var starters = InhabitantsSystem.StartersFor(TwoPairs(), 10);
        Assert.Equal([1, 1, 2, 2, 3, 3, 4, 4, 5, 5], starters.Select(s => s.Group));
        Assert.Equal(
            Enumerable.Repeat(new[] { StartInhabitantSex.Female, StartInhabitantSex.Male }, 5).SelectMany(pair => pair),
            starters.Select(s => s.Sex));
        Assert.Throws<ArgumentOutOfRangeException>(() => InhabitantsSystem.StartersFor(TwoPairs(), -1));
        Assert.Throws<InvalidDataException>(() => InhabitantsSystem.StartersFor(new StartModel(), 2));
    }

    [Fact]
    public void AWorldWithTheDatasCountIsTheWorldWithoutACount()
    {
        var usual = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        var counted = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset, inhabitants: usual.Inhabitants.All.Count());
        Assert.Equal(usual.World.StateHash(), counted.World.StateHash());
    }

    [Fact]
    public void AHundredInhabitantsStartAsPairsOnFreeDryGroundAroundTheStartPlaces()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset, inhabitants: 100);
        var map = Assert.IsType<WorldMapSystem>(host.Map);
        var everyone = host.Inhabitants.All.ToList();
        Assert.Equal(100, everyone.Count);
        Assert.Equal(Enumerable.Range(1, 100).Select(n => $"inh-{n:000000}"), everyone.Select(i => i.Id));

        // Fifty pairs, a woman and a man each, who have met each other and nobody else (as the data's pairs).
        Assert.All(everyone.GroupBy(i => i.Group), pair => Assert.Equal(["female", "male"], pair.Select(i => i.Sex)));
        Assert.Equal(Enumerable.Range(1, 50), everyone.Select(i => i.Group).Distinct());
        Assert.All(everyone, i => Assert.Equal(everyone.Where(o => o.Group == i.Group && o != i).Select(o => o.Id), i.Met));
        Assert.All(everyone, i => Assert.NotNull(i.Name));

        // Everybody stands on a dry cell of its start place's walkable area, nobody on another's cell or on a thing.
        var cells = everyone.Select(i => map.CellOf(i.Position)!.Value).ToList();
        Assert.Equal(cells.Count, cells.Distinct().Count());
        var things = map.Island.Objects.Select(o => (o.X, o.Z)).ToHashSet();
        for (var n = 0; n < everyone.Count; n++)
        {
            var home = map.Island.Starts.First(s => s.Pair == (everyone[n].Group - 1) % 2);
            Assert.Equal(map.AreaOf(home.X, home.Z), map.AreaOf(cells[n].X, cells[n].Z));
            Assert.Equal(WaterKind.None, map.Map.Cell(cells[n].X, cells[n].Z).Water);
            Assert.True(n < 4 || !things.Contains(cells[n]));
        }

        // The data's four stand where they always stand.
        var usual = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        Assert.Equal(usual.Inhabitants.All.Select(i => i.Position), everyone.Take(4).Select(i => i.Position));
    }

    [Fact]
    public void ABiggerStartIsTheSameInEveryRun()
    {
        string Run()
        {
            var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 42, new MockDecisionPort(), "de", Preset, inhabitants: 24);
            host.World.Run(90);
            return host.World.StateHash();
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void TheProbeHearsEverySystemInOrderThenTheBoundariesAndChangesNothing()
    {
        var plain = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        var watched = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        var probe = new Recorder();
        watched.World.Probe = probe;
        watched.World.Step();
        plain.World.Step();

        // Top-level parts: the systems in the order they run, then the boundary listeners.
        Assert.Equal(watched.World.SystemNames.Append(World.BoundariesPart), probe.TopLevel);
        Assert.Contains("inhabitants", watched.World.SystemNames);

        // Parts a system names begin and end inside it, and their names start with the system's - except the
        // frontier search, one search of the exploration that the routine and a request both ask for, so it
        // lies inside whichever system asks.
        Assert.Contains(probe.Nested, n => n.Part == InhabitantsSystem.PartAct && n.Inside == "inhabitants");
        Assert.All(
            probe.Nested.Where(n => n.Part != Simulation.Exploration.ExplorationSystem.PartFrontier),
            n => Assert.StartsWith(n.Outermost + "/", n.Part, StringComparison.Ordinal));
        Assert.Equal(0, probe.Depth);

        watched.World.Run(200);
        plain.World.Run(200);
        Assert.Equal(0, probe.Depth);
        Assert.Equal(plain.World.StateHash(), watched.World.StateHash());
    }

    private sealed class Recorder : IStepProbe
    {
        private readonly Stack<string> _open = new();

        public List<string> TopLevel { get; } = [];

        public List<(string Part, string Inside, string Outermost)> Nested { get; } = [];

        public int Depth => _open.Count;

        public void Enter(string part)
        {
            if (_open.Count == 0)
            {
                TopLevel.Add(part);
            }
            else
            {
                Nested.Add((part, _open.Peek(), _open.Last()));
            }

            _open.Push(part);
        }

        public void Leave(string part) => Assert.Equal(part, _open.Pop());
    }
}
