using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B2-5 R15: the tides follow from the world tick alone (whole numbers, nothing saved), the snapshot shows
/// the level, and the shallow sea costs more or less to walk with the tide without ever being closed.
/// </summary>
public sealed class TideTests : IDisposable
{
    private const string Preset = "start_island";
    private const int Shallow = 8;
    private const int ShallowCost = 200;
    private const int FloodedCost = 600;
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private static readonly string[] Biomes = ["land"];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public TideTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>High water at tick 0, mean at a quarter, low at half the period.</summary>
    private static Tides Wave(int amplitude = 3, int period = 1000) => new(new WorldPresetTides
    {
        Amplitude = amplitude,
        PeriodTicks = period,
        Curve = [Point(0, 1000), Point(250, 0), Point(500, -1000), Point(750, 0), Point(1000, 1000)],
    });

    private static WorldPresetTidePoint Point(int phase, int level) => new() { PhasePermille = phase, LevelPermille = level };

    private static WorldPresetTides PresetTides() => Content.Value.WorldGen.Presets[Preset].Tides;

    [Fact]
    public void TheLevelFollowsTheCurveWithLinearInterpolationInWholeNumbers()
    {
        var tides = Wave();
        Assert.Equal([1000, 500, 0, -500, -1000, -500, 0, 500], new long[] { 0, 125, 250, 375, 500, 625, 750, 875 }.Select(tides.LevelPermilleAt));
        Assert.Equal([30, 15, 0, -15, -30, -15, 0, 15], new long[] { 0, 125, 250, 375, 500, 625, 750, 875 }.Select(tides.LevelTenthsAt));

        // Whole units for walking: rounded to the nearest, a half away from zero (1.5 → 2, −1.5 → −2, 0.4 → 0).
        Assert.Equal([3, 2, 0, -2, -3], new long[] { 0, 125, 250, 375, 500 }.Select(tides.LevelUnitsAt));
        Assert.Equal([0, 0, 1, -1], new long[] { 240, 260, 208, 292 }.Select(tides.LevelUnitsAt));

        // One tick is one thousandth of this cycle: 4 permille of level per tick on the falling flank.
        Assert.Equal(996, tides.LevelPermilleAt(1));
        Assert.Equal(-996, tides.LevelPermilleAt(499));

        // Two straight lines: from low to high over the first half of the cycle and back over the second.
        var ramp = new Tides(new WorldPresetTides { Amplitude = 10, PeriodTicks = 2000, Curve = [Point(0, -1000), Point(500, 1000), Point(1000, -1000)] });
        Assert.All(Enumerable.Range(0, 1001), t => Assert.Equal(-1000 + (2 * t), ramp.LevelPermilleAt(t)));
        Assert.All(Enumerable.Range(1000, 1000), t => Assert.Equal(1000 - (2 * (t - 1000)), ramp.LevelPermilleAt(t)));
    }

    [Fact]
    public void TheTideRepeatsEveryPeriodAndStaysWithinTheAmplitude()
    {
        var tides = Wave(amplitude: 7, period: 745);
        for (var tick = 0L; tick < 745; tick++)
        {
            Assert.Equal(tides.LevelTenthsAt(tick), tides.LevelTenthsAt(tick + 745));
            Assert.Equal(tides.LevelTenthsAt(tick), tides.LevelTenthsAt(tick + (745L * 1_000_000)));
            Assert.InRange(tides.LevelTenthsAt(tick), -70, 70);
            Assert.InRange(tides.LevelUnitsAt(tick), -7, 7);

            // Falling and rising flank are the same line (whole numbers: within one permille).
            Assert.InRange(tides.LevelPermilleAt(tick) - tides.LevelPermilleAt(745 - tick), -1, 1);
        }

        // The period is odd, so no tick hits the lowest point exactly; in whole units the low water is reached.
        Assert.Equal(70, tides.LevelTenthsAt(0));
        Assert.Equal(-69, tides.LevelTenthsAt(372));
        Assert.Equal(-7, tides.LevelUnitsAt(372));
    }

