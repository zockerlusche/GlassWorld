using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>B2-2: the simulation on the generated start island (worldmap system, start, save and load).</summary>
public sealed class GeneratedWorldTests : IDisposable
{
    private const string Preset = "start_island";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public GeneratedWorldTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void InhabitantsStartOnTheirPairsStartCellsAndObjectsOnTheirCells()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        var map = Assert.IsType<WorldMapSystem>(host.Map);
        Assert.Equal(map.Island.Starts.Count, host.Inhabitants.All.Count());
        foreach (var inhabitant in host.Inhabitants.All)
        {
            var (x, z) = CellOf(map, inhabitant.Position);
            var start = Assert.Single(map.Island.Starts, s => s.X == x && s.Z == z);
            Assert.Equal(inhabitant.Group, start.Pair + 1);
            Assert.NotEqual(MapCell.Blocked, map.Map.Cell(x, z).WalkCost);
        }

        Assert.Equal(map.Island.Objects.Count, host.Objects.All.Count());
        Assert.All(host.Objects.All, o => Assert.True(map.Map.Contains(CellOf(map, o.Position).X, CellOf(map, o.Position).Z)));
    }

    [Fact]
    public void TheIslandIsPartOfTheStateHash()
    {
        var a = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        var b = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        var other = SimulationHost.CreateGeneratedWorld(ContentDirectory, 8, new MockDecisionPort(), "de", Preset);
        Assert.Equal(a.World.StateHash(), b.World.StateHash());
        Assert.NotEqual(a.World.StateHash(), other.World.StateHash());
        Assert.Equal("worldmap", a.Map!.Name);
    }

    [Fact]
    public void SaveAndLoadRebuildTheSameIslandAndRunOnIdentically()
    {
        var original = SimulationHost.CreateGeneratedWorld(ContentDirectory, 42, new MockDecisionPort(), "de", Preset);
        original.World.Run(original.World.Calendar.TicksPerDay + 77);
        var path = Path.Combine(_directory, "island.db");
        SaveGameStore.Save(path, StateCodec.Capture(original));

        var state = SaveGameStore.Load(path);
        Assert.Equal(Preset, state.WorldPreset);
        var loaded = Restore(state, new ReplayDecisionPort(original.Decisions.Log));
        Assert.Equal(original.Map!.IslandHash, loaded.Map!.IslandHash);
        Assert.Equal(original.World.StateHash(), loaded.World.StateHash());

        original.World.Run(original.World.Calendar.TicksPerDay);
        var replay = Restore(SaveGameStore.Load(path), new ReplayDecisionPort(original.Decisions.Log));
        replay.World.Run(original.World.Calendar.TicksPerDay);
        Assert.Equal(original.World.StateHash(), replay.World.StateHash());
    }

    [Fact]
    public void PlaceholderSaveGamesLoadWithoutAnIsland()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 3, new MockDecisionPort(), "de");
        var path = Path.Combine(_directory, "placeholder.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var state = SaveGameStore.Load(path);
        Assert.Null(state.WorldPreset);
        Assert.Null(Restore(state, new MockDecisionPort()).Map);
    }

    [Fact]
    public void AnUnknownPresetIsRefused()
    {
        var e = Assert.Throws<InvalidDataException>(() => SimulationHost.CreateGeneratedWorld(ContentDirectory, 1, new MockDecisionPort(), "de", "no_such_island"));
        Assert.Contains("no_such_island", e.Message, StringComparison.Ordinal);
    }

    private static (int X, int Z) CellOf(WorldMapSystem map, Position position) =>
        ((int)Math.Floor(position.X * 100 / map.CellSizeCm), (int)Math.Floor(position.Z * 100 / map.CellSizeCm));

    private static SimulationHost Restore(SaveState state, IDecisionPort port)
    {
        var content = ContentSet.Load(ContentDirectory);
        var settings = Contracts.Validation.ContractSerializer.Deserialize<Contracts.DeciderSettings>(
            File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return StateCodec.Restore(new SimulationHostFactory(content, settings, port, "de"), state);
    }
}
