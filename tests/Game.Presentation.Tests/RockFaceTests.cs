using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// Rock walls as natural rock faces (TL #20449): a wall is a skin over the unchanged ground that leans
/// from the terrace edge to a foot on the ground below, with vertices moved by a hash of their grid
/// corner. No straight block sides, no gap to the ground, steep, and the same for the same map.
/// </summary>
public sealed class RockFaceTests
{
    /// <summary>Longest stretch of cells in a row that may still be an exactly vertical, axis-aligned wall (where the cliff runs out or three levels meet).</summary>
    private const int LongestBlockSideCells = 2;

    private const int Meadow = 0;
    private const int Rock = 1;

    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(Path.Combine(TestPaths.Root, "content")));

    [Theory]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(2026UL)]
    public void TheRealIslandsWallsAreNoStraightBlockSides(ulong seed)
    {
        var (map, look) = RealIsland(seed);
        var faces = All(map, look).Where(t => t.Face).ToList();
        Assert.NotEmpty(faces);

        // Exactly vertical and along a grid line: the cells such a wall covers, per grid line.
        var blockSides = new Dictionary<(bool AlongZ, int Line), SortedSet<int>>();
        var straight = 0;
        foreach (var t in faces.Where(t => MathF.Abs(t.Normal.Y) < 1e-4f && (MathF.Abs(t.Normal.X) > 0.9999f || MathF.Abs(t.Normal.Z) > 0.9999f)))
        {
            straight++;
            var alongZ = MathF.Abs(t.Normal.X) > 0.5f;
            var line = (int)MathF.Round((alongZ ? t.A.X : t.A.Z) / look.CellM);
            var from = MathF.Min(MathF.Min(alongZ ? t.A.Z : t.A.X, alongZ ? t.B.Z : t.B.X), alongZ ? t.C.Z : t.C.X);
            var cell = (int)MathF.Floor((from / look.CellM) + 1e-3f);
            (blockSides.TryGetValue((alongZ, line), out var cells) ? cells : blockSides[(alongZ, line)] = []).Add(cell);
        }

        foreach (var ((alongZ, line), cells) in blockSides)
        {
            var run = 0;
            var last = int.MinValue;
            foreach (var cell in cells)
            {
                run = cell == last + 1 ? run + 1 : 1;
                last = cell;
                Assert.True(run <= LongestBlockSideCells, $"seed {seed}: a straight vertical wall of more than {LongestBlockSideCells} cells along {(alongZ ? "x" : "z")} = {line} up to cell {cell}");
            }
        }

        // Nearly all of the rock is leaning face, not block side.
        Assert.True(straight * 20 < faces.Count, $"seed {seed}: {straight} of {faces.Count} wall triangles are straight block sides");
    }

    [Theory]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(2026UL)]
    public void TheRealIslandsFacesMeetTheGroundWithoutGaps(ulong seed)
    {
        var (map, look) = RealIsland(seed);
        AssertFacesMeetTheGround(map, look, $"seed {seed}");
    }

    [Fact]
    public void TheSameMapGivesTheSameFaces()
    {
        var (map, look) = RealIsland(7);
        var (again, _) = RealIsland(7);
        var faces = 0;
        for (var cz = 0; cz < map.ChunksZ; cz++)
        {
            for (var cx = 0; cx < map.ChunksX; cx++)
            {
                var (a, b) = (TerrainMesher.Ground(map, cx, cz, look), TerrainMesher.Ground(again, cx, cz, look));
                Assert.Equal(a.Positions, b.Positions);
                Assert.Equal(a.Colors, b.Colors);
                Assert.Equal(a.RockFaces, b.RockFaces);
                faces += a.RockFaces.Count;
            }
        }

        Assert.True(faces > 0);
    }

    /// <summary>
    /// The core's cliffs stay cliffs: every face vertex lies within the lean plus the jitter of a grid line
    /// (its wall), so a face of at least half the cliff drop is steep and the middle of the cell at its foot,
    /// where inhabitants and caves stand, stays free; and the ground itself is the same with and without faces.
    /// </summary>
    [Theory]
    [InlineData(7UL)]
    [InlineData(42UL)]
    public void FacesStayAtTheirWallAndLeaveTheGroundAlone(ulong seed)
    {
        var (map, look) = RealIsland(seed);
        var face = Assert.IsType<RockFaceLook>(look.Rock!.Face);
        var reach = MathF.Max(face.LeanM, face.LedgeLeanM) + face.JitterM + 1e-4f;
        Assert.True(reach < look.CellM / 2);
        float OffLine(float v) => MathF.Abs((v / look.CellM) - MathF.Round(v / look.CellM)) * look.CellM;
        var all = All(map, look).ToList();
        Assert.All(all.Where(t => t.Face).SelectMany(t => new[] { t.A, t.B, t.C }), v => Assert.True(MathF.Min(OffLine(v.X), OffLine(v.Z)) <= reach, $"seed {seed}: face vertex ({v.X}, {v.Z}) leaves its wall"));

        var plain = All(map, look with { Rock = look.Rock with { Face = null } }).Where(t => !t.Face).Select(t => (t.A, t.B, t.C)).ToList();
        Assert.Equal(plain, all.Where(t => !t.Face).Select(t => (t.A, t.B, t.C)).ToList());
    }

    [Fact]
    public void StackedStepsCloseUpAndTheFootStandsOnTheGround()
    {
        // Open meadow, then three rock steps along x with ledges one cell wide (columns 4 and 5) below a plateau.
        var map = Map((x, _) => x switch { <= 3 => (30, Meadow), 4 => (45, Rock), 5 => (60, Rock), _ => (75, Rock) }, chunksX: 3);
        var look = Look(new RockFaceLook(LeanM: 0.3f, LedgeLeanM: 0.4f, BevelM: 0.12f, JitterM: 0f, RowM: 0.9f, Blend: 0.6f));
        var faces = All(map, look).Where(t => t.Face).SelectMany(t => new[] { t.A, t.B, t.C }).ToList();

        // Without jitter: above open ground the foot stands the lean in front of the wall, on a ledge the
        // ledge lean, so only 0.6 of the ledge stays flat between two faces.
        Assert.Equal(3.7f, faces.Where(v => v.X <= 4).Min(v => v.X), 4);
        Assert.Equal(4.6f, faces.Where(v => v.X is > 4 and <= 5).Min(v => v.X), 4);
        Assert.Equal(5.6f, faces.Where(v => v.X is > 5 and <= 6).Min(v => v.X), 4);
        Assert.DoesNotContain(faces, v => v.X > 6);

        // Top edges are the terrace edges, feet lie at the height of the ground below.
        Assert.Equal(4.5f, faces.Where(v => v.X == 4f).Max(v => v.Y), 4);
        Assert.All(faces.Where(v => MathF.Abs(v.X - 3.7f) < 1e-4f), v => Assert.Equal(3.0f, v.Y, 4));
        Assert.All(faces.Where(v => MathF.Abs(v.X - 4.6f) < 1e-4f), v => Assert.Equal(4.5f, v.Y, 4));
        AssertFacesMeetTheGround(map, look, "steps");

        // No face is a vertical block side, and none is flatter than a steep wall overall (the broken edge aside).
        var triangles = All(map, look).Where(t => t.Face).ToList();
        Assert.All(triangles, t => Assert.True(MathF.Abs(t.Normal.Y) > 1e-3f, "a vertical block side"));
        Assert.All(triangles, t => Assert.True(t.Normal.Y < 0.75f, "a face flat enough to read as a ramp"));
    }

    [Fact]
    public void AnOuterCornerIsCutOffAndStaysClosed()
    {
        // A rock block of 2 × 2 cells on open meadow, and a notch in a plateau: outer and inner corners.
        var block = Map((x, z) => x is >= 3 and <= 4 && z is >= 3 and <= 4 ? (50, Rock) : (30, Meadow), chunksX: 2, chunksZ: 2);
        var notch = Map((x, z) => x >= 3 && z >= 3 && !(x == 3 && z == 3) ? (50, Rock) : (30, Meadow), chunksX: 2, chunksZ: 2);
        foreach (var jitter in new[] { 0f, 0.07f })
        {
            var look = Look(new RockFaceLook(0.3f, 0.4f, 0.12f, jitter, 0.9f, 0.6f));
            AssertFacesMeetTheGround(block, look, "block");
            AssertFacesMeetTheGround(notch, look, "notch");
        }

        // The foot around the block's corner (3, 3) lies on the diagonal, the lean away from the corner.
        var feet = All(block, Look(new RockFaceLook(0.3f, 0.4f, 0.12f, 0f, 0.9f, 0.6f))).Where(t => t.Face).SelectMany(t => new[] { t.A, t.B, t.C }).Where(v => v.X < 3 && v.Z < 3).ToList();
        Assert.NotEmpty(feet);
        Assert.All(feet.Where(v => MathF.Abs(v.Y - 3f) < 1e-4f), v => Assert.Equal(3f - (0.3f / MathF.Sqrt(2)), v.X, 3));
    }

    [Fact]
    public void TheFaceDataMustKeepTheMiddleOfTheCellFree()
    {
        static Contracts.TerrainRockFace Data(int lean, int ledge, int bevel, int jitter) =>
            new() { LeanMm = lean, LedgeLeanMm = ledge, BevelMm = bevel, JitterMm = jitter, RowMm = 900, BlendPermille = 600 };
        Assert.Equal(new RockFaceLook(0.3f, 0.4f, 0.13f, 0.07f, 0.9f, 0.6f), RockFaceLook.From(Data(300, 400, 130, 70), 1f));
        Assert.Throws<InvalidDataException>(() => RockFaceLook.From(Data(300, 450, 130, 70), 1f));
        Assert.Throws<InvalidDataException>(() => RockFaceLook.From(Data(480, 400, 130, 30), 1f));
        Assert.Throws<InvalidDataException>(() => RockFaceLook.From(Data(300, 400, 310, 70), 1f));
        Assert.Throws<InvalidDataException>(() => RockFaceLook.From(Data(100, 400, 50, 70), 1f));

        // The data of the game are valid and switch the faces on.
        Assert.NotNull(RealIsland(7).Look.Rock?.Face);
    }

    /// <summary>The shot set <c>rocks</c> (before and after pictures, seed 7): every view looks at rock faces, three by day and one at night.</summary>
    [Fact]
    public void TheRocksShotSetLooksAtRockFaces()
    {
        var shots = ContractSerializer.Deserialize<Contracts.LookShots>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "shots_rocks.json")));
        var (map, look) = RealIsland(7);
        var faces = All(map, look).Where(t => t.Face).Select(t => t.A).ToList();
        Assert.All(shots.Points, p =>
        {
            var (x, z) = (p.TargetXPermille / 1000f * map.WidthCells * look.CellM, p.TargetZPermille / 1000f * map.DepthCells * look.CellM);
            Assert.True(faces.Count(v => MathF.Abs(v.X - x) < 6 && MathF.Abs(v.Z - z) < 6) >= 30, $"shot {p.Id} shows no rock faces");
            Assert.Contains(p.Phase, shots.Phases);
            Assert.Null(p.Scene);
        });
        Assert.Equal(["day", "day", "day", "night"], shots.Points.Select(p => p.Phase));
    }

    /// <summary>
    /// Every edge of the rock faces belongs to two triangles (four where two rock cells touch only
    /// diagonally), or it is an edge of the ground mesh (the top edge, a plain foot), or it is a foot edge
    /// that lies on the ground: both ends are ground vertices or on the ground surface, and so is its middle,
    /// so the foot follows the ground triangles and neither floats nor leaves a gap.
    /// </summary>
    private static void AssertFacesMeetTheGround(WorldMap map, TerrainLook look, string what)
    {
        var all = All(map, look).ToList();
        var groundVertices = new HashSet<(float, float, float)>();
        var groundEdges = new HashSet<((float, float, float), (float, float, float))>();
        var faceEdges = new Dictionary<((float X, float Y, float Z) P, (float X, float Y, float Z) Q), int>();
        static ((float X, float Y, float Z), (float X, float Y, float Z)) Edge((float X, float Y, float Z) p, (float X, float Y, float Z) q) => p.CompareTo(q) < 0 ? (p, q) : (q, p);
        foreach (var t in all)
        {
            foreach (var (p, q) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
            {
                if (t.Face)
                {
                    faceEdges[Edge(p, q)] = faceEdges.GetValueOrDefault(Edge(p, q)) + 1;
                }
                else
                {
                    groundVertices.Add(p);
                    groundEdges.Add(Edge(p, q));
                }
            }
        }

        Assert.NotEmpty(faceEdges);
        bool OnGround((float X, float Y, float Z) v) => groundVertices.Contains(v) || MathF.Abs(TerrainMesher.GroundHeight(map, look, v.X, v.Z) - v.Y) < 1e-3f;
        var feet = 0;
        var (width, depth) = (map.WidthCells * look.CellM, map.DepthCells * look.CellM);
        foreach (var ((p, q), count) in faceEdges)
        {
            // Like the ground, a face is open where the map ends: the end of a wall at the map border.
            var half = look.CellM / 2;
            var border = MathF.Max(p.X, q.X) < half || MathF.Min(p.X, q.X) > width - half || MathF.Max(p.Z, q.Z) < half || MathF.Min(p.Z, q.Z) > depth - half;
            if (count is 2 or 4 || border || groundEdges.Contains((p, q)))
            {
                continue;
            }

            feet++;
            var middle = ((p.X + q.X) / 2, (p.Y + q.Y) / 2, (p.Z + q.Z) / 2);
            Assert.True(count == 1, $"{what}: face edge {p}-{q} belongs to {count} triangles");
            Assert.True(OnGround(p) && OnGround(q) && OnGround(middle), $"{what}: open face edge {p}-{q} does not lie on the ground");
        }

        Assert.True(feet > 0, $"{what}: no face stands out on the ground");
    }

    private static IEnumerable<((float X, float Y, float Z) A, (float X, float Y, float Z) B, (float X, float Y, float Z) C, (float X, float Y, float Z) Normal, bool Face)> All(WorldMap map, TerrainLook look)
    {
        (float X, float Y, float Z) At(List<float> list, int i) => (list[i * 3], list[(i * 3) + 1], list[(i * 3) + 2]);
        for (var cz = 0; cz < map.ChunksZ; cz++)
        {
            for (var cx = 0; cx < map.ChunksX; cx++)
            {
                var mesh = TerrainMesher.Ground(map, cx, cz, look);
                var faces = mesh.RockFaces.ToHashSet();
                for (var t = 0; t < mesh.TriangleCount; t++)
                {
                    yield return (At(mesh.Positions, t * 3), At(mesh.Positions, (t * 3) + 1), At(mesh.Positions, (t * 3) + 2), At(mesh.Normals, t * 3), faces.Contains(t));
                }
            }
        }
    }

    private static TerrainLook Look(RockFaceLook face) => new(
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
        CliffBiomes: [false, true],
        Rock: new RockLook(0.09f, 7, 0.25f, 0.45f, face));

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
