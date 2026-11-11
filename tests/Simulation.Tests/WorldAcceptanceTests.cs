using System.Collections.Concurrent;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// The start islands the acceptance tests of B2 look at: one list of seeds, each island generated once
/// for all tests that read it (an island is never changed after it was generated).
/// </summary>
internal static class AcceptanceIslands
{
    private static readonly Lazy<ContentSet> Loaded = new(() => ContentSet.Load(Path.Combine(TestData.Root, "content")));
    private static readonly ConcurrentDictionary<ulong, Lazy<GeneratedIsland>> Islands = new();

    public const string Preset = "start_island";

    /// <summary>The repository's content (read only: tests that change content load their own).</summary>
    public static ContentSet Content => Loaded.Value;

    /// <summary>The seeds of the acceptance tests.</summary>
    public static TheoryData<ulong> Seeds => [1UL, 3UL, 6UL, 7UL, 11UL, 42UL, 99UL, 123UL, 500UL, 2026UL];

    /// <summary>The start island of a seed from the repository's content.</summary>
    public static GeneratedIsland Of(ulong seed) =>
        Islands.GetOrAdd(seed, s => new Lazy<GeneratedIsland>(() => WorldGenerator.Generate(Content.WorldGen, Content.Objects, Preset, s))).Value;
}

/// <summary>
/// Evidence for the acceptance of B2 (world and look) where the older tests proved less than the
/// acceptance asks: the counter-check of the mandatory check, start positions out of each other's sight,
/// no walking through deep water or over too steep slopes, and the same world on several threads.
/// </summary>
public sealed class WorldAcceptanceTests
{
    private static Contracts.WorldPreset Preset => AcceptanceIslands.Content.WorldGen.Presets[AcceptanceIslands.Preset];

    /// <summary>
    /// Counter-check (B2 acceptance: "a preset without flint fails the mandatory check and is reported").
    /// Flint is the island's spark striker (hard and sharp). With content whose spawn table places no such
    /// type, no attempt can pass: the generator uses up its attempts, returns the island as failed and the
    /// check names the line that failed - and only that one.
    /// </summary>
    [Fact]
    public void AnIslandWithoutFlintFailsTheMandatoryCheckAndSaysWhichLine()
    {
        var content = ContentSet.Load(Path.Combine(TestData.Root, "content"));
        var catalog = content.Objects;
        var spawns = content.WorldGen.Spawns;
        var preset = content.WorldGen.Presets[AcceptanceIslands.Preset];
        var striker = spawns.Mandatory.Single(m => m.Id == "spark_striker");
        bool Strikes(string type) => striker.PropertiesAll.All(p => catalog.HasProperty(type, p)) && !striker.PropertiesNone.Any(p => catalog.HasProperty(type, p));

        // With the repository's table the line passes, and flint is what it counts.
        var sound = AcceptanceIslands.Of(42);
        Assert.True(sound.Passed);
        Assert.True(Assert.Single(sound.Check, c => c.Id == striker.Id).Passed);
        Assert.Equal(["flint"], sound.Objects.Select(o => o.TypeId).Where(Strikes).Distinct());

        // No rule places a spark striker any more; caves and drink spots never were one.
        var rules = spawns.Spawns.Where(r => Strikes(r.ObjectType)).ToList();
        Assert.NotEmpty(rules);
        foreach (var rule in rules)
        {
            Assert.True(spawns.Spawns.Remove(rule));
        }

        Assert.DoesNotContain(preset.Water.DrinkSpots.Select(s => s.ObjectType).Append(preset.Caves.ObjectType), Strikes);

        var island = WorldGenerator.Generate(content.WorldGen, catalog, AcceptanceIslands.Preset, 42);
        Assert.False(island.Passed);
        Assert.Equal(preset.Start.MaxAttempts, island.Attempts);
        Assert.DoesNotContain(island.Objects, o => Strikes(o.TypeId));
        var line = Assert.Single(island.Check, c => c.Id == striker.Id);
        Assert.Equal((0, 0, striker.Min), (line.Reachable, line.Total, line.Min));
        Assert.False(line.Passed || line.ReportOnly);
        Assert.Equal([striker.Id], island.Check.Where(c => !c.Passed && !c.ReportOnly).Select(c => c.Id));

        // Deterministic: the same seed gives the same failed island.
        Assert.Equal(island.Hash(), WorldGenerator.Generate(content.WorldGen, catalog, AcceptanceIslands.Preset, 42).Hash());
    }

