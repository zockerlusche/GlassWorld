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
        SlopeShadeGain: 3f,
        CellM: 1f,
        UnitM: 0.1f,
        CliffDrop: 12,
        ShoreLiftM: 0.03f);

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

        // Ten water cells. The dry cells next to the swamp lie no higher than its surface, so they carry
        // no bank plate (they would look flooded); the swamp comes down to them instead.
        Assert.Equal((4 + 4 + 1 + 1) * 2, water.TriangleCount);
        var faces = Faces(water);
        (float Y, Rgb Color, (float, float) Flow) Cell(int x, int z) =>
            faces.Where(f => (int)f.X == x && (int)f.Z == z).Select(f => (f.Y, f.Color, f.Flow)).First();

        Assert.Equal((2f, Look.SeaDeep), (Cell(0, 2).Y, Cell(0, 2).Color));
        Assert.Equal((2f, Look.SeaShallow), (Cell(1, 2).Y, Cell(1, 2).Color));
        Assert.Equal((3f, Look.Fresh, (0f, 1f)), (Cell(2, 0).Y, Cell(2, 0).Color, Cell(2, 0).Flow));
        Assert.Equal(Look.Swamp, Cell(3, 0).Color);
        Assert.Equal((0f, 0f), Cell(3, 0).Flow);
        Assert.DoesNotContain(faces, f => (int)f.Z >= 1 && (int)f.X >= 2);
        Assert.Equal(8 * 2, TerrainMesher.Water(map, 0, 0, Look, WaterKind.Salt).TriangleCount);
        Assert.Equal(2 * 2, TerrainMesher.Water(map, 0, 0, Look, WaterKind.Fresh).TriangleCount);
    }

    [Fact]
    public void ABankCarriesAPlateOnlyWhereTheShoreLineCrossesIt()
    {
        // A lake along x = 0 (surface 30); the bank cells at x = 1 rise from below to above the surface.
        var shore = TerrainMesher.Water(LakeBeside(31, 40), 0, 0, Look);
        Assert.Equal((4 + 4) * 2, shore.TriangleCount);
        Assert.Contains(Faces(shore), f => (int)f.X == 1);
        Assert.DoesNotContain(Faces(shore), f => (int)f.X >= 2);

        // A bank wholly above the water stays dry.
        var high = TerrainMesher.Water(LakeBeside(36, 36), 0, 0, Look);
        Assert.Equal(4 * 2, high.TriangleCount);
        Assert.All(Faces(high), f => Assert.Equal(0, (int)f.X));
    }

    [Fact]
    public void WaterBesideABankBelowItsSurfaceComesDownToTheGround()
    {
        // The bank (height 29) lies wholly below the lake surface (30): no plate (it would look flooded),
        // and the lake's edge comes down to the bank's ground plus the shore lift instead of floating.
        var map = LakeBeside(29, 29);
        var lake = TerrainMesher.Water(map, 0, 0, Look);
        Assert.Equal(4 * 2, lake.TriangleCount);
        for (var i = 0; i < lake.VertexCount; i++)
        {
            float x = lake.Positions[i * 3], y = lake.Positions[(i * 3) + 1], z = lake.Positions[(i * 3) + 2];
            if (x > 0.5f)
            {
                var ground = TerrainMesher.GroundHeight(map, Look, x, z);
                Assert.Equal(ground + Look.ShoreLiftM, y, 4);
            }
        }
    }

    [Fact]
    public void ATwoCellStreamOfTheGeneratorIsOneSlopedRibbon()
    {
        // The generator's layout: a main cell (x = 1) and its side cell (x = 2) both run down the slope
        // (flow 6 = +z) between dry banks. Both are sloped ribbons, no flat plate of still water.
        var builder = new WorldMapBuilder(4, 1, 1, Biomes);
        for (var z = 0; z < 4; z++)
        {
            for (var x = 0; x < 4; x++)
            {
                builder.Set(x, z, x is 1 or 2
                    ? new MapCell(40 - (z * 3), WaterKind.Fresh, 2, 6, 1000, 150, 0, 3000)
                    : new MapCell(40 - (z * 3), WaterKind.None, 0, -1, 400, 150, 1, 1000));
            }
        }

        var map = builder.Build();
        var stream = TerrainMesher.Water(map, 0, 0, Look);
        Assert.Equal(8 * 2, stream.TriangleCount);
        Assert.All(Normals(stream), n => Assert.True(n.Y < 0.999f, "every stream face follows the slope"));
        Assert.All(Faces(stream), f => Assert.Equal((0f, 1f), f.Flow));

        // Where both cells meet (x = 2) the water lies at ground plus depth; at the dry banks (x = 1 and 3)
        // it comes down to the ground plus the shore lift, so no edge floats above the bank.
        for (var i = 0; i < stream.VertexCount; i++)
        {
            float x = stream.Positions[i * 3], y = stream.Positions[(i * 3) + 1], z = stream.Positions[(i * 3) + 2];
            var ground = TerrainMesher.GroundHeight(map, Look, x, z);
            var inner = MathF.Abs(x - 2f) < 0.01f;
            Assert.Equal(inner ? ground + (2 * Look.UnitM) : ground + Look.ShoreLiftM, y, 4);
        }
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(2026UL)]
    public void NoBankPlateOfTheRealIslandLiesAboveItsWholeCell(ulong seed)
    {
        var (map, look) = RealIsland(seed);
        var cells = new Dictionary<(int X, int Z), List<(float X, float Y, float Z)>>();
        for (var cz = 0; cz < map.ChunksZ; cz++)
        {
            for (var cx = 0; cx < map.ChunksX; cx++)
            {
                var water = TerrainMesher.Water(map, cx, cz, look, WaterKind.Fresh);
                foreach (var f in Faces(water))
                {
                    var key = ((int)MathF.Floor(f.X / look.CellM), (int)MathF.Floor(f.Z / look.CellM));
                    if (map.Cell(key.Item1, key.Item2).Water == WaterKind.None)
                    {
                        (cells.TryGetValue(key, out var list) ? list : cells[key] = []).AddRange(f.Vertices);
                    }
                }
            }
        }

        Assert.NotEmpty(cells);
        foreach (var ((x, z), vertices) in cells)
        {
            // At least one corner of the plate lies at or below the ground (within the shore lift), so
            // the ground hides part of it and the dry cell does not look flooded.
            var ground = TerrainMesher.CornerHeights(map, x, z, look.CliffDrop);
            float Ground((float X, float Y, float Z) v) =>
                ground[((int)MathF.Round((v.X / look.CellM) - x), (int)MathF.Round((v.Z / look.CellM) - z)) switch
                {
                    (0, 0) => 0,
                    (1, 0) => 1,
                    (1, 1) => 2,
                    _ => 3,
                }] * look.UnitM;
            var lowest = vertices.Min(v => v.Y - Ground(v));
            Assert.True(lowest <= look.ShoreLiftM + 1e-4f, $"seed {seed}: bank plate at ({x},{z}) lies {lowest:0.00} m above its whole cell");
        }
    }

    [Fact]
    public void GroundHeightIsTheMeshSurface()
    {
        var map = Map((x, z) => 30 + ((x * 7) % 11) + ((z * 5) % 9) + (x == 2 && z == 1 ? 15 : 0));
        var mesh = TerrainMesher.Ground(map, 0, 0, Look);
        var faces = 0;
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var v = Enumerable.Range(0, 3).Select(k => (X: mesh.Positions[((t * 3) + k) * 3], Y: mesh.Positions[(((t * 3) + k) * 3) + 1], Z: mesh.Positions[(((t * 3) + k) * 3) + 2])).ToArray();
            if (MathF.Abs(mesh.Normals[(t * 9) + 1]) < 0.01f)
            {
                continue; // cliff wall
            }

            faces++;

            // Pull the vertices a hair towards the centre so each sample lies in this cell.
            var cx = (v[0].X + v[1].X + v[2].X) / 3;
            var cz = (v[0].Z + v[1].Z + v[2].Z) / 3;
            foreach (var p in v)
            {
                Assert.Equal(p.Y, TerrainMesher.GroundHeight(map, Look, p.X + ((cx - p.X) * 1e-4), p.Z + ((cz - p.Z) * 1e-4)), 3);
            }

            // Inside: barycentric points lie on the triangle's plane.
            foreach (var (a, b) in new[] { (0.2f, 0.3f), (0.6f, 0.1f), (0.33f, 0.33f) })
            {
                var c = 1 - a - b;
                Assert.Equal(
                    (a * v[0].Y) + (b * v[1].Y) + (c * v[2].Y),
                    TerrainMesher.GroundHeight(map, Look, (a * v[0].X) + (b * v[1].X) + (c * v[2].X), (a * v[0].Z) + (b * v[1].Z) + (c * v[2].Z)),
                    4);
            }
        }

        Assert.Equal(4 * 4 * 2, faces);
    }

    private static WorldMap LakeBeside(int bank, int beyond)
    {
        var builder = new WorldMapBuilder(4, 1, 1, Biomes);
        for (var z = 0; z < 4; z++)
        {
            for (var x = 0; x < 4; x++)
            {
                builder.Set(x, z, x switch
                {
                    0 => new MapCell(26, WaterKind.Fresh, 4, -1, 1000, 150, 0, 3000),
                    1 => new MapCell(bank, WaterKind.None, 0, -1, 400, 150, 1, 1000),
                    _ => new MapCell(beyond, WaterKind.None, 0, -1, 400, 150, 1, 1000),
                });
            }
        }

        return builder.Build();
    }

    private static List<(float X, float Z, float Y, Rgb Color, (float, float) Flow, (float X, float Y, float Z)[] Vertices)> Faces(MeshData mesh) =>
        Enumerable.Range(0, mesh.TriangleCount).Select(f =>
        {
            var v = f * 3;
            var vertices = Enumerable.Range(v, 3).Select(i => (mesh.Positions[i * 3], mesh.Positions[(i * 3) + 1], mesh.Positions[(i * 3) + 2])).ToArray();
            return (
                X: vertices.Average(p => p.Item1),
                Z: vertices.Average(p => p.Item3),
                Y: mesh.Positions[(v * 3) + 1],
                Color: new Rgb(mesh.Colors[v * 3], mesh.Colors[(v * 3) + 1], mesh.Colors[(v * 3) + 2]),
                Flow: (mesh.Flow[v * 2], mesh.Flow[(v * 2) + 1]),
                Vertices: vertices);
        }).ToList();

    private static (WorldMap Map, TerrainLook Look) RealIsland(ulong seed)
    {
        var content = ContentSet.Load(Path.Combine(TestPaths.Root, "content"));
        var island = WorldGenerator.Generate(content.WorldGen, content.Objects, "start_island", seed);
        var look = TerrainLook.From(
            ContractSerializer.Deserialize<Contracts.LookTerrain>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "terrain.json"))),
            ContractSerializer.Deserialize<Contracts.LookPalette>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "palette.json"))),
            island.Map,
            content.WorldGen.Presets["start_island"]);
        return (island.Map, look);
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
        var (map, look) = RealIsland(7);
        var first = TerrainMesher.Ground(map, 2, 3, look);
        var again = TerrainMesher.Ground(map, 2, 3, look);
        Assert.Equal(first.Positions, again.Positions);
        Assert.All(first.Positions.Concat(first.Normals).Concat(first.Colors), v => Assert.True(float.IsFinite(v)));
        Assert.True(first.TriangleCount >= map.ChunkCells * map.ChunkCells * 2);
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
