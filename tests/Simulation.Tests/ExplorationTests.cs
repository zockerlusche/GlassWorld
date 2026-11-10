using System.Text.Json;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Exploration;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Look;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B2-6 R14: cloud cover and exploring in the core. Seen cells per inhabitant, the player's view as the
/// union that is never covered again, the option to explore, and acceptance 3 of the B2 spec.
/// </summary>
public sealed class ExplorationTests : IDisposable
{
    private const string Preset = "start_island";
    private const string Curiosity = "curiosity";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public ExplorationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>The action the data sends to the frontier (no action id in the test either).</summary>
    private static string Explore => Content.Value.Actions.OfferedActions.Single(a => Content.Value.Actions.Timing(a).Frontier);

    /// <summary>
    /// Acceptance 3 of the B2 spec: a 28-day run with a fixed seed gives the same weather sequence and the
    /// same world hash at every day boundary on two runs.
    /// </summary>
    [Fact]
    public async Task ATwentyEightDayRunGivesTheSameWeatherAndTheSameHashAtEveryDayBoundaryTwice()
    {
        var runs = await Task.WhenAll(Task.Run(() => Days(2026, 28)), Task.Run(() => Days(2026, 28)));

        Assert.Equal(28, runs[0].Count);
        Assert.Equal(runs[0], runs[1]);

        // Not equal for nothing: the weather changes, the hash moves on and the cloud cover opens.
        Assert.True(runs[0].Select(d => d.Weather).Distinct().Count() > 1);
        Assert.Equal(28, runs[0].Select(d => d.Hash).Distinct().Count());
        Assert.True(runs[0][0].Revealed > 0);
        Assert.True(runs[0][^1].Revealed >= runs[0][0].Revealed);
    }

    [Fact]
    public void CellsAreRevealedOnlyWhereAnInhabitantSeesThemAndAreNeverCoveredAgain()
    {
        var host = Island(7);
        var exploration = host.Exploration!;
        var width = exploration.Width;
        var shown = new HashSet<int>();
        foreach (var cell in exploration.RevealedSince(0))
        {
            Assert.True(shown.Add(cell));
            Assert.True(InSight(host, cell));
        }

        var atStart = shown.Count;
        Assert.True(atStart > 0);
        Assert.Equal(atStart, host.Snapshot().ExploredVersion);
        for (var tick = 0; tick < 3000; tick++)
        {
            var before = exploration.Version;
            host.World.Step();
            var fresh = exploration.RevealedSince(before);
            Assert.Equal(exploration.Version - before, fresh.Length);
            foreach (var cell in fresh)
            {
                // New to the view, and somebody stands close enough to see it right now.
                Assert.True(shown.Add(cell));
                Assert.True(InSight(host, cell));
            }

            if (tick % 500 == 499)
            {
                AssertViewIs(exploration, shown);
            }
        }

        Assert.True(exploration.Version > atStart);
        Assert.Equal(exploration.Version, host.Snapshot().ExploredVersion);
        AssertViewIs(exploration, shown);

        // The player's view holds what every inhabitant has seen itself.
        foreach (var inhabitant in host.Inhabitants.All)
        {
            var own = Enumerable.Range(0, width * exploration.Depth).Where(i => exploration.HasSeen(inhabitant.Id, i % width, i / width)).ToList();
            Assert.Equal(own.Count, exploration.SeenCount(inhabitant.Id));
            Assert.All(own, i => Assert.Contains(i, shown));
        }
    }

    [Fact]
    public void AChildBornLaterStartsWithAnEmptyMapAndThePlayersViewKeepsEverything()
    {
        var host = Island(7);
        var exploration = host.Exploration!;
        host.World.Run(600);
        var before = exploration.RevealedSince(0);
        var parent = host.Inhabitants.All.First();
        var child = new Inhabitant(
            "inh-000099",
            parent.Sex,
            parent.Group,
            parent.Position,
            new Dictionary<string, double>(parent.Needs, StringComparer.Ordinal),
            new Dictionary<string, double>(parent.Moods, StringComparer.Ordinal),
            new Dictionary<string, double>(parent.Traits, StringComparer.Ordinal),
            parent.WillToLive);
        Assert.Equal(0, exploration.SeenCount(child.Id));
        host.Inhabitants.Add(child);

        host.World.Step();

        // The child has seen what lies around it, nothing of what the others saw before.
        var cell = host.Map!.CellOf(child.Position)!.Value;
        var range = exploration.SightRangeCells(cell.X, cell.Z);
        Assert.InRange(exploration.SeenCount(child.Id), 1, ((2 * range) + 1) * ((2 * range) + 1));
        Assert.True(exploration.SeenCount(child.Id) < exploration.Version);
        Assert.All(before, c => Assert.True(exploration.IsRevealed(c % exploration.Width, c / exploration.Width)));
        Assert.True(exploration.Version >= before.Length);
    }