    /// <summary>
    /// B2 acceptance (R9): the start positions of the two pairs do not see each other. Every start of one
    /// pair is checked against every start of every other pair, with the height bonus of the view at its
    /// most: nobody sees farther than <c>view_range_cells + max_height_bonus_cells</c> (the upper bound of
    /// <c>ExplorationSystem.SightRangeCells</c>; the weather only shortens it), and a cell is in sight when
    /// it lies within that many cells (a circle). The partners of a pair stand together.
    /// </summary>
    [Theory]
    [MemberData(nameof(AcceptanceIslands.Seeds), MemberType = typeof(AcceptanceIslands))]
    public void NoStartSeesAStartOfAnotherPairEvenFromAHeight(ulong seed)
    {
        var island = AcceptanceIslands.Of(seed);
        Assert.True(island.Passed, $"seed {seed} did not pass");
        var farthest = Preset.Exploration.ViewRangeCells + Preset.Exploration.MaxHeightBonusCells;
        var pairs = island.Starts.GroupBy(s => s.Pair).ToList();
        Assert.Equal(Preset.Start.Pairs, pairs.Count);
        Assert.All(pairs, p => Assert.Equal(2, p.Count()));
        var compared = 0;
        foreach (var a in island.Starts)
        {
            foreach (var b in island.Starts.Where(b => b.Pair != a.Pair))
            {
                var d2 = ((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z));
                Assert.True(
                    d2 > farthest * farthest,
                    $"seed {seed}: the start ({a.X},{a.Z}) of pair {a.Pair} and the start ({b.X},{b.Z}) of pair {b.Pair} are {Math.Sqrt(d2):F1} cells apart, the farthest sight is {farthest}");
                compared++;
            }
        }

        // Each of the four starts against both starts of the other pair.
        Assert.Equal(island.Starts.Count * 2, compared);
    }

