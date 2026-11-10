using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// Smooth hills, rock cliffs only (D576, TL #19553): neighbours meet in slopes unless the higher one is a
/// cliff biome at least the cliff drop above; walls follow the terrace edges and the ground stays closed.
/// </summary>
public sealed class TerrainCliffTests
{
    private const int Meadow = 0;
    private const int Rock = 1;

    private static readonly TerrainLook Look = new(
        [new Rgb(0.2f, 0.8f, 0.2f), new Rgb(0.6f, 0.6f, 0.6f)],
        new Rgb(0.3f, 0.3f, 0.3f),
        new Rgb(0.9f, 0.8f, 0.6f),
        new Rgb(0.6f, 0.8f, 0.8f),
        new Rgb(0.4f, 0.6f, 0.8f),
        new Rgb(0.5f, 0.8f, 0.8f),
        new Rgb(0.4f, 0.5f, 0.3f),
        DeepFromUnits: 8,
        SwampMaxUnits: 1,
        SlopeShade: 0.5f,
        SlopeShadeGain: 3f,
        CellM: 1f,
        UnitM: 0.1f,
        CliffDrop: 12,
        ShoreLiftM: 0.03f,
        CliffBiomes: [false, true]);

    [Theory]
    [InlineData(Meadow, 20)]
    [InlineData(Meadow, 40)]
    [InlineData(Rock, 8)]
    [InlineData(Rock, 11)]
    public void NoWallWhereTheStepIsNoRockCliff(int highBiome, int step)
    {
        // Terraces along x: every column 'step' units above the one before. Below the rock cliff threshold
        // (or on meadow at any height) they are one smooth slope: no wall, one height per grid corner.
        var map = Map((x, _) => (30 + (x * step), x >= 4 ? highBiome : Meadow), chunksX: 2);
        foreach (var chunk in new[] { 0, 1 })
        {
            var mesh = TerrainMesher.Ground(map, chunk, 0, Look);
            Assert.Equal(4 * 4 * 2, mesh.TriangleCount);
            Assert.All(Triangles(mesh), t => Assert.True(t.Normal.Y > 0.05f, "no vertical wall"));
        }

        AssertOneHeightPerCorner(map);
        AssertClosed(map, Look);
    }

    [Fact]
    public void ARockStepOfTheCliffDropStaysAWallOwnedByTheRock()
    {
        var map = Map((x, _) => x >= 4 ? (50, Rock) : (30, Meadow), chunksX: 2);
        var walls = Triangles(TerrainMesher.Ground(map, 1, 0, Look)).Where(t => MathF.Abs(t.Normal.Y) < 0.01f).ToList();
        Assert.Equal(4 * 2, walls.Count);
        Assert.All(walls, w => Assert.Equal(-1f, w.Normal.X, 3));
        Assert.DoesNotContain(Triangles(TerrainMesher.Ground(map, 0, 0, Look)), t => MathF.Abs(t.Normal.Y) < 0.01f);

        // The same step with meadow on top is a slope.
        var meadow = Map((x, _) => x >= 4 ? (50, Meadow) : (30, Meadow), chunksX: 2);
        Assert.All(Triangles(TerrainMesher.Ground(meadow, 1, 0, Look)), t => Assert.True(t.Normal.Y > 0.05f));
        AssertClosed(map, Look);
    }

    [Fact]
    public void AWallTopIsTheTerraceEdgeWithoutTeethWhereTheCliffRunsOut()
    {
        // A rock plateau (x >= 3, z >= 2) above meadow; the cliff ends where a ramp cell bridges the drop
        // (3, 1). Before D576 the plateau corners next to the ramp took different heights in neighbouring
        // cells and walls filled the gaps as saw teeth.
        var map = Map((x, z) => (x, z) switch
        {
            ( >= 3, >= 2) => (60, Rock),
            (3, 1) => (50, Meadow),
            _ => (40, Meadow),
        }, chunksX: 2, chunksZ: 2);
        AssertClosed(map, Look);
        AssertWallsOnlyAtRockCliffs(map, Look);
        AssertWallTopsAreTerraceEdges(map, Look);
    }