    /// <summary>Acceptance 3 of the B2 spec: after save and load weather, cloud cover and look epoch are the same.</summary>
    [Fact]
    public void AfterSaveAndLoadWeatherCloudCoverAndLookEpochAreTheSame()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 42, new MockDecisionPort(), "de", Preset, null, new SimulationHostOptions(DebugCommands: true));
        var epoch = host.Content.LookEpochs.Epochs.Last().Id;
        Assert.True(host.Decisions.Submit(new PlayerCommand(LookEpochSystem.DebugSetLookEpoch, null) { TypeId = epoch }).Accepted);
        host.World.Run((2 * host.World.Calendar.TicksPerDay) + 137);
        Assert.Equal(epoch, host.LookEpoch.Current);
        Assert.True(host.Exploration!.Version > 0);

        var path = Path.Combine(_directory, "world.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loaded = Restore(SaveGameStore.Load(path));

        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(JsonSerializer.Serialize(host.WeatherSystem!.Capture(), Json), JsonSerializer.Serialize(loaded.WeatherSystem!.Capture(), Json));
        Assert.Equal(JsonSerializer.Serialize(host.Snapshot().Weather, Json), JsonSerializer.Serialize(loaded.Snapshot().Weather, Json));
        Assert.Equal(epoch, loaded.LookEpoch.Current);
        AssertSameCloudCover(host, loaded);

        // The loaded world goes on like the one that was never saved.
        host.World.Run(300);
        loaded.World.Run(300);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(host.WeatherSystem.Current, loaded.WeatherSystem.Current);
        AssertSameCloudCover(host, loaded);
    }

    [Fact]
    public void ASaveFromBeforeTheCloudCoverStartsWithWhatTheInhabitantsSeeWhenItIsLoaded()
    {
        var host = Island(7);
        host.World.Run(900);
        var path = Path.Combine(_directory, "v6.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE exploration; PRAGMA user_version = 6;";
            command.ExecuteNonQuery();
        }

        var older = SaveGameStore.Load(path);
        Assert.Null(older.Exploration);
        Assert.Equal(6, SaveGameStore.VersionOf(path));
        var loaded = Restore(older);
        var exploration = loaded.Exploration!;

        // Exactly the cells around the inhabitants, for each of them and for the player.
        var width = exploration.Width;
        var all = new SortedSet<int>();
        foreach (var inhabitant in loaded.Inhabitants.All)
        {
            var own = Enumerable.Range(0, width * exploration.Depth).Where(i => Sees(loaded, inhabitant, i)).ToList();
            Assert.Equal(own.Count, exploration.SeenCount(inhabitant.Id));
            Assert.All(own, i => Assert.True(exploration.HasSeen(inhabitant.Id, i % width, i / width)));
            all.UnionWith(own);
        }

        Assert.Equal(all, exploration.RevealedSince(0).Order());
        Assert.True(exploration.Version <= host.Exploration!.Version);

        // From then on it is saved like any other world.
        loaded.World.Run(60);
        SaveGameStore.Save(path, StateCodec.Capture(loaded));
        Assert.Equal(SaveGameStore.SchemaVersion, SaveGameStore.VersionOf(path));
        var again = Restore(SaveGameStore.Load(path));
        Assert.Equal(loaded.World.StateHash(), again.World.StateHash());
        AssertSameCloudCover(loaded, again);
    }

    [Fact]
    public void ACuriousInhabitantWalksToWhereTheUnseenLandBeginsAndSeesMore()
    {
        var port = new RecordingPort();
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, port, "de", Preset);
        var exploration = host.Exploration!;
        var map = host.Map!;
        var explorer = host.Inhabitants.All.First();
        var explore = Explore;
        var starts = 0;
        var finished = 0;
        var seenAtStart = 0;
        host.Inhabitants.Reported += e => finished += e.Kind == InhabitantsSystem.ActionFinished && e.InhabitantId == explorer.Id && e.Action == explore ? 1 : 0;
        for (var tick = 0; tick < host.World.Calendar.TicksPerDay && finished < 3; tick++)
        {
            foreach (var inhabitant in host.Inhabitants.All)
            {
                Sate(inhabitant, inhabitant == explorer ? 1.0 : 0.0);
            }

            var doing = explorer.Activity?.Action;
            var edge = exploration.Frontier(explorer);
            var seenBefore = exploration.SeenCount(explorer.Id);
            var finishedBefore = finished;
            host.World.Step();
            if (explorer.Activity is { } now && now.Action == explore && doing != explore)
            {
                // It heads for the nearest cell it has seen and can walk to that lies next to an unseen one.
                starts++;
                seenAtStart = seenBefore;
                var goal = map.CellOf(now.Destination)!.Value;
                Assert.Equal(edge, now.Destination);
                Assert.Equal(map.CentreOf(goal.X, goal.Z), now.Destination);
                Assert.True(map.Connected(explorer.Position, now.Destination));
            }

            if (finished > finishedBefore)
            {
                Assert.True(exploration.SeenCount(explorer.Id) > seenAtStart);
            }

            // Nobody without curiosity sets off to explore.
            Assert.DoesNotContain(host.Inhabitants.All, i => i != explorer && i.Activity?.Action == explore);
        }

        Assert.True(starts >= 3);
        Assert.Equal(3, finished);

        // The decider is offered the action like any other, to the curious one only.
        var offered = port.Requests.Where(r => r.Options.Any(o => o.Action == explore)).ToList();
        Assert.All(offered, r => Assert.Equal(explorer.Id, r.Inhabitant.Id));
    }

    [Fact]
    public void TheDeciderIsOfferedExploringWhileUnseenLandCanBeReached()
    {
        var port = new RecordingPort();
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, port, "de", Preset);
        var explore = Explore;
        for (var tick = 0; tick < 2 * host.World.Calendar.TicksPerDay && port.Requests.Count == 0; tick++)
        {
            foreach (var inhabitant in host.Inhabitants.All)
            {
                Sate(inhabitant, 1.0);
            }

            host.World.Step();
        }

        var request = port.Requests[0];
        Assert.NotNull(host.Exploration!.Frontier(host.Inhabitants.Get(request.Inhabitant.Id)));
        Assert.Contains(request.Options, o => o.Action == explore && o.Target is null);
    }

    [Fact]
    public void PressingNeedsComeBeforeExploring()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new RecordingPort(), "de", Preset);
        var brain = host.Content.Brain;
        var explore = Explore;
        var inhabitant = Sate(host.Inhabitants.All.First(), 1.0);
        var weight = brain.Weights.Considerations.Single(c => c.Action == explore).Weight;

        // Sated and curious: the weight of the data, driven by curiosity.
        Assert.Equal(weight, brain.Score(inhabitant, []).Single(o => o.Action == explore).Score, 9);
        Assert.True(brain.NeedSuggests(inhabitant, explore));
        inhabitant.Traits[Curiosity] = 0.5;
        Assert.Equal(weight * 0.5, brain.Score(inhabitant, []).Single(o => o.Action == explore).Score, 9);

        // A need that presses outbids it, and a neglected one silences it.
        inhabitant.Traits[Curiosity] = 1.0;
        inhabitant.Needs["thirst"] = 0.4;
        var scored = brain.Score(inhabitant, []);
        Assert.True(scored.Single(o => o.Action == explore).Score < weight);
        inhabitant.Needs["thirst"] = 0.05;
        Assert.Equal(0.0, brain.Score(inhabitant, []).Single(o => o.Action == explore).Score);
        Assert.False(brain.NeedSuggests(inhabitant, explore));

        // In the world: while everyone is parched nobody sets off to explore.
        for (var tick = 0; tick < 240; tick++)
        {
            foreach (var each in host.Inhabitants.All)
            {
                Sate(each, 1.0).Needs["thirst"] = 0.05;
            }

            host.World.Step();
            Assert.DoesNotContain(host.Inhabitants.All, i => i.Activity?.Action == explore);
        }
    }

    [Fact]
    public void AWorldWithoutAnIslandHasNoCloudCoverAndNobodyExplores()
    {
        var port = new RecordingPort();
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, port, "de");
        var explore = Explore;
        Assert.Null(host.Exploration);
        for (var tick = 0; tick < host.World.Calendar.TicksPerDay; tick++)
        {
            foreach (var inhabitant in host.Inhabitants.All)
            {
                inhabitant.Traits[Curiosity] = 1.0;
            }

            host.World.Step();
            Assert.DoesNotContain(host.Inhabitants.All, i => i.Activity?.Action == explore);
        }

        Assert.Null(host.Snapshot().ExploredVersion);
        Assert.NotEmpty(port.Requests);
        Assert.DoesNotContain(port.Requests, r => r.Options.Any(o => o.Action == explore));
        Assert.Null(StateCodec.Capture(host).Exploration);
    }

    [Fact]
    public void AViewHelpsStandingHigherThanTheSurroundingsSeesFurther()
    {
        var host = Island(7);
        var map = host.Map!;
        var preset = host.Content.WorldGen.Presets[Preset];

        // One source: the preset's exploration section, the same values the start check uses.
        var data = preset.Exploration;
        var exploration = host.Exploration!;
        var near = data.ViewRangeCells;
        var far = near + data.MaxHeightBonusCells;
        Assert.True(preset.Start.MinPairDistanceCells > far);
        var higher = 0;
        (int X, int Z, int Range) widest = (0, 0, 0);
        for (var z = 0; z < exploration.Depth; z++)
        {
            for (var x = 0; x < exploration.Width; x++)
            {
                var range = exploration.SightRangeCells(x, z);
                Assert.InRange(range, near, far);
                if (map.Map.Cell(x, z).Water == WaterKind.Salt)
                {
                    // The sea lies at sea level, never above its surroundings.
                    Assert.Equal(near, range);
                }

                higher += range > near ? 1 : 0;
                widest = range > widest.Range ? (x, z, range) : widest;
            }
        }

        // The island has heights, and the widest view is from a cell above the land around it.
        Assert.True(higher > 0);
        var around = new List<int>();
        var radius = data.SurroundingsRadiusCells;
        for (var dz = -radius; dz <= radius; dz++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if ((dx * dx) + (dz * dz) <= radius * radius && map.Map.Contains(widest.X + dx, widest.Z + dz))
                {
                    around.Add(Math.Max(map.Map.Cell(widest.X + dx, widest.Z + dz).Height, preset.Terrain.SeaLevel));
                }
            }
        }

        Assert.True(map.Map.Cell(widest.X, widest.Z).Height > around.Average());

        // Sight range and bonus are data: without a bonus everyone sees equally far, with a huge range everything.
        var level = new ExplorationSystem(Settings(data.ViewRangeCells, 0), map, host.Inhabitants, preset.Terrain.SeaLevel);
        Assert.Equal(near, level.SightRangeCells(widest.X, widest.Z));
        var everything = new ExplorationSystem(Settings(500, 0), map, host.Inhabitants, preset.Terrain.SeaLevel);
        Assert.False(everything.HasState);
        everything.Look();
        Assert.Equal(everything.Width * everything.Depth, everything.Version);
        Assert.All(host.Inhabitants.All, i => Assert.Null(everything.Frontier(i)));
    }

    [Fact]
    public void TheFrontierIsASeenWalkableCellNextToAnUnseenOne()
    {
        var host = Island(7);
        var map = host.Map!;
        var exploration = host.Exploration!;
        foreach (var inhabitant in host.Inhabitants.All)
        {
            var edge = exploration.Frontier(inhabitant);
            Assert.NotNull(edge);
            Assert.Equal(edge, exploration.Frontier(inhabitant));
            var cell = map.CellOf(edge.Value)!.Value;
            var own = map.CellOf(inhabitant.Position)!.Value;
            Assert.True(exploration.HasSeen(inhabitant.Id, cell.X, cell.Z));
            Assert.Equal(WaterKind.None, map.Map.Cell(cell.X, cell.Z).Water);
            Assert.NotEqual(0, map.AreaOf(cell.X, cell.Z));
            Assert.Equal(map.AreaOf(own.X, own.Z), map.AreaOf(cell.X, cell.Z));
            Assert.Contains(new[] { (1, 0), (0, -1), (-1, 0), (0, 1) }, d =>
                map.Map.Contains(cell.X + d.Item1, cell.Z + d.Item2) && !exploration.HasSeen(inhabitant.Id, cell.X + d.Item1, cell.Z + d.Item2));
            Assert.Equal(PathStatus.Found, map.Find(inhabitant.Position, edge.Value).Status);
        }
    }

    /// <summary>
    /// Review GW2 #187 (4): wadeable sea is walkable, but exploring does not lead into it. The walker stands
    /// in the shallow sea where a seen sea cell next to unseen sea is nearer than any dry one.
    /// </summary>
    [Fact]
    public void TheFrontierLiesOnDryLandEvenWhereTheShallowSeaIsNearer()
    {
        var host = Island(7);
        var map = host.Map!;
        var grid = map.Map;
        var preset = host.Content.WorldGen.Presets[Preset];
        var who = host.Inhabitants.All.First();
        var home = map.CellOf(who.Position)!.Value;
        var area = map.AreaOf(home.X, home.Z);
        bool Wades(int x, int z) => grid.Contains(x, z) && grid.Cell(x, z).Water == WaterKind.Salt && map.AreaOf(x, z) == area;
        bool Dry(int x, int z) => grid.Contains(x, z) && grid.Cell(x, z).Water == WaterKind.None && map.AreaOf(x, z) == area;

        // A spot in the sea: water two cells around, a wadeable diagonal (its end is the nearest seen cell
        // next to unseen sea, two steps away) and dry land three cells off along a wadeable line.
        (int X, int Z)? spot = null;
        for (var z = 4; z < grid.DepthCells - 4 && spot is null; z++)
        {
            for (var x = 4; x < grid.WidthCells - 4 && spot is null; x++)
            {
                var water = Enumerable.Range(-2, 5).All(dz => Enumerable.Range(-2, 5).All(dx => grid.Cell(x + dx, z + dz).Water != WaterKind.None));
                var diagonal = new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) }.Any(d => Wades(x + d.Item1, z + d.Item2) && Wades(x + (2 * d.Item1), z + (2 * d.Item2)));
                var shore = new[] { (1, 0), (0, -1), (-1, 0), (0, 1) }.Any(d => Wades(x + d.Item1, z + d.Item2) && Wades(x + (2 * d.Item1), z + (2 * d.Item2)) && Dry(x + (3 * d.Item1), z + (3 * d.Item2)));
                if (Wades(x, z) && water && diagonal && shore)
                {
                    spot = (x, z);
                }
            }
        }

        Assert.NotNull(spot);
        var narrow = new ExplorationSystem(Settings(3, 0), map, host.Inhabitants, preset.Terrain.SeaLevel);
        who.Position = map.CentreOf(spot.Value.X, spot.Value.Z);
        narrow.Look();

        var edge = narrow.Frontier(who);
        Assert.NotNull(edge);
        var cell = map.CellOf(edge.Value)!.Value;
        Assert.True(Dry(cell.X, cell.Z));
        Assert.True(narrow.HasSeen(who.Id, cell.X, cell.Z));
        Assert.Equal(3, Math.Max(Math.Abs(cell.X - spot.Value.X), Math.Abs(cell.Z - spot.Value.Z)));
    }

    /// <summary>
    /// Review GW2 #187 (6): an answer to explore that finds no unseen land any more is dropped, like an answer
    /// whose target is gone. Nobody is paid for standing around.
    /// </summary>
    [Fact]
    public void AnAnswerToExploreIsDroppedWhenNoUnseenLandIsLeft()
    {
        var port = new RecordingPort();
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, port, "de", Preset);
        var exploration = host.Exploration!;
        var map = host.Map!;
        var explore = Explore;
        for (var tick = 0; tick < 2 * host.World.Calendar.TicksPerDay && port.Requests.Count == 0; tick++)
        {
            foreach (var inhabitant in host.Inhabitants.All)
            {
                Sate(inhabitant, 1.0);
            }

            host.World.Step();
        }

        var request = port.Requests[0];
        var asker = host.Inhabitants.Get(request.Inhabitant.Id);
        var option = request.Options.Single(o => o.Action == explore);

        // Between the offer and the answer the asker gets to see the whole island.
        var at = asker.Position;
        var step = host.Content.WorldGen.Presets[Preset].Exploration.ViewRangeCells / 2;
        for (var z = 0; z < exploration.Depth; z += step)
        {
            for (var x = 0; x < exploration.Width; x += step)
            {
                asker.Position = map.CentreOf(x, z);
                exploration.Look();
            }
        }

        asker.Position = at;
        Assert.Null(exploration.Frontier(asker));
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        port.Answer(request, option.OptionId);
        for (var tick = 0; tick < 60; tick++)
        {
            host.World.Step();
            Assert.NotEqual(explore, asker.Activity?.Action);
            Assert.NotEqual(explore, asker.Planned?.Action);
        }

        Assert.Contains(host.Decisions.Log.Entries, e => e.Kind == DecisionLogKind.Discarded && e.RequestId == request.RequestId && e.Note == ReplayNotes.NothingToExplore);
        Assert.DoesNotContain(host.Decisions.Log.Entries, e => e.Kind == DecisionLogKind.Response && e.RequestId == request.RequestId);
        Assert.Contains(events, e => e.Kind == "question_failed" && e.InhabitantId == asker.Id && e.Detail == ReplayNotes.NothingToExplore);
        Assert.DoesNotContain(events, e => e.InhabitantId == asker.Id && e.Action == explore);
    }

    [Fact]
    public void ACellSetOnlyGrowsAndSurvivesItsBytes()
    {
        var set = new CellSet(70, 5);
        var added = new List<int>();
        set.AddRow(1, 60, 69, added);
        set.AddRow(2, 0, 3, added);
        Assert.Equal(14, set.Count);
        Assert.Equal([.. Enumerable.Range(130, 10), .. Enumerable.Range(140, 4)], added);
        Assert.True(set.Contains(69, 1));
        Assert.False(set.Contains(59, 1));
        Assert.False(set.Contains(70, 1));
        Assert.False(set.Contains(-1, 2));

        // Adding again reports nothing new.
        added.Clear();
        set.AddRow(1, 58, 62, added);
        Assert.Equal([128, 129], added);
        Assert.Equal(16, set.Count);

        var copy = CellSet.FromBytes(70, 5, set.ToBytes());
        var cells = new List<int>();
        copy.CopyTo(cells);
        Assert.Equal(16, copy.Count);
        Assert.Equal([.. Enumerable.Range(128, 16)], cells);
        Assert.Throws<InvalidDataException>(() => CellSet.FromBytes(64, 5, set.ToBytes()));
        var beyond = set.ToBytes();
        beyond[^1] = 0x80;
        Assert.Throws<InvalidDataException>(() => CellSet.FromBytes(70, 5, beyond));
    }

    private static SimulationHost Island(ulong seed) =>
        SimulationHost.CreateGeneratedWorld(ContentDirectory, seed, new MockDecisionPort(), "de", Preset);

    private static SimulationHost Restore(SaveState state) =>
        StateCodec.Restore(
            new SimulationHostFactory(
                Content.Value,
                ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json"))),
                new MockDecisionPort(),
                "de"),
            state);

    private static WorldPresetExploration Settings(int viewRangeCells, int bonusPer100Height)
    {
        var data = Content.Value.WorldGen.Presets[Preset].Exploration;
        return new WorldPresetExploration
        {
            ViewRangeCells = viewRangeCells,
            HeightBonusPer100Height = bonusPer100Height,
            SurroundingsRadiusCells = data.SurroundingsRadiusCells,
            MaxHeightBonusCells = data.MaxHeightBonusCells,
            RevealRealSeconds = data.RevealRealSeconds,
        };
    }

    private static Inhabitant Sate(Inhabitant inhabitant, double curiosity)
    {
        foreach (var need in inhabitant.Needs.Keys.ToList())
        {
            inhabitant.Needs[need] = 1.0;
        }

        inhabitant.Traits[Curiosity] = curiosity;
        return inhabitant;
    }

    /// <summary>Hash, weather of every hour and the size of the player's view, per day.</summary>
    private static List<(string Hash, string Weather, int Revealed)> Days(ulong seed, int days)
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, seed, MockDecisionPort.FromContent(ContentDirectory), "de", Preset);
        var result = new List<(string Hash, string Weather, int Revealed)>();
        var hours = new List<string>();
        host.World.BoundaryReached += (world, boundary) =>
        {
            if (boundary == TimeBoundary.Hour)
            {
                hours.Add(host.WeatherSystem!.Current);
            }

            if (boundary == TimeBoundary.Day)
            {
                result.Add((world.StateHash(), string.Join(',', hours), host.Exploration!.Version));
                hours.Clear();
            }
        };
        host.World.Run((long)days * host.World.Calendar.TicksPerDay);
        return result;
    }

    /// <summary>Whether a cell lies within the sight range of an inhabitant where it stands.</summary>
    private static bool Sees(SimulationHost host, Inhabitant inhabitant, int cell)
    {
        var exploration = host.Exploration!;
        var at = host.Map!.CellOf(inhabitant.Position)!.Value;
        var range = exploration.SightRangeCells(at.X, at.Z);
        var dx = (cell % exploration.Width) - at.X;
        var dz = (cell / exploration.Width) - at.Z;
        return (dx * dx) + (dz * dz) <= range * range;
    }

    private static bool InSight(SimulationHost host, int cell) => host.Inhabitants.All.Any(i => Sees(host, i, cell));

    /// <summary>The player's view is exactly the given cells: nothing was covered again, nothing else shown.</summary>
    private static void AssertViewIs(ExplorationSystem exploration, HashSet<int> shown)
    {
        Assert.Equal(shown.Count, exploration.Version);
        for (var i = 0; i < exploration.Width * exploration.Depth; i++)
        {
            Assert.Equal(shown.Contains(i), exploration.IsRevealed(i % exploration.Width, i / exploration.Width));
        }
    }

    private static void AssertSameCloudCover(SimulationHost a, SimulationHost b)
    {
        var (first, second) = (a.Exploration!, b.Exploration!);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(a.Snapshot().ExploredVersion, b.Snapshot().ExploredVersion);
        Assert.Equal(first.RevealedSince(0).Order(), second.RevealedSince(0).Order());
        Assert.Equal(a.Inhabitants.All.Select(i => i.Id), b.Inhabitants.All.Select(i => i.Id));
        foreach (var inhabitant in a.Inhabitants.All)
        {
            Assert.Equal(first.SeenCount(inhabitant.Id), second.SeenCount(inhabitant.Id));
            for (var i = 0; i < first.Width * first.Depth; i++)
            {
                Assert.Equal(first.HasSeen(inhabitant.Id, i % first.Width, i / first.Width), second.HasSeen(inhabitant.Id, i % first.Width, i / first.Width));
            }
        }
    }

    /// <summary>A decider that keeps what it was asked and answers only what a test tells it to.</summary>
    private sealed class RecordingPort : IDecisionPort
    {
        private readonly List<PortMessage> _ready = [];

        public List<DecisionRequest> Requests { get; } = [];

        public void Send(string requestId, string requestJson) => Requests.Add(ContractSerializer.Deserialize<DecisionRequest>(requestJson));

        /// <summary>Answers a request with one of its options at the next tick.</summary>
        public void Answer(DecisionRequest request, string optionId) =>
            _ready.Add(new PortMessage(
                request.RequestId,
                ContractSerializer.Serialize(new DecisionResponse
                {
                    ContractVersion = request.ContractVersion,
                    RequestId = request.RequestId,
                    Tick = request.Tick,
                    OptionId = optionId,
                    Expression = null,
                    Rationale = "test",
                    Thought = null,
                    Meta = new ResponseMeta { Source = ResponseMetaSource.Mock, Model = "mock", LatencyMs = 0, Attempts = 1 },
                }),
                IsError: false));

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            var messages = _ready.ToList();
            _ready.Clear();
            return messages;
        }
    }
}
