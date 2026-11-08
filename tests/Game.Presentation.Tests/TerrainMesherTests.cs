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

        var water = TerrainMesher.Water(builder.Build(), 0, 0, Look);
        Assert.Equal((4 + 4 + 1 + 1) * 2, water.TriangleCount);
        var vertices = Enumerable.Range(0, water.VertexCount).Select(i => (
            X: water.Positions[i * 3], Y: water.Positions[(i * 3) + 1],
            Color: new Rgb(water.Colors[i * 3], water.Colors[(i * 3) + 1], water.Colors[(i * 3) + 2]),
            Flow: (water.Flow[i * 2], water.Flow[(i * 2) + 1]))).ToList();
        Assert.All(vertices.Where(v => v.X < 1f), v => Assert.Equal((2f, Look.SeaDeep), (v.Y, v.Color)));
        Assert.All(vertices.Where(v => v.X is > 1f and < 2f), v => Assert.Equal((2f, Look.SeaShallow), (v.Y, v.Color)));
        var stream = vertices.Where(v => v.X is > 2f and < 3f).ToList();
        Assert.All(stream, v => Assert.Equal((3f, Look.Fresh, (0f, 1f)), (v.Y, v.Color, v.Flow)));
        Assert.All(vertices.Where(v => v.X > 3f), v => Assert.Equal(Look.Swamp, v.Color));
        Assert.Equal(8 * 2, TerrainMesher.Water(builder.Build(), 0, 0, Look, WaterKind.Salt).TriangleCount);
        Assert.Equal(2 * 2, TerrainMesher.Water(builder.Build(), 0, 0, Look, WaterKind.Fresh).TriangleCount);
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