    /// <summary>
    /// B2 acceptance (R10): paths never lead through deep water or over too steep slopes. The generator's
    /// own rule (<c>WorldGenerator.WalkCost</c>): a cell is an obstacle where its water is deeper than
    /// <c>water.shallow_max_depth</c>, or where it stands higher than its lowest neighbour (east, north,
    /// west, south) by more than <c>terrain.walkable_slope</c>. Every such cell of the generated island is
    /// blocked, nothing else is, and the routes the path finder finds between the starts and between
    /// walkable cells all over the island contain no such cell.
    /// </summary>
    [Theory]
    [MemberData(nameof(AcceptanceIslands.Seeds), MemberType = typeof(AcceptanceIslands))]
    public void DeepWaterAndTooSteepSlopesAreBlockedAndNoRouteCrossesThem(ulong seed)
    {
        var island = AcceptanceIslands.Of(seed);
        var map = island.Map;
        int w = map.WidthCells, d = map.DepthCells;
        var forbidden = new bool[w * d];
        var walkable = new List<(int X, int Z)>();
        int deep = 0, steep = 0, lowest = int.MaxValue;
        for (var z = 0; z < d; z++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = map.Cell(x, z);
                var tooDeep = c.Water != WaterKind.None && c.WaterDepth > Preset.Water.ShallowMaxDepth;
                var drop = c.Height - new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }
                    .Where(n => map.Contains(x + n.Item1, z + n.Item2))
                    .Min(n => map.Cell(x + n.Item1, z + n.Item2).Height);
                var tooSteep = drop > Preset.Terrain.WalkableSlope;
                deep += tooDeep ? 1 : 0;
                steep += tooSteep ? 1 : 0;
                forbidden[(z * w) + x] = tooDeep || tooSteep;
                if (tooDeep || tooSteep)
                {
                    Assert.True(c.WalkCost == MapCell.Blocked, $"seed {seed}: ({x},{z}) can be walked on (cost {c.WalkCost}) with water depth {c.WaterDepth} and a drop of {drop}");
                }
                else
                {
                    Assert.True(c.WalkCost != MapCell.Blocked, $"seed {seed}: ({x},{z}) is blocked without deep water or a steep slope");
                    walkable.Add((x, z));
                    lowest = Math.Min(lowest, c.WalkCost);
                }
            }
        }

        // The island has both kinds of obstacle, so the rule is really tried.
        Assert.True(deep > 0 && steep > 0, $"seed {seed}: {deep} deep water cells, {steep} steep cells");

        // Routes: from every start to every other one, and between walkable cells spread over the main land
        // (every n-th of them, in row order - no random draw, the same routes every time). The budget is the
        // whole map: this test asks where routes lead, not how far a search may look.
        var areas = PathFinder.WalkableAreas(map);
        var first = island.Starts[0];
        var main = areas[(first.Z * w) + first.X];
        var onMain = walkable.Where(c => areas[(c.Z * w) + c.X] == main).ToList();
        var ends = island.Starts.Select(s => (s.X, s.Z)).ToList();
        ends.AddRange(Enumerable.Range(0, 6).Select(k => onMain[(int)((long)k * (onMain.Count - 1) / 5)]));
        var scratch = new PathFinder.Scratch(w * d);
        var routes = 0;
        for (var a = 0; a < ends.Count; a++)
        {
            for (var b = a + 1; b < ends.Count; b++)
            {
                var path = PathFinder.Find(map, ends[a], ends[b], w * d, lowest, scratch);
                Assert.True(path.Status == PathStatus.Found, $"seed {seed}: no route from {ends[a]} to {ends[b]} ({path.Status})");
                foreach (var (x, z) in path.Cells)
                {
                    Assert.False(forbidden[(z * w) + x], $"seed {seed}: the route from {ends[a]} to {ends[b]} crosses ({x},{z}): water depth {map.Cell(x, z).WaterDepth}, height {map.Cell(x, z).Height}");
                }

                routes += path.Cells.Count > 0 ? 1 : 0;
            }
        }

        Assert.True(routes >= 10, $"seed {seed}: only {routes} routes with steps");
    }

    /// <summary>
    /// B2 acceptance 1 (same hash with 1 and N threads): the generator itself runs single-threaded - it
    /// starts no task and no thread, keeps nothing between calls but constants, and draws only from the
    /// streams it derives from the seed. So the number of threads around it cannot change an island: the
    /// same seed generated on several threads at once gives the hash of the island generated alone.
    /// </summary>
    [Fact]
    public void TheSameSeedOnSeveralThreadsAtOnceGivesTheSameWorld()
    {
        const int threads = 4;
        var content = AcceptanceIslands.Content;
        var alone = AcceptanceIslands.Of(7).Hash();
        var hashes = new string[threads];
        using var start = new Barrier(threads);
        var workers = Enumerable.Range(0, threads).Select(k => new Thread(() =>
        {
            start.SignalAndWait();
            hashes[k] = WorldGenerator.Generate(content.WorldGen, content.Objects, AcceptanceIslands.Preset, 7).Hash();
        })).ToList();
        workers.ForEach(t => t.Start());
        workers.ForEach(t => t.Join());
        Assert.All(hashes, h => Assert.Equal(alone, h));
    }

    /// <summary>
    /// Pins what the test above relies on: no source file of the generator and its map uses tasks, threads
    /// or parallel loops. Whoever makes the generator parallel must prove the same hashes anew.
    /// </summary>
    [Fact]
    public void TheGeneratorSourceUsesNoThreadsOrTasks()
    {
        string[] marks = ["Parallel.", "AsParallel", "Task.", "Task<", "ThreadPool", "new Thread", "async ", "await ", "System.Threading"];
        var files = Directory.GetFiles(Path.Combine(TestData.Root, "src", "Simulation.Core", "Map"), "*.cs", SearchOption.AllDirectories);
        Assert.Contains(files, f => Path.GetFileName(f) == "WorldGenerator.cs");
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.All(marks, mark => Assert.False(text.Contains(mark, StringComparison.Ordinal), $"{Path.GetFileName(file)} uses '{mark}'"));
        }
    }
}
