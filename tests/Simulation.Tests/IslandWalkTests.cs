using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>B2-4 core: appearance from the seed (R27), walking on the island (R10), saved islands (R11).</summary>
public sealed class IslandWalkTests : IDisposable
{
    private const string Preset = "start_island";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public IslandWalkTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void EveryStartInhabitantGetsAnAppearanceFromTheSeedWithinTheData()
    {
        var a = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 11, new MockDecisionPort(), "de");
        var b = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 11, new MockDecisionPort(), "de");
        var other = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 12, new MockDecisionPort(), "de");
        var data = a.Content.Appearance;
        foreach (var inhabitant in a.Inhabitants.All)
        {
            var look = Assert.IsType<Appearance>(inhabitant.Appearance);
            Assert.Equal(look, b.Inhabitants.Get(inhabitant.Id).Appearance! with { Shapes = look.Shapes });
            Assert.Equal(look.Shapes, b.Inhabitants.Get(inhabitant.Id).Appearance!.Shapes);
            Assert.InRange(look.HeightCm, data.HeightCm.Min, data.HeightCm.Max);
            Assert.InRange(look.BuildPermille, data.BuildPermille.Min, data.BuildPermille.Max);
            Assert.Contains(look.SkinTone, data.SkinTones);
            Assert.Contains(look.HairStyle, data.HairStyles);
            Assert.Contains(look.ClothingStyle, data.ClothingStyles);
            Assert.Contains(look.AgeStage, data.AgeStages.Select(s => s.Id));
            Assert.InRange(look.HeadScale, data.HeadScale.Min, data.HeadScale.Max);
            Assert.True(look.Shapes.Count <= 1);
        }

        // Different people in one world, different worlds from different seeds.
        Assert.True(a.Inhabitants.All.Select(i => i.Appearance).Distinct().Count() > 1);
        Assert.NotEqual(
            a.Inhabitants.All.Select(i => (i.Appearance!.HeightCm, i.Appearance.SkinTone, i.Appearance.HairColor)),
            other.Inhabitants.All.Select(i => (i.Appearance!.HeightCm, i.Appearance.SkinTone, i.Appearance.HairColor)));

        // Drawn from its own stream: the world's streams are untouched.
        Assert.DoesNotContain(a.World.Random.Names, n => n.EndsWith("/appearance", StringComparison.Ordinal));
    }

    [Fact]
    public void AppearanceIsSavedAndPartOfTheHash()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), "de");
        var path = Path.Combine(_directory, "look.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loaded = Restore(SaveGameStore.Load(path), new MockDecisionPort());
        Assert.All(host.Inhabitants.All, i => Assert.Equal(i.Appearance!.SkinTone, loaded.Inhabitants.Get(i.Id).Appearance!.SkinTone));
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());

        var before = host.World.StateHash();
        var first = host.Inhabitants.All.First();
        first.Appearance = first.Appearance! with { HairColor = "#000000" };
        Assert.NotEqual(before, host.World.StateHash());
    }

    [Fact]
    public void OnTheIslandInhabitantsNeverStepOntoBlockedCells()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        var map = host.Map!;
        for (var t = 0; t < host.World.Calendar.TicksPerDay * 2; t++)
        {
            host.World.Step();
            foreach (var inhabitant in host.Inhabitants.All)
            {
                var cell = map.CellOf(inhabitant.Position);
                Assert.NotNull(cell);
                Assert.NotEqual(MapCell.Blocked, map.Map.Cell(cell!.Value.X, cell.Value.Z).WalkCost);
            }
        }
    }

    [Fact]
    public void AnUnreachableDestinationCancelsTheActionWithTheReason()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        var map = host.Map!;
        var inhabitant = host.Inhabitants.All.First();

        // A deep sea cell at the map corner.
        var corner = map.CentreOf(0, 0);
        Assert.Equal(WaterKind.Salt, map.Map.Cell(0, 0).Water);
        var target = host.Objects.Spawn(host.Objects.All.First().TypeId, corner);
        var cancelled = new List<WorldEvent>();
        host.Inhabitants.Reported += e =>
        {
            if (e.Kind == InhabitantsSystem.ActionCancelled && e.TargetId == target.Id)
            {
                cancelled.Add(e);
            }
        };
        inhabitant.Activity = new Activity("inspect", target.Id, corner, null);
        host.World.Step();
        var e = Assert.Single(cancelled);
        Assert.Equal(InhabitantsSystem.Unreachable, e.Detail);
    }

    [Fact]
    public void ALoadedIslandWalksOnIdenticallyMidRoute()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 42, new MockDecisionPort(), "de", Preset);
        host.World.Run(host.World.Calendar.TicksPerDay / 3);
        Assert.Contains(host.Inhabitants.All, i => i.Activity?.Route is { Count: > 0 } || i.Activity is not null);
        var path = Path.Combine(_directory, "walk.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        host.World.Run(host.World.Calendar.TicksPerDay);
        var replay = Restore(SaveGameStore.Load(path), new ReplayDecisionPort(host.Decisions.Log));
        replay.World.Run(host.World.Calendar.TicksPerDay);
        Assert.Equal(host.World.StateHash(), replay.World.StateHash());
    }

    [Fact]
    public void ASaveLoadsItsOwnIslandAfterTheWorldDataChanged()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        host.World.Run(200);
        var path = Path.Combine(_directory, "island.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var state = SaveGameStore.Load(path);
        Assert.NotNull(state.IslandData);

        // A copy of the content where the generator would now build a different island.
        var changed = CopyContent();
        var presetFile = Path.Combine(changed, "world", "presets", "start_island.json");
        var text = File.ReadAllText(presetFile);
        File.WriteAllText(presetFile, text.Replace("\"amplitude\": 90", "\"amplitude\": 60", StringComparison.Ordinal));
        var changedContent = ContentSet.Load(changed);
        Assert.NotEqual(
            WorldGenerator.Generate(changedContent.WorldGen, changedContent.Objects, Preset, 7).Hash(),
            host.Map!.IslandHash);

        var loaded = StateCodec.Restore(new SimulationHostFactory(changedContent, Settings(), new MockDecisionPort(), "de"), state);
        Assert.Equal(host.Map.IslandHash, loaded.Map!.IslandHash);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
    }

    [Fact]
    public void AnOlderSaveWithoutIslandDataGeneratesTheIslandAgain()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        var withoutData = StateCodec.Capture(host) with { IslandData = null };
        var loaded = Restore(withoutData, new MockDecisionPort());
        Assert.Equal(host.Map!.IslandHash, loaded.Map!.IslandHash);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
    }

    [Fact]
    public void TheIslandIsAStoredBlobAndOlderBase64StandsStillLoad()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        var path = Path.Combine(_directory, "blob.db");
        var state = StateCodec.Capture(host);
        SaveGameStore.Save(path, state);
        Assert.Equal(state.IslandData, SaveGameStore.Load(path).IslandData);

        // A stand written by an earlier B2-4 build: the island as base64 in meta, no blob row.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM island; INSERT INTO meta VALUES('island_data', $data);";
            command.Parameters.AddWithValue("$data", Convert.ToBase64String(state.IslandData!));
            command.ExecuteNonQuery();
        }

        var older = SaveGameStore.Load(path);
        Assert.Equal(state.IslandData, older.IslandData);
        Assert.Equal(host.World.StateHash(), Restore(older, new MockDecisionPort()).World.StateHash());
    }

    [Fact]
    public void TheIslandCodecRoundTripsEveryCell()
    {
        var content = ContentSet.Load(ContentDirectory);
        var island = WorldGenerator.Generate(content.WorldGen, content.Objects, Preset, 2026);
        var (decoded, cellSize) = IslandCodec.Decode(IslandCodec.Encode(island, 100));
        Assert.Equal(100, cellSize);
        Assert.Equal(island.Hash(), decoded.Hash());
    }

    private static Contracts.DeciderSettings Settings() =>
        ContractSerializer.Deserialize<Contracts.DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));

    private static SimulationHost Restore(SaveState state, IDecisionPort port) =>
        StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), Settings(), port, "de"), state);

    private string CopyContent()
    {
        var target = Path.Combine(_directory, "content");
        foreach (var file in Directory.GetFiles(ContentDirectory, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(ContentDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        return target;
    }
}