    [Theory]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(2026UL)]
    public void TheRealIslandHasWallsOnlyAtRockCliffsAndIsClosed(ulong seed)
    {
        var (map, look) = PlainWalls(RealIsland(seed));
        var preset = Content.Value.WorldGen.Presets["start_island"];
        Assert.InRange(look.CliffDrop, preset.Terrain.WalkableSlope + 1, preset.Terrain.CliffMinDrop);
        AssertWallsOnlyAtRockCliffs(map, look);
        AssertWallTopsAreTerraceEdges(map, look);
        AssertClosed(map, look);
    }

    /// <summary>Rock is no flat grey (TL #19984): its brightness varies over the grid, and every wall is darker at its foot than at its top.</summary>
    [Fact]
    public void RockVariesAndWallsDarkenTowardsTheirFoot()
    {
        var (map, look) = PlainWalls(RealIsland(7));
        var rock = Assert.IsType<RockLook>(look.Rock);
        var samples = Enumerable.Range(0, 64).SelectMany(z => Enumerable.Range(0, 64).Select(x => rock.Brightness(x, z))).ToList();
        Assert.True(samples.Max() - samples.Min() > rock.Variation, "rock brightness hardly varies");
        Assert.All(samples, b => Assert.InRange(b, 1 - rock.Variation - 1e-4f, 1 + rock.Variation + 1e-4f));

        var walls = 0;
        for (var cz = 0; cz < map.ChunksZ; cz++)
        {
            for (var cx = 0; cx < map.ChunksX; cx++)
            {
                var mesh = TerrainMesher.Ground(map, cx, cz, look);
                for (var t = 0; t < mesh.TriangleCount; t++)
                {
                    if (MathF.Abs(mesh.Normals[(t * 9) + 1]) >= 0.01f)
                    {
                        continue;
                    }

                    walls++;
                    var v = Enumerable.Range(0, 3).Select(k => (t * 3) + k)
                        .Select(i => (X: mesh.Positions[i * 3], Y: mesh.Positions[(i * 3) + 1], Z: mesh.Positions[(i * 3) + 2], Light: mesh.Colors[i * 3] + mesh.Colors[(i * 3) + 1] + mesh.Colors[(i * 3) + 2])).ToList();
                    foreach (var p in v)
                    {
                        foreach (var q in v.Where(q => q.X == p.X && q.Z == p.Z && q.Y < p.Y))
                        {
                            Assert.True(q.Light < p.Light, "a wall is not darker towards its foot");
                        }
                    }
                }
            }
        }

        Assert.True(walls > 0);
    }

    [Fact]
    public void WalkableGroundHeightIsTheMeshSurfaceOnSlopesAndCliffs()
    {
        var map = Map((x, z) => (30 + ((x * 7) % 11) + ((z * 5) % 9) + (x >= 2 ? 16 : 0) + (z == 3 ? 14 : 0), x == 2 ? Rock : Meadow));
        var faces = 0;
        foreach (var t in Triangles(TerrainMesher.Ground(map, 0, 0, Look)).Where(t => MathF.Abs(t.Normal.Y) >= 0.01f))
        {
            faces++;
            var (cx, cz) = ((t.A.X + t.B.X + t.C.X) / 3, (t.A.Z + t.B.Z + t.C.Z) / 3);
            foreach (var p in new[] { t.A, t.B, t.C })
            {
                Assert.Equal(p.Y, TerrainMesher.GroundHeight(map, Look, p.X + ((cx - p.X) * 1e-4), p.Z + ((cz - p.Z) * 1e-4)), 3);
            }

            foreach (var (a, b) in new[] { (0.2f, 0.3f), (0.6f, 0.1f), (0.33f, 0.33f) })
            {
                var c = 1 - a - b;
                Assert.Equal(
                    (a * t.A.Y) + (b * t.B.Y) + (c * t.C.Y),
                    TerrainMesher.GroundHeight(map, Look, (a * t.A.X) + (b * t.B.X) + (c * t.C.X), (a * t.A.Z) + (b * t.B.Z) + (c * t.C.Z)),
                    4);
            }
        }

        Assert.Equal(4 * 4 * 2, faces);
    }

    /// <summary>Every vertical face lies between two cells that are a rock cliff apart (<see cref="TerrainMesher.Joined"/> false).</summary>
    private static void AssertWallsOnlyAtRockCliffs(WorldMap map, TerrainLook look)
    {
        var walls = 0;
        foreach (var t in AllGround(map, look).Where(t => MathF.Abs(t.Normal.Y) < 0.01f))
        {
            walls++;
            var (x0, z0) = (MathF.Min(MathF.Min(t.A.X, t.B.X), t.C.X), MathF.Min(MathF.Min(t.A.Z, t.B.Z), t.C.Z));
            (int, int) a, b;
            if (MathF.Abs(t.Normal.X) > 0.5f)
            {
                var (gx, gz) = ((int)MathF.Round(x0 / look.CellM), (int)MathF.Floor((z0 / look.CellM) + 1e-3f));
                (a, b) = ((gx - 1, gz), (gx, gz));
            }
            else
            {
                var (gx, gz) = ((int)MathF.Floor((x0 / look.CellM) + 1e-3f), (int)MathF.Round(z0 / look.CellM));
                (a, b) = ((gx, gz - 1), (gx, gz));
            }

            var (ca, cb) = (map.Cell(a.Item1, a.Item2), map.Cell(b.Item1, b.Item2));
            var higher = ca.Height >= cb.Height ? ca : cb;
            Assert.True(Math.Abs(ca.Height - cb.Height) >= look.CliffDrop && look.CliffBiomes![higher.Biome], $"wall between {a} and {b} is no rock cliff");
            Assert.False(TerrainMesher.Joined(ca, cb, look));
        }

        Assert.True(walls == 0 || look.CliffBiomes!.Any(c => c));
    }

    /// <summary>Every wall vertex is a corner of a top face at the same place and height: walls end at the terrace edges, no teeth.</summary>
    private static void AssertWallTopsAreTerraceEdges(WorldMap map, TerrainLook look)
    {
        var ground = AllGround(map, look).ToList();
        var tops = ground.Where(t => MathF.Abs(t.Normal.Y) >= 0.01f).SelectMany(t => new[] { t.A, t.B, t.C }).Select(Key).ToHashSet();
        foreach (var w in ground.Where(t => MathF.Abs(t.Normal.Y) < 0.01f))
        {
            foreach (var v in new[] { w.A, w.B, w.C })
            {
                Assert.Contains(Key(v), tops);
            }
        }
    }

    /// <summary>
    /// The ground of the whole map at level 0 is closed: every edge inside the map belongs to two triangles
    /// (no gaps, no T-junctions, no zigzag along a wall top); only the map border is open. Where two rock
    /// cells touch only diagonally, the four walls around that corner share its vertical edge (four).
    /// </summary>
    private static void AssertClosed(WorldMap map, TerrainLook look)
    {
        var edges = new Dictionary<((int, int, int), (int, int, int)), int>();
        foreach (var t in AllGround(map, look))
        {
            var (a, b, c) = (Key(t.A), Key(t.B), Key(t.C));
            Assert.False(a == b || b == c || a == c, "no degenerate triangle");
            foreach (var (p, q) in new[] { (a, b), (b, c), (c, a) })
            {
                var edge = p.CompareTo(q) < 0 ? (p, q) : (q, p);
                edges[edge] = edges.GetValueOrDefault(edge) + 1;
            }
        }

        var (w, d) = ((int)MathF.Round(map.WidthCells * look.CellM * 1000), (int)MathF.Round(map.DepthCells * look.CellM * 1000));
        foreach (var (((ax, _, az), (bx, _, bz)), count) in edges)
        {
            var border = (ax == bx && (ax == 0 || ax == w)) || (az == bz && (az == 0 || az == d));
            if (!border)
            {
                Assert.True(count is 2 || (count is 4 && ax == bx && az == bz), $"edge ({ax / 1000f}, {az / 1000f})-({bx / 1000f}, {bz / 1000f}) belongs to {count} triangles");
            }
        }
    }

    private static void AssertOneHeightPerCorner(WorldMap map)
    {
        var heights = new Dictionary<(int, int), HashSet<int>>();
        foreach (var t in AllGround(map, Look))
        {
            foreach (var (x, y, z) in new[] { Key(t.A), Key(t.B), Key(t.C) })
            {
                (heights.TryGetValue((x, z), out var set) ? set : heights[(x, z)] = []).Add(y);
            }
        }

        Assert.All(heights.Values, set => Assert.Single(set));
    }

    private static (int X, int Y, int Z) Key((float X, float Y, float Z) v) =>
        ((int)MathF.Round(v.X * 1000), (int)MathF.Round(v.Y * 1000), (int)MathF.Round(v.Z * 1000));

    private static IEnumerable<((float X, float Y, float Z) A, (float X, float Y, float Z) B, (float X, float Y, float Z) C, (float X, float Y, float Z) Normal)> AllGround(WorldMap map, TerrainLook look)
    {
        for (var cz = 0; cz < map.ChunksZ; cz++)
        {
            for (var cx = 0; cx < map.ChunksX; cx++)
            {
                foreach (var t in Triangles(TerrainMesher.Ground(map, cx, cz, look)))
                {
                    yield return t;
                }
            }
        }
    }

    private static IEnumerable<((float X, float Y, float Z) A, (float X, float Y, float Z) B, (float X, float Y, float Z) C, (float X, float Y, float Z) Normal)> Triangles(MeshData mesh)
    {
        (float X, float Y, float Z) At(List<float> list, int i) => (list[i * 3], list[(i * 3) + 1], list[(i * 3) + 2]);
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            yield return (At(mesh.Positions, t * 3), At(mesh.Positions, (t * 3) + 1), At(mesh.Positions, (t * 3) + 2), At(mesh.Normals, t * 3));
        }
    }

    private static WorldMap Map(Func<int, int, (int Height, int Biome)> cell, int chunksX = 1, int chunksZ = 1)
    {
        var builder = new WorldMapBuilder(4, chunksX, chunksZ, ["meadow", "rocks"]);
        for (var z = 0; z < 4 * chunksZ; z++)
        {
            for (var x = 0; x < 4 * chunksX; x++)
            {
                var (height, biome) = cell(x, z);
                builder.Set(x, z, new MapCell(height, WaterKind.None, 0, -1, 400, 150, (byte)biome, 1000));
            }
        }

        return builder.Build();
    }

    [Fact]
    public void TheLooksCliffsMustMatchTheCoresCliffsAndCaves()
    {
        var content = Content.Value;
        var preset = content.WorldGen.Presets["start_island"];
        Contracts.LookTerrain Terrain() => ContractSerializer.Deserialize<Contracts.LookTerrain>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "terrain.json")));
        var palette = ContractSerializer.Deserialize<Contracts.LookPalette>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "palette.json")));
        var island = WorldGenerator.Generate(content.WorldGen, content.Objects, "start_island", 7).Map;
        Assert.Equal(preset.Terrain.CliffMinDrop, TerrainLook.From(Terrain(), palette, island, preset).CliffDrop);

        // Walls on steps the core calls walkable, or missing on steps where it puts caves, are refused.
        var low = Terrain();
        low.Cliffs.MinDropMm = preset.Terrain.WalkableSlope * preset.Grid.HeightUnitMm;
        Assert.Throws<InvalidDataException>(() => TerrainLook.From(low, palette, island, preset));
        var high = Terrain();
        high.Cliffs.MinDropMm = (preset.Terrain.CliffMinDrop + 1) * preset.Grid.HeightUnitMm;
        Assert.Throws<InvalidDataException>(() => TerrainLook.From(high, palette, island, preset));
        var noCaves = Terrain();
        noCaves.Cliffs.Biomes = ["meadow"];
        Assert.Throws<InvalidDataException>(() => TerrainLook.From(noCaves, palette, island, preset));
    }

    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(Path.Combine(TestPaths.Root, "content")));

    /// <summary>
    /// The island with plain vertical walls, which these tests measure: where the walls stand and that the
    /// ground is closed. The rock faces drawn instead (TL #20449) leave the ground as it is, see <see cref="RockFaceTests"/>.
    /// </summary>
    private static (WorldMap Map, TerrainLook Look) PlainWalls((WorldMap Map, TerrainLook Look) island) =>
        (island.Map, island.Look with { Rock = island.Look.Rock! with { Face = null } });

    private static (WorldMap Map, TerrainLook Look) RealIsland(ulong seed)
    {
        var content = Content.Value;
        var island = WorldGenerator.Generate(content.WorldGen, content.Objects, "start_island", seed);
        var look = TerrainLook.From(
            ContractSerializer.Deserialize<Contracts.LookTerrain>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "terrain.json"))),
            ContractSerializer.Deserialize<Contracts.LookPalette>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "palette.json"))),
            island.Map,
            content.WorldGen.Presets["start_island"]);
        return (island.Map, look);
    }
}
