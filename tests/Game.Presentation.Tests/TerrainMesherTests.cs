using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Presentation.Tests;

public sealed class TerrainMesherTests
{
    private static readonly string[] Biomes = ["low", "high"];

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
        CellM: 1f,
        UnitM: 0.1f,
        CliffDrop: 12);

    [Fact]
    public void FlatGroundIsTwoUpwardTrianglesPerCellWithoutWalls()
    {
        var map = Map((_, _) => 30);
        var mesh = TerrainMesher.Ground(map, 0, 0, Look);
        Assert.Equal(4 * 4 * 2, mesh.TriangleCount);
        Assert.All(Normals(mesh), n => Assert.Equal(1f, n.Y, 3));
        Assert.All(Ys(mesh), y => Assert.Equal(3f, y, 3));
    }

    [Fact]
    public void AGentleSlopeIsSmoothAndHasNoWalls()
    {
        var map = Map((x, _) => 30 + (x * 4));
        var mesh = TerrainMesher.Ground(map, 0, 0, Look);
        Assert.Equal(4 * 4 * 2, mesh.TriangleCount);
        Assert.All(Normals(mesh), n => Assert.True(n.Y > 0.5f));
    }

    [Fact]
    public void ACliffBecomesAnOutwardFacingWallOwnedByTheHigherSide()
    {
        // Cells x >= 4 are 20 units higher: a cliff runs along x = 4 across the map (8 x 4 cells).
        var map = Map((x, _) => x >= 4 ? 50 : 30, chunksX: 2);
        var high = TerrainMesher.Ground(map, 1, 0, Look);
        var low = TerrainMesher.Ground(map, 0, 0, Look);
        Assert.Equal((4 * 4 * 2) + (4 * 2), high.TriangleCount);
        Assert.Equal(4 * 4 * 2, low.TriangleCount);
        var walls = Normals(high).Where(n => MathF.Abs(n.Y) < 0.01f).ToList();
        Assert.Equal(4 * 2 * 3, walls.Count);
        Assert.All(walls, n => Assert.Equal(-1f, n.X, 3));
    }

    [Fact]
    public void NeighbouringChunksShareTheirEdgeHeights()
    {
        var map = Map((x, z) => 30 + ((x * 3) % 7) + ((z * 5) % 9), chunksX: 2, chunksZ: 2);
        var heights = new Dictionary<(float, float), HashSet<float>>();
        for (var cz = 0; cz < 2; cz++)
        {
            for (var cx = 0; cx < 2; cx++)
            {
                var mesh = TerrainMesher.Ground(map, cx, cz, Look);
                for (var i = 0; i < mesh.VertexCount; i++)
                {
                    if (MathF.Abs(mesh.Normals[(i * 3) + 1]) < 0.01f)
                    {
                        continue;
                    }

                    var key = (mesh.Positions[i * 3], mesh.Positions[(i * 3) + 2]);
                    if (!heights.TryGetValue(key, out var set))
                    {
                        heights[key] = set = [];
                    }

                    set.Add(mesh.Positions[(i * 3) + 1]);
                }
            }
        }

        // Without cliffs every corner of the grid has exactly one height, also across chunk borders.
        Assert.All(heights.Values, set => Assert.Single(set));
    }

    [Fact]
    public void WaterSurfacesLieAtBedPlusDepthWithKindColourAndTheCoresFlow()
    {
        var builder = new WorldMapBuilder(4, 1, 1, Biomes);
        for (var z = 0; z < 4; z++)
        {
            for (var x = 0; x < 4; x++)
            {
                var cell = (x, z) switch
                {
                    (0, _) => new MapCell(10, WaterKind.Salt, 10, -1, 1000, 150, 0, MapCell.Blocked),
                    (1, _) => new MapCell(15, WaterKind.Salt, 5, -1, 1000, 150, 0, MapCell.Blocked),
                    (2, 0) => new MapCell(28, WaterKind.Fresh, 2, 6, 1000, 150, 0, 3000),
                    (3, 0) => new MapCell(29, WaterKind.Fresh, 1, -1, 1000, 150, 0, 3000),
                    _ => new MapCell(30, WaterKind.None, 0, -1, 400, 150, 1, 1000),
                };
                builder.Set(x, z, cell);
            }
        }

        var map = builder.Build();
        var water = TerrainMesher.Water(map, 0, 0, Look);

        // Two banks (dry cells next to the fresh water) carry the water surface too: 10 water cells + 2 banks.
        Assert.Equal((4 + 4 + 1 + 1 + 2) * 2, water.TriangleCount);
        var faces = Enumerable.Range(0, water.TriangleCount).Select(f =>
        {
            var v = f * 3;
            return (
                X: (water.Positions[v * 3] + water.Positions[(v + 1) * 3] + water.Positions[(v + 2) * 3]) / 3,
                Z: (water.Positions[(v * 3) + 2] + water.Positions[((v + 1) * 3) + 2] + water.Positions[((v + 2) * 3) + 2]) / 3,
                Y: water.Positions[(v * 3) + 1],
                Color: new Rgb(water.Colors[v * 3], water.Colors[(v * 3) + 1], water.Colors[(v * 3) + 2]),
                Flow: (water.Flow[v * 2], water.Flow[(v * 2) + 1]));
        }).ToList();
        (float, float, float, Rgb, (float, float)) Cell(int x, int z) =>
            faces.Where(f => (int)f.X == x && (int)f.Z == z).Select(f => (f.X, f.Z, f.Y, f.Color, f.Flow)).First();

        Assert.Equal((2f, Look.SeaDeep), (Cell(0, 2).Item3, Cell(0, 2).Item4));
        Assert.Equal((2f, Look.SeaShallow), (Cell(1, 2).Item3, Cell(1, 2).Item4));
        Assert.Equal((3f, Look.Fresh, (0f, 1f)), (Cell(2, 0).Item3, Cell(2, 0).Item4, Cell(2, 0).Item5));
        Assert.Equal((3f, Look.Swamp), (Cell(3, 0).Item3, Cell(3, 0).Item4));
        Assert.Equal(3f, Cell(2, 1).Item3, 3);
        Assert.Equal((0f, 0f), Cell(2, 1).Item5);
        Assert.DoesNotContain(faces, f => (int)f.Z >= 2 && (int)f.X >= 2);
        Assert.Equal(8 * 2, TerrainMesher.Water(map, 0, 0, Look, WaterKind.Salt).TriangleCount);
        Assert.Equal((2 + 2) * 2, TerrainMesher.Water(map, 0, 0, Look, WaterKind.Fresh).TriangleCount);
    }

    [Fact]
    public void AStreamDoesNotSpillIntoItsBanks()
    {
        // A stream runs down a slope; its surface would float above the lower bank, so banks stay dry.
        var builder = new WorldMapBuilder(4, 1, 1, Biomes);
        for (var z = 0; z < 4; z++)
        {
            for (var x = 0; x < 4; x++)
            {
                builder.Set(x, z, x == 1
                    ? new MapCell(40 - (z * 3), WaterKind.Fresh, 2, 6, 1000, 150, 0, 3000)
                    : new MapCell(38 - (z * 3), WaterKind.None, 0, -1, 400, 150, 1, 1000));
            }
        }

        var stream = TerrainMesher.Water(builder.Build(), 0, 0, Look);
        Assert.Equal(4 * 2, stream.TriangleCount);

        // The ribbon slopes with the ground (no flat plates): its surface heights differ along the stream.
        var heights = Enumerable.Range(0, stream.VertexCount).Select(i => stream.Positions[(i * 3) + 1]).Distinct().Count();
        Assert.True(heights > 2, "stream surface must follow the slope");
    }

    [Fact]
    public void TheRealIslandMeshesDeterministicallyWithoutBrokenValues()
    {
        var content = ContentSet.Load(Path.Combine(TestPaths.Root, "content"));
        var island = WorldGenerator.Generate(content.WorldGen, content.Objects, "start_island", 7);
        var look = TerrainLook.From(
            ContractSerializer.Deserialize<Contracts.LookTerrain>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "terrain.json"))),
            ContractSerializer.Deserialize<Contracts.LookPalette>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "palette.json"))),
            island.Map,
            content.WorldGen.Presets["start_island"]);
        var first = TerrainMesher.Ground(island.Map, 2, 3, look);
        var again = TerrainMesher.Ground(island.Map, 2, 3, look);
        Assert.Equal(first.Positions, again.Positions);
        Assert.All(first.Positions.Concat(first.Normals).Concat(first.Colors), v => Assert.True(float.IsFinite(v)));
        Assert.True(first.TriangleCount >= island.Map.ChunkCells * island.Map.ChunkCells * 2);
    }

    private static WorldMap Map(Func<int, int, int> height, int chunksX = 1, int chunksZ = 1)
    {
        var builder = new WorldMapBuilder(4, chunksX, chunksZ, Biomes);
        for (var z = 0; z < 4 * chunksZ; z++)
        {
            for (var x = 0; x < 4 * chunksX; x++)
            {
                builder.Set(x, z, new MapCell(height(x, z), WaterKind.None, 0, -1, 400, 150, 1, 1000));
            }
        }

        return builder.Build();
    }

    private static IEnumerable<(float X, float Y, float Z)> Normals(MeshData mesh) =>
        Enumerable.Range(0, mesh.VertexCount).Select(i => (mesh.Normals[i * 3], mesh.Normals[(i * 3) + 1], mesh.Normals[(i * 3) + 2]));

    private static IEnumerable<float> Ys(MeshData mesh) => Enumerable.Range(0, mesh.VertexCount).Select(i => mesh.Positions[(i * 3) + 1]);
}
