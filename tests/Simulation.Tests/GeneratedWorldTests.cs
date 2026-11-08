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
        Assert.Equal(original.Map!.IslandHash, state.IslandHash);
        var loaded = Restore(state, new ReplayDecisionPort(original.Decisions.Log));
        Assert.Equal(original.Map!.IslandHash, loaded.Map!.IslandHash);
        Assert.Equal(original.World.StateHash(), loaded.World.StateHash());

        original.World.Run(original.World.Calendar.TicksPerDay);
        var replay = Restore(SaveGameStore.Load(path), new ReplayDecisionPort(original.Decisions.Log));
        replay.World.Run(original.World.Calendar.TicksPerDay);
        Assert.Equal(original.World.StateHash(), replay.World.StateHash());
    }

    [Fact]
    public void ASaveWhoseIslandDiffersFromTheCurrentDataIsRefused()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 42, new MockDecisionPort(), "de", Preset);
        var state = StateCodec.Capture(host) with { IslandHash = new string('0', 64) };
        var e = Assert.Throws<InvalidDataException>(() => Restore(state, new MockDecisionPort()));
        Assert.Contains("changed since the save", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AUsedUpObjectGrowsBackAtItsPlaceAfterTheRegrowTimeFromTheData()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        var map = host.Map!;
        Assert.NotEmpty(map.Occurrences);
        var place = map.Occurrences[0];
        var hours = host.Content.WorldGen.Spawns.Spawns.First(r => r.ObjectType == place.TypeId).RegrowHours!.Value;
        var regrowTicks = (long)hours * host.World.Calendar.Settings.TicksPerHour;
        Assert.True(host.Objects.Remove(place.ObjectId));

        host.World.Step();
        var gone = map.Occurrences[0];
        Assert.NotNull(gone.GoneSinceTick);

        while (host.World.Tick - gone.GoneSinceTick!.Value < regrowTicks)
        {
            Assert.Null(map.Occurrences[0].GoneSinceTick is null ? "grew back too early" : null);
            host.World.Step();
        }

        host.World.Step();
        var grown = map.Occurrences[0];
        Assert.Null(grown.GoneSinceTick);
        Assert.NotEqual(place.ObjectId, grown.ObjectId);
        Assert.True(host.Objects.TryGet(grown.ObjectId, out var obj));
        Assert.Equal(place.TypeId, obj.TypeId);
        Assert.Equal(map.CentreOf(place.X, place.Z), obj.Position);

        // Regrowth places exactly one thing at the occurrence's place (no double placing). The total of the
        // type is not checked: inhabitants eat and drink other ones of it during the regrow time.
        Assert.Single(host.Objects.All, o => o.TypeId == place.TypeId && o.Position == obj.Position);
    }

    [Fact]
    public void SaveAndLoadWhileSomethingRegrowsGiveTheSameHash()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        host.Objects.Remove(host.Map!.Occurrences[0].ObjectId);
        host.World.Run(host.World.Calendar.Settings.TicksPerHour * 3);
        var path = Path.Combine(_directory, "regrow.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loaded = Restore(SaveGameStore.Load(path), new ReplayDecisionPort(host.Decisions.Log));
        Assert.Equal(host.Map.Occurrences, loaded.Map!.Occurrences);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        // Run on past the regrowth; the replay of the later answers comes from the original's log.
        host.World.Run(host.World.Calendar.TicksPerDay * 4);
        var replay = Restore(SaveGameStore.Load(path), new ReplayDecisionPort(host.Decisions.Log));
        replay.World.Run(host.World.Calendar.TicksPerDay * 4);
        Assert.Equal(host.World.StateHash(), replay.World.StateHash());
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
