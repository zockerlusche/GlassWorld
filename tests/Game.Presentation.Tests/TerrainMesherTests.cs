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

        // Nine water plates: the running cell (2, 0) is drawn as a stream band (Streams), not here. The dry
        // cells next to the swamp lie no higher than its surface, so they carry no bank plate (they would
        // look flooded); the swamp comes down to them instead.
        Assert.Equal((4 + 4 + 1) * 2, water.TriangleCount);
        var faces = Faces(water);
        (float Y, Rgb Color, (float, float) Flow) Cell(int x, int z) =>
            faces.Where(f => (int)f.X == x && (int)f.Z == z).Select(f => (f.Y, f.Color, f.Flow)).First();

        Assert.Equal((2f, Look.SeaDeep), (Cell(0, 2).Y, Cell(0, 2).Color));
        Assert.Equal((2f, Look.SeaShallow), (Cell(1, 2).Y, Cell(1, 2).Color));
        Assert.DoesNotContain(faces, f => (int)f.X == 2 && (int)f.Z == 0);
        Assert.Equal(Look.Swamp, Cell(3, 0).Color);
        Assert.Equal((0f, 0f), Cell(3, 0).Flow);
        Assert.DoesNotContain(faces, f => (int)f.Z >= 1 && (int)f.X >= 2);
        Assert.Equal(8 * 2, TerrainMesher.Water(map, 0, 0, Look, WaterKind.Salt).TriangleCount);
        Assert.Equal(1 * 2, TerrainMesher.Water(map, 0, 0, Look, WaterKind.Fresh).TriangleCount);
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
    public void ATwoCellStreamOfTheGeneratorIsOneContinuousBandBetweenItsCells()
    {
        // The generator's layout: a main cell (x = 1) and its side cell (x = 2) both run down the slope
        // (flow 6 = +z) between dry banks. One band runs between them, no square cell plates (A1456).
        var map = TwoCellStream();
        Assert.Equal(0, TerrainMesher.Water(map, 0, 0, Look).TriangleCount);
        var line = Assert.Single(TerrainMesher.StreamLines(map, Look));
        Assert.All(line, p => Assert.Equal(2.0, p.X, 6));
        Assert.Equal(0.5, line[0].Z, 6);
        Assert.Equal(3.5, line[^1].Z, 6);

        var stream = TerrainMesher.Streams(map, Look);
        Assert.Equal((line.Count - 1) * 4, stream.TriangleCount);
        Assert.All(Normals(stream), n => Assert.True(n.Y > 0, "every band face looks up"));
        Assert.All(Faces(stream), f => Assert.Equal((0f, 1f), f.Flow));

        // Continuous: every segment starts on the cross-section the one before it ended on.
        var faces = Faces(stream);
        for (var s = 1; s < line.Count - 1; s++)
        {
            var end = faces.Skip((s - 1) * 4).Take(4).SelectMany(f => f.Vertices).Where(v => Math.Abs(v.Z - line[s].Z) < 1e-4).Distinct().ToList();
            var start = faces.Skip(s * 4).Take(4).SelectMany(f => f.Vertices).Where(v => Math.Abs(v.Z - line[s].Z) < 1e-4).Distinct().ToList();
            Assert.Equal(3, end.Count);
            Assert.Equal(end.Order(), start.Order());
        }
    }

    [Fact]
    public void AStreamBandFollowsTheCoresDirectionWithRoundedCorners()
    {
        // A stream that runs east (flow 0) for four cells and then turns south (flow 6).
        var builder = new WorldMapBuilder(8, 1, 1, Biomes);
        for (var z = 0; z < 8; z++)
        {
            for (var x = 0; x < 8; x++)
            {
                var flow = (x, z) switch
                {
                    ( < 4, 1) => (sbyte)0,
                    (4, >= 1 and < 7) => (sbyte)6,
                    _ => (sbyte)-1,
                };
                builder.Set(x, z, flow >= 0
                    ? new MapCell(40 - x - z, WaterKind.Fresh, 2, flow, 1000, 150, 0, 3000)
                    : new MapCell(42 - x - z, WaterKind.None, 0, -1, 400, 150, 1, 1000));
            }
        }

        var map = builder.Build();
        var raw = Assert.Single(TerrainMesher.StreamLines(map, Look with { StreamSmoothing = 0 }));
        Assert.Equal([(0.5, 1.5), (1.5, 1.5), (2.5, 1.5), (3.5, 1.5), (4.5, 1.5), (4.5, 2.5), (4.5, 3.5), (4.5, 4.5), (4.5, 5.5), (4.5, 6.5)], raw.Select(p => (p.X, p.Z)));

        var smooth = Assert.Single(TerrainMesher.StreamLines(map, Look));
        Assert.True(smooth.Count > raw.Count);
        Assert.Equal((raw[0].X, raw[0].Z), (smooth[0].X, smooth[0].Z));
        Assert.Equal((raw[^1].X, raw[^1].Z), (smooth[^1].X, smooth[^1].Z));

        // The corner (4.5, 1.5) is cut: no smoothed point lies on it, and the line never runs against the flow.
        Assert.DoesNotContain(smooth, p => Math.Abs(p.X - 4.5) < 1e-6 && Math.Abs(p.Z - 1.5) < 1e-6);
        for (var i = 1; i < smooth.Count; i++)
        {
            Assert.True(smooth[i].X >= smooth[i - 1].X - 1e-9 && smooth[i].Z >= smooth[i - 1].Z - 1e-9, "the band follows the core's direction");
        }

        // No squares: the band's corners are not the cell grid.
        var stream = TerrainMesher.Streams(map, Look);
        var offGrid = Enumerable.Range(0, stream.VertexCount).Count(i => Math.Abs(stream.Positions[i * 3] - MathF.Round(stream.Positions[i * 3])) > 0.01f);
        Assert.True(offGrid > stream.VertexCount / 2, "band vertices lie off the cell corners");
    }

    [Fact]
    public void AStreamBandNeverFloatsAboveItsBankByMoreThanTheLift()
    {
        // A stream runs down a slope; the banks are lower than the water.
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

        var map = builder.Build();
        AssertBandRests(map, Look);
        var heights = Ys(TerrainMesher.Streams(map, Look)).Distinct().Count();
        Assert.True(heights > 2, "the band follows the slope");
    }

    [Theory]
    [InlineData(7UL)]
    [InlineData(42UL)]
    public void TheStreamsOfTheRealIslandAreBandsResting(ulong seed)
    {
        var (map, look) = RealIsland(seed);
        var lines = TerrainMesher.StreamLines(map, look);
        Assert.NotEmpty(lines);
        AssertBandRests(map, look);

        // Every running cell lies under a band (within a cell diagonal of its centre line).
        var points = lines.SelectMany(l => l).ToList();
        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                if (map.Cell(x, z) is { Water: WaterKind.Fresh, Flow: >= 0 })
                {
                    var (cx, cz) = (x + 0.5, z + 0.5);
                    Assert.True(points.Any(p => ((p.X - cx) * (p.X - cx)) + ((p.Z - cz) * (p.Z - cz)) <= 2.0), $"running cell ({x}, {z}) has no band");
                }
            }
        }
    }

    /// <summary>
    /// TL #19755: a stream is cut into the terrain - its cell is a trough down to the core's bed, the water
    /// lies below the banks, and the band is as wide as the core says (wider downstream).
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    public void StreamsLieInATroughBelowTheirBanksAsWideAsTheCoreSays(ulong seed)
    {
        var (map, look) = RealIsland(seed);
        int troughs = 0, below = 0;
        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                var cell = map.Cell(x, z);
                if (!TerrainMesher.IsTrough(map, x, z))
                {
                    continue;
                }

                troughs++;
                var centre = TerrainMesher.GroundHeight(map, look, (x + 0.5) * look.CellM, (z + 0.5) * look.CellM);
                Assert.Equal(cell.Height * look.UnitM, centre, 4);
                var banks = TerrainMesher.CornerHeights(map, x, z, look).Max() * look.UnitM;
                below += centre + (cell.WaterDepth * look.UnitM) < banks ? 1 : 0;
            }
        }

        Assert.True(troughs > 0);
        // Where a stream crosses a flat between ponds (its bed cannot fall below the pond it runs into) the
        // water lies level with its banks; everywhere else below them.
        Assert.True(below * 10 >= troughs * 7, $"seed {seed}: water below the banks in {below} of {troughs} stream cells");

        // The band widths come from the core's widths (blended along the smoothed line).
        var hydrology = map.Hydrology!;
        var core = new List<double>();
        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                if (hydrology.WidthCm(x, z) > 0)
                {
                    core.Add(hydrology.WidthCm(x, z) / 100.0);
                }
            }
        }

        var widths = TerrainMesher.StreamLines(map, look).SelectMany(l => l).Select(p => p.WidthM).ToList();
        // A stream starting at its source widens from a fifth of its width (seep, TL #19722 polish).
        Assert.All(widths, w => Assert.InRange(w, (core.Min() * 0.2) - 1e-9, core.Max() + 1e-9));
        Assert.True(widths.Max() > widths.Min(), $"seed {seed}: streams widen with their flow");
    }

    /// <summary>Centre vertices lie at ground plus depth; every other vertex at most the shore lift above its ground.</summary>
    private static void AssertBandRests(WorldMap map, TerrainLook look)
    {
        var centres = TerrainMesher.StreamLines(map, look).SelectMany(l => l).Select(p => (X: (float)p.X, Z: (float)p.Z, p.Depth)).ToList();
        var stream = TerrainMesher.Streams(map, look);
        Assert.True(stream.TriangleCount > 0);
        for (var i = 0; i < stream.VertexCount; i++)
        {
            float x = stream.Positions[i * 3], y = stream.Positions[(i * 3) + 1], z = stream.Positions[(i * 3) + 2];
            var ground = TerrainMesher.GroundHeight(map, look, x, z);
            var centre = centres.Where(c => MathF.Abs(c.X - x) < 1e-5f && MathF.Abs(c.Z - z) < 1e-5f).Select(c => (double?)c.Depth).FirstOrDefault();
            if (centre is { } depth)
            {
                // Float rounding only (the heights are single precision): a tolerance, not decimal places.
                Assert.InRange(y, ground + (float)(depth * look.UnitM) - 1e-4f, ground + (float)(depth * look.UnitM) + 1e-4f);
            }
            else
            {
                Assert.True(y <= ground + look.ShoreLiftM + 1e-4f, $"band edge at ({x}, {z}) floats {y - ground:0.000} m above the bank");
            }
        }
    }

    private static WorldMap TwoCellStream()
    {
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

        return builder.Build();
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
            var ground = TerrainMesher.CornerHeights(map, x, z, look);
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