    [Fact]
    public void TheReadingSaysWhetherTheWaterRises()
    {
        var tides = Wave();
        Assert.Equal(new TideReading(15, 500, 3, 1000, false), tides.Read(125));
        Assert.Equal(new TideReading(-15, -500, 3, 1000, true), tides.Read(625));
        Assert.False(tides.Read(0).Rising);
        Assert.True(tides.Read(500).Rising);
    }

    [Fact]
    public void AmplitudeZeroTurnsTheTidesOff()
    {
        var off = Wave(amplitude: 0);
        Assert.False(off.Active);
        Assert.All(Enumerable.Range(0, 2000), t => Assert.Equal(new TideReading(0, 0, 0, 1000, false), off.Read(t)));
    }

    [Fact]
    public void ACurveThatDoesNotRiseFromZeroToAThousandIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => new Tides(new WorldPresetTides { Amplitude = 1, PeriodTicks = 10, Curve = [Point(0, 0), Point(900, 0)] }));
        Assert.Throws<InvalidDataException>(() => new Tides(new WorldPresetTides { Amplitude = 1, PeriodTicks = 10, Curve = [Point(0, 0), Point(600, 5), Point(600, 9), Point(1000, 0)] }));
    }

    [Fact]
    public void ACurveThatDoesNotEndWhereItStartsIsRefusedSoTheWaterNeverJumps()
    {
        // Review GW2 #182: phase 1000 is phase 0 of the next cycle.
        var open = Assert.Throws<InvalidDataException>(() => new Tides(new WorldPresetTides { Amplitude = 3, PeriodTicks = 100, Curve = [Point(0, 1000), Point(500, -1000), Point(1000, 500)] }));
        Assert.Contains("end at the level it starts with", open.Message, StringComparison.Ordinal);

        // The curve of the data closes, and from the last tick of a cycle to the first of the next the
        // level moves no more than between any other two ticks.
        var data = PresetTides();
        Assert.Equal(data.Curve.First().LevelPermille, data.Curve.Last().LevelPermille);
        var tides = new Tides(data);
        var steps = Enumerable.Range(0, data.PeriodTicks * 2).Select(t => Math.Abs(tides.LevelPermilleAt(t + 1) - tides.LevelPermilleAt(t))).ToList();
        Assert.Equal(steps.Take(data.PeriodTicks - 1).Max(), steps.Max());
    }

    [Fact]
    public void TheDataOfTheStartIslandGiveATideWithinItsAmplitude()
    {
        var data = PresetTides();
        var tides = new Tides(data);
        Assert.True(tides.Active);
        var levels = Enumerable.Range(0, data.PeriodTicks).Select(t => tides.LevelTenthsAt(t)).ToList();
        Assert.InRange(levels.Max(), (data.Amplitude * 10) - 1, data.Amplitude * 10);
        Assert.InRange(levels.Min(), -data.Amplitude * 10, -(data.Amplitude * 10) + 1);
        Assert.Contains(levels, l => l == 0);

        // In whole units the walk costs see the full range, high and low water alike.
        var units = Enumerable.Range(0, data.PeriodTicks).Select(t => tides.LevelUnitsAt(t)).ToList();
        Assert.Equal((-data.Amplitude, data.Amplitude), (units.Min(), units.Max()));
    }

    /// <summary>
    /// A beach strip: x = 0 land, x = 1..10 sea of depth x (1..8 wadeable, 9 and 10 too deep), row 2 a
    /// fresh pond of depth 2 at x = 1.
    /// </summary>
    private static WorldMap Beach()
    {
        var builder = new WorldMapBuilder(1, 11, 3, Biomes);
        for (var z = 0; z < 3; z++)
        {
            for (var x = 0; x < 11; x++)
            {
                var cell = x == 0 ? new MapCell(21, WaterKind.None, 0, -1, 400, 150, 0, 1000)
                    : z == 2 && x == 1 ? new MapCell(18, WaterKind.Fresh, 2, -1, 400, 150, 0, 1000 + ShallowCost)
                    : new MapCell(20 - x, WaterKind.Salt, x, -1, 400, 150, 0, x <= Shallow ? 1000 + ShallowCost : MapCell.Blocked);
                builder.Set(x, z, cell);
            }
        }

        return builder.Build();
    }

    private static int[] Row(int[] costs, int z) => [.. costs.Skip(z * 11).Take(11)];

    [Fact]
    public void TheTideChangesTheCostOfTheShallowSeaButNeverClosesOrOpensACell()
    {
        var map = Beach();
        var layer = new TideWalkLayer(map, Wave(), Shallow, ShallowCost, FloodedCost);
        const int Wet = 1000 + ShallowCost;
        const int Blocked = MapCell.Blocked;

        // Mean water (tick 250): exactly the base grid.
        Assert.Equal([1000, Wet, Wet, Wet, Wet, Wet, Wet, Wet, Wet, Blocked, Blocked], Row(layer.CostsAt(250), 0));

        // High water (+3, tick 0): depths 6..8 are flooded above the wading depth, still walkable but dear.
        Assert.Equal([1000, Wet, Wet, Wet, Wet, Wet, Wet + FloodedCost, Wet + FloodedCost, Wet + FloodedCost, Blocked, Blocked], Row(layer.CostsAt(0), 0));

        // Low water (−3, tick 500): depths 1..3 fall dry; the deep cells 9 and 10 stay closed.
        Assert.Equal([1000, 1000, 1000, 1000, Wet, Wet, Wet, Wet, Wet, Blocked, Blocked], Row(layer.CostsAt(500), 0));

        // Less than a whole unit of tide changes nothing (tick 240: +0.1 units).
        Assert.Equal(Row(layer.CostsAt(250), 0), Row(layer.CostsAt(240), 0));

        // Fresh water does not follow the tide.
        Assert.All(new long[] { 0, 250, 500 }, t => Assert.Equal(Wet, layer.CostsAt(t)[(2 * 11) + 1]));

        // At every tick the same cells are walkable as on the base grid.
        for (var tick = 0L; tick < 1000; tick += 7)
        {
            var costs = layer.CostsAt(tick);
            Assert.All(Enumerable.Range(0, costs.Length), i => Assert.Equal(map.Cell(i % 11, i / 11).WalkCost == Blocked, costs[i] == Blocked));
        }

        Assert.Equal(1000, layer.MinCost);
        Assert.Equal(8 + 8 + 7, layer.TidalCells);
    }

    [Fact]
    public void AWalkerInAFloodedCellGetsOutAndRoutesAvoidTheFlood()
    {
        var map = Beach();
        var layer = new TideWalkLayer(map, Wave(), Shallow, ShallowCost, FloodedCost);

        // Standing at depth 8 at high water: the way back to the land exists.
        var high = layer.CostsAt(0);
        var out8 = PathFinder.Find(map, (8, 1), (0, 1), 1000, layer.MinCost, null, high);
        Assert.Equal(PathStatus.Found, out8.Status);
        Assert.Equal((0, 1), out8.Cells[^1]);

        // A flooded cell can still be a goal (an object lying there), at its higher cost.
        var into = PathFinder.Find(map, (0, 1), (7, 1), 1000, layer.MinCost, null, high);
        var mean = PathFinder.Find(map, (0, 1), (7, 1), 1000, layer.MinCost, null, layer.CostsAt(250));
        Assert.Equal(PathStatus.Found, into.Status);
        Assert.Equal(mean.Cells.Count, into.Cells.Count);
        Assert.Equal(mean.Cost + (2 * 1000L * FloodedCost), into.Cost);

        // The base search is the search at mean water.
        Assert.Equal(PathFinder.Find(map, (0, 1), (7, 1), 1000, 1000).Cells, mean.Cells);
        Assert.Equal(PathFinder.Find(map, (0, 1), (7, 1), 1000, 1000).Cost, mean.Cost);
    }

    [Fact]
    public void ARouteAlongTheShoreStaysOutOfTheFloodAtHighWater()
    {
        var map = Corridor();
        var layer = new TideWalkLayer(map, Wave(), Shallow, ShallowCost, 2000);
        (int X, int Z) from = (0, 3), to = (8, 3);
        var atMean = PathFinder.Find(map, from, to, 1000, layer.MinCost, null, layer.CostsAt(250));
        var atHigh = PathFinder.Find(map, from, to, 1000, layer.MinCost, null, layer.CostsAt(0));
        Assert.All(atMean.Cells, c => Assert.True(c.Z >= 3));
        Assert.Contains(atHigh.Cells, c => c.Z < 2);
        Assert.Equal(PathStatus.Found, atHigh.Status);
    }

    /// <summary>A corridor along a wall: the short way leads through depth 7, the long way stays at depth 2.</summary>
    private static WorldMap Corridor()
    {
        var builder = new WorldMapBuilder(1, 9, 5, Biomes);
        for (var z = 0; z < 5; z++)
        {
            for (var x = 0; x < 9; x++)
            {
                var wall = z == 2 && x is > 0 and < 8;
                var deep = z > 2;
                builder.Set(x, z, wall ? new MapCell(60, WaterKind.None, 0, -1, 400, 150, 0, MapCell.Blocked)
                    : new MapCell(deep ? 13 : 18, WaterKind.Salt, deep ? 7 : 2, -1, 400, 150, 0, 1000 + ShallowCost));
            }
        }

        return builder.Build();
    }

    [Fact]
    public void ASearchIsKeptOnlyWhileTheTideLevelHolds()
    {
        // The map system keeps the searches of the tide level that holds: the same two cells are not
        // searched twice. With another level the costs are other ones - and here the path as well - so
        // nothing kept may be used.
        var map = Corridor();
        var island = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset).Map!.Island with { Map = map };
        var tick = 250L;
        var system = new WorldMapSystem(island, 100) { TideWalk = new TideWalkLayer(map, Wave(), Shallow, ShallowCost, 2000), Clock = () => tick };
        WorldMapSystem Fresh() => new(island, 100) { TideWalk = new TideWalkLayer(map, Wave(), Shallow, ShallowCost, 2000), Clock = () => tick };
        var from = system.CentreOf(0, 3);
        var to = system.CentreOf(8, 3);

        var atMean = system.Find(from, to);
        Assert.Equal(PathStatus.Found, atMean.Status);
        Assert.Equal(1, system.Searches);
        Assert.Equal(atMean.Waypoints, system.Find(from, to).Waypoints);
        Assert.Equal(1, system.Searches);
        Assert.Equal(Fresh().Find(from, to).Waypoints, atMean.Waypoints);

        // Another place in the same goal cell: the same cells, and the walk ends exactly there.
        var beside = new Simulation.Objects.Position(to.X + 0.2, to.Z - 0.3);
        var toBeside = system.Find(from, beside);
        Assert.Equal(1, system.Searches);
        Assert.Equal(beside, toBeside.Waypoints[^1]);
        Assert.Equal(atMean.Waypoints.SkipLast(1), toBeside.Waypoints.SkipLast(1));
        Assert.Equal(to, atMean.Waypoints[^1]);

        // High water: searched anew, and the way is another one (around the flood).
        tick = 0;
        var atHigh = system.Find(from, to);
        Assert.Equal(2, system.Searches);
        Assert.NotEqual(atMean.Waypoints, atHigh.Waypoints);
        Assert.Equal(Fresh().Find(from, to).Waypoints, atHigh.Waypoints);

        // Back at mean water: what was kept for the high water is gone as well.
        tick = 250;
        Assert.Equal(atMean.Waypoints, system.Find(from, to).Waypoints);
        Assert.Equal(3, system.Searches);

        // What cannot be reached is kept like a path: no second search for the same answer.
        var wall = system.CentreOf(4, 2);
        Assert.Equal(PathStatus.Unreachable, system.Find(from, wall).Status);
        Assert.Equal(PathStatus.Unreachable, system.Find(from, wall).Status);
        Assert.Equal(4, system.Searches);
    }

    [Fact]
    public void TheCostsMustFitTheMap()
    {
        var map = Beach();
        Assert.Throws<ArgumentException>(() => PathFinder.Find(map, (0, 0), (1, 0), 100, 1000, null, new int[5]));
    }

    [Fact]
    public void TheSnapshotOfAnIslandShowsTheTideOfTheTick()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        var tides = new Tides(PresetTides());
        Assert.Equal(tides.Read(0), host.Snapshot().Tide);
        Assert.Equal(PresetTides().Amplitude * 10, host.Snapshot().Tide!.LevelTenths);
        host.World.Run(200);
        Assert.Equal(tides.Read(200), host.Snapshot().Tide);
        Assert.NotEqual(tides.Read(0).LevelTenths, host.Snapshot().Tide!.LevelTenths);
        Assert.NotNull(host.Map!.TideWalk);
        Assert.True(host.Map.TideWalk!.TidalCells > 0);

        // The placeholder world has no sea.
        Assert.Null(SimulationHost.CreatePlaceholderWorld(ContentDirectory, 7, new MockDecisionPort(), "de").Snapshot().Tide);
    }

    [Fact]
    public void AtTheHighestWaterEveryWadeableSeaCellStillHasARouteToTheLand()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        var map = host.Map!;
        Assert.Equal(PresetTides().Amplitude, new Tides(PresetTides()).LevelUnitsAt(host.World.Tick));
        var start = map.Island.Starts[0];
        var land = map.CentreOf(start.X, start.Z);
        var w = map.Map.WidthCells;
        var flooded = Enumerable.Range(0, w * map.Map.DepthCells)
            .Select(i => (X: i % w, Z: i / w))
            .Where(c => map.Map.Cell(c.X, c.Z) is { Water: WaterKind.Salt } cell && cell.WalkCost != MapCell.Blocked && cell.WaterDepth + PresetTides().Amplitude > host.Content.WorldGen.Presets[Preset].Water.ShallowMaxDepth)
            .Where(c => map.Connected(map.CentreOf(c.X, c.Z), land))
            .ToList();
        Assert.NotEmpty(flooded);

        // Every 7th flooded cell: a route to the nearest dry ground exists, and wading there is slower than
        // at mean water (the map system without tides), never impossible.
        var plain = new WorldMapSystem(map.Island, map.CellSizeCm);
        foreach (var cell in flooded.Where((_, i) => i % 7 == 0))
        {
            var here = map.CentreOf(cell.X, cell.Z);
            var dry = NearestDryGround(map, cell, land);
            Assert.Equal(PathStatus.Found, map.Find(here, dry).Status);
            Assert.True(map.SpeedFactor(here) > 0.25 && map.SpeedFactor(here) < plain.SpeedFactor(here), $"speed at ({cell.X}, {cell.Z})");
        }
    }

    /// <summary>Centre of the nearest dry walkable cell of the land's area (rings of growing size around the cell).</summary>
    private static Objects.Position NearestDryGround(WorldMapSystem map, (int X, int Z) cell, Objects.Position land)
    {
        for (var r = 1; r < 64; r++)
        {
            for (var dz = -r; dz <= r; dz++)
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    int x = cell.X + dx, z = cell.Z + dz;
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) == r && map.Map.Contains(x, z)
                        && map.Map.Cell(x, z) is { Water: WaterKind.None } c && c.WalkCost != MapCell.Blocked
                        && map.Connected(map.CentreOf(x, z), land))
                    {
                        return map.CentreOf(x, z);
                    }
                }
            }
        }

        throw new InvalidOperationException($"no dry ground near ({cell.X}, {cell.Z})");
    }

    [Fact]
    public void WithAmplitudeZeroWalkingReadsTheBaseGridAndNoOtherTideValueMatters()
    {
        const string Tides = "\"amplitude\": 3,\n    \"period_ticks\": 745";
        Assert.Contains(Tides, File.ReadAllText(Path.Combine(ContentDirectory, "world", "presets", "start_island.json")).ReplaceLineEndings("\n"), StringComparison.Ordinal);
        var off = CopyContent("off", text => text.Replace(Tides, "\"amplitude\": 0,\n    \"period_ticks\": 745", StringComparison.Ordinal));
        var other = CopyContent("other", text => text
            .Replace(Tides, "\"amplitude\": 0,\n    \"period_ticks\": 100", StringComparison.Ordinal)
            .Replace("\"cost_flooded_water\": 60", "\"cost_flooded_water\": 5000", StringComparison.Ordinal));
        var a = SimulationHost.CreateGeneratedWorld(off, 7, new MockDecisionPort(), "de", Preset);
        var b = SimulationHost.CreateGeneratedWorld(other, 7, new MockDecisionPort(), "de", Preset);
        var with = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);

        // No layer at all: routes and speed are those of the map system without tides (the code before B2-5).
        Assert.Null(a.Map!.TideWalk);
        Assert.Equal(new TideReading(0, 0, 0, 745, false), a.Snapshot().Tide);
        var plain = new WorldMapSystem(a.Map.Island, a.Map.CellSizeCm) { NodeBudget = a.Map.NodeBudget };
        var starts = a.Map.Island.Starts;
        var w = a.Map.Map.WidthCells;
        var shore = Enumerable.Range(0, w * a.Map.Map.DepthCells)
            .Select(i => (X: i % w, Z: i / w))
            .Where(c => a.Map.Map.Cell(c.X, c.Z) is { Water: WaterKind.Salt } cell && cell.WalkCost != MapCell.Blocked)
            .Where((_, i) => i % 97 == 0)
            .Select(c => a.Map.CentreOf(c.X, c.Z))
            .ToList();
        Assert.NotEmpty(shore);
        foreach (var goal in shore)
        {
            var from = a.Map.CentreOf(starts[0].X, starts[0].Z);
            Assert.Equal(plain.Find(from, goal).Waypoints, a.Map.Find(from, goal).Waypoints);
            Assert.Equal(plain.SpeedFactor(goal), a.Map.SpeedFactor(goal));
        }

        // The same island with tides walks the same areas.
        Assert.Equal(a.Map.IslandHash, with.Map!.IslandHash);
        Assert.All(shore, goal => Assert.Equal(
            a.Map.Connected(a.Map.CentreOf(starts[0].X, starts[0].Z), goal),
            with.Map.Connected(with.Map.CentreOf(starts[0].X, starts[0].Z), goal)));

        // Two days: a world without tides does not notice any other tide value.
        var ticks = a.World.Calendar.TicksPerDay * 2;
        a.World.Run(ticks);
        b.World.Run(ticks);
        Assert.Equal(a.World.StateHash(), b.World.StateHash());
    }

    [Fact]
    public void TheTideNeedsNoSaveALoadedWorldGoesOnWithTheSameWaterAndHash()
    {
        var original = SimulationHost.CreateGeneratedWorld(ContentDirectory, 42, new MockDecisionPort(), "de", Preset);
        original.World.Run(300);
        var path = Path.Combine(_directory, "tide.db");
        SaveGameStore.Save(path, StateCodec.Capture(original));

        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        var loaded = StateCodec.Restore(new SimulationHostFactory(Content.Value, settings, new ReplayDecisionPort(original.Decisions.Log), "de"), SaveGameStore.Load(path));
        Assert.Equal(original.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(original.Snapshot().Tide, loaded.Snapshot().Tide);
        Assert.NotNull(loaded.Map!.TideWalk);

        // Half a tide later both stand in the same world.
        var again = SimulationHost.CreateGeneratedWorld(ContentDirectory, 42, new MockDecisionPort(), "de", Preset);
        again.World.Run(300 + 400);
        original.World.Run(400);
        Assert.Equal(again.World.StateHash(), original.World.StateHash());
        Assert.Equal(again.Snapshot().Tide, original.Snapshot().Tide);
    }

    private string CopyContent(string name, Func<string, string> preset)
    {
        var target = Path.Combine(_directory, name);
        foreach (var file in Directory.GetFiles(ContentDirectory, "*.json", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(ContentDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        var path = Path.Combine(target, "world", "presets", "start_island.json");
        var text = File.ReadAllText(path).ReplaceLineEndings("\n");
        var changed = preset(text);
        Assert.NotEqual(text, changed);
        File.WriteAllText(path, changed);
        return target;
    }
}
