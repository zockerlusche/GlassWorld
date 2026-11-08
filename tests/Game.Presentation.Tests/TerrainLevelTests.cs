using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// Detail levels of the terrain chunks (B2 R18): coarser ground where it is plain dry land, the coast,
/// banks, water and cliffs as on the base grid, no cracks and no skirts.
/// </summary>
public sealed class TerrainLevelTests
{
    private const float Eps = 1e-4f;
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
    public void LevelsFollowTheDataAndRejectBrokenOnes()
    {
        var map = Map((x, z) => 30 + x + z, chunksX: 2, chunksZ: 2);
        var levels = TerrainLevel.From(Detail((1, 50), (2, 120), (4, null)), map, Look);
        Assert.Equal([1, 2, 4], levels.Select(l => l.CellStep));
        Assert.Equal([0f, 50f, 120f], levels.Select(l => l.VisibleFromM));
        Assert.Equal([50f, 120f, 0f], levels.Select(l => l.VisibleToM));

        // Godot ranges with the switch margin (2 m) as hysteresis: widened by the margin on the inner ends.
        Assert.Equal([0f, 48f, 118f], levels.Select(l => l.RangeBeginM));
        Assert.Equal([52f, 122f, 0f], levels.Select(l => l.RangeEndM));
        for (var i = 1; i < levels.Count; i++)
        {
            // A hidden level appears only a margin inside its range: both appear exactly at the switch
            // distance, so a chunk is never at neither level; a shown level leaves a margin outside it.
            var (near, far) = (levels[i - 1], levels[i]);
            Assert.Equal(near.VisibleToM, near.RangeEndM - near.SwitchMarginM);
            Assert.Equal(near.VisibleToM, far.RangeBeginM + far.SwitchMarginM);
        }

        Assert.Equal(0.1f, levels[1].MaxErrorM, 5);
        Assert.Equal(0.03f, levels[1].MaxColorDelta, 5);

        Assert.Throws<InvalidDataException>(() => TerrainLevel.From(Detail((2, 50), (4, null)), map, Look));
        Assert.Throws<InvalidDataException>(() => TerrainLevel.From(Detail((1, 50), (3, null)), map, Look));
        Assert.Throws<InvalidDataException>(() => TerrainLevel.From(Detail((1, 50), (2, 50), (4, null)), map, Look));
        Assert.Throws<InvalidDataException>(() => TerrainLevel.From(Detail((1, 50), (2, 58), (4, null)), map, Look));
        Assert.Throws<InvalidDataException>(() => TerrainLevel.From(Detail((1, null), (2, null)), map, Look));
        var untolerant = Detail((1, 50), (2, null));
        untolerant.Levels.Last().MaxErrorMm = null;
        Assert.Throws<InvalidDataException>(() => TerrainLevel.From(untolerant, map, Look));
    }

    [Fact]
    public void PlainDryBlocksBecomeFansAndBlocksAtTheWaterStayFull()
    {
        // One chunk of 8 x 8 flat cells with a sea cell in the corner (0, 0); blocks of 4.
        var builder = new WorldMapBuilder(8, 1, 1, Biomes);
        for (var z = 0; z < 8; z++)
        {
            for (var x = 0; x < 8; x++)
            {
                builder.Set(x, z, (x, z) == (0, 0) ? new MapCell(15, WaterKind.Salt, 5, -1, 1000, 150, 0, MapCell.Blocked) : new MapCell(30, WaterKind.None, 0, -1, 400, 150, 1, 1000));
            }
        }

        var map = builder.Build();
        var level = TerrainLevel.From(Detail((1, 50), (4, null)), map, Look)[1];
        var fine = TerrainMesher.Ground(map, 0, 0, Look);
        var coarse = TerrainMesher.Ground(level, 0, 0);

        // The block at the sea splits: its quarter with the sea cell keeps its 4 cells and the walls down to the
        // sea cell; the other quarters merge (4, 4 and 2 triangles). Of the three big blocks two keep the base
        // vertices on the chunk border and towards the split block and are fanned from their centre (11
        // triangles each); the far one has two plain edges and is fanned from their corner (8 triangles).
        var atSea = Faces(fine).Where(f => f.X < 2 && f.Z < 2).ToList();
        var coarseKeys = Keys(coarse);
        Assert.All(atSea, f => Assert.Contains(f.Key, coarseKeys));
        Assert.Equal(atSea.Count + 4 + 4 + 2 + 11 + 11 + 8, coarse.TriangleCount);
        Assert.True(coarse.TriangleCount < fine.TriangleCount / 2);
        Assert.All(Faces(coarse).Where(f => f.X >= 2 || f.Z >= 2), f => Assert.All(f.Vertices, v => Assert.Equal(3f, v.Y, 4)));
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    public void CoarseLevelsKeepCoastBanksWaterAndCliffsAndAddNoSkirts(ulong seed)
    {
        var levels = RealLevels(seed);
        var map = levels[0].Base;
        var seaLevel = SeaLevel() * levels[0].Look.UnitM;
        foreach (var level in levels.Skip(1))
        {
            foreach (var (cx, cz) in Chunks(map))
            {
                var fine = Faces(TerrainMesher.Ground(levels[0], cx, cz));
                var fineKeys = fine.Select(f => f.Key).ToHashSet();
                var coarse = Faces(TerrainMesher.Ground(level, cx, cz));
                var coarseKeys = coarse.Select(f => f.Key).ToHashSet();

                // Every cell at the coast, a bank, a stream or a lake (dry land and water in or next to each other)
                // is drawn as on level 0, so the coarse level leaves no water hole and no flooded land; only the
                // bed of the open sea (sea all around) may merge.
                bool OpenSea((float X, float Z, bool Vertical, (float X, float Y, float Z)[] Vertices, string Key) f) => Around(map, f).All(c => map.Cell(c.X, c.Z).Water == WaterKind.Salt);
                Assert.All(fine.Where(f => NearWater(map, f) && !OpenSea(f)), f => Assert.True(coarseKeys.Contains(f.Key), $"seed {seed} step {level.CellStep}: a face at the water at ({f.X:0.0}, {f.Z:0.0}) differs from level 0"));

                // Every other face is either a level-0 face or part of a fan over plain dry land or open sea bed
                // (wholly below the sea); there are no skirts and no walls a level-0 view would not show.
                foreach (var f in coarse.Where(f => !fineKeys.Contains(f.Key)))
                {
                    Assert.False(f.Vertical, $"seed {seed} step {level.CellStep}: a wall at ({f.X:0.0}, {f.Z:0.0}) that level 0 does not have (skirt)");
                    if (NearWater(map, f))
                    {
                        Assert.True(f.Vertices.All(v => v.Y <= seaLevel - level.SeaFloorMarginM + Eps), $"seed {seed} step {level.CellStep}: a merged face at the water at ({f.X:0.0}, {f.Z:0.0}) above the sea floor margin");
                        Assert.True(Around(map, f).All(c => map.Cell(c.X, c.Z).Water == WaterKind.Salt), $"seed {seed} step {level.CellStep}: a merged face at ({f.X:0.0}, {f.Z:0.0}) near the coast or fresh water");
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(2026UL)]
    public void MergedSeaFloorStaysClearlyBelowTheSeaAndWithinTheTolerance(ulong seed)
    {
        var levels = RealLevels(seed);
        var map = levels[0].Base;
        var seaLevel = SeaLevel() * levels[0].Look.UnitM;
        var merged = 0;
        var maxDrop = 0f;
        foreach (var level in levels.Skip(1))
        {
            Assert.True(level.SeaFloorMarginM > 0, "the data keeps merged sea floor below the surface");
            foreach (var (cx, cz) in Chunks(map))
            {
                var fineKeys = Keys(TerrainMesher.Ground(levels[0], cx, cz));
                foreach (var f in Faces(TerrainMesher.Ground(level, cx, cz)).Where(f => !fineKeys.Contains(f.Key) && NearWater(map, f)))
                {
                    merged++;

                    // No merged sea-floor vertex lies above sea level minus the margin ...
                    Assert.All(f.Vertices, v => Assert.True(v.Y <= seaLevel - level.SeaFloorMarginM + Eps, $"seed {seed} step {level.CellStep}: merged sea floor at ({v.X:0.0}, {v.Z:0.0}) lies {v.Y - seaLevel:0.00} m from the sea surface"));

                    // ... the merged floor stays within the height tolerance of the base floor everywhere (sampled
                    // every 1/8 m; the sea colours its bands by the depth, so the switch must not change it) ...
                    var (a, b, c) = (f.Vertices[0], f.Vertices[1], f.Vertices[2]);
                    var det = ((b.Z - c.Z) * (a.X - c.X)) + ((c.X - b.X) * (a.Z - c.Z));
                    for (var sz = MathF.Floor(f.Vertices.Min(v => v.Z)); sz <= f.Vertices.Max(v => v.Z); sz += 0.125f)
                    {
                        for (var sx = MathF.Floor(f.Vertices.Min(v => v.X)); sx <= f.Vertices.Max(v => v.X); sx += 0.125f)
                        {
                            var wa = (((b.Z - c.Z) * (sx - c.X)) + ((c.X - b.X) * (sz - c.Z))) / det;
                            var wb = (((c.Z - a.Z) * (sx - c.X)) + ((a.X - c.X) * (sz - c.Z))) / det;
                            if (wa >= -Eps && wb >= -Eps && wa + wb <= 1 + Eps)
                            {
                                var drawn = (wa * a.Y) + (wb * b.Y) + ((1 - wa - wb) * c.Y);
                                var floor = TerrainMesher.GroundHeight(map, level.Look, sx, sz);
                                maxDrop = Math.Max(maxDrop, floor - drawn);
                                Assert.True(MathF.Abs(floor - drawn) <= level.MaxErrorM + Eps, $"seed {seed} step {level.CellStep}: merged sea floor {floor - drawn:0.000} m below the base floor at ({sx:0.00},{sz:0.00})");
                            }
                        }
                    }

                    // ... and never lies above the base grid's floor at any of its corners.
                    foreach (var (gx, gz, surface) in CornersUnder(f))
                    {
                        var floor = BaseCorner(map, level.Look, gx, gz);
                        Assert.True(surface <= floor + Eps, $"seed {seed} step {level.CellStep}: merged sea floor raised {surface - floor:0.000} m at ({gx},{gz})");
                    }
                }
            }
        }

        Assert.True(merged > 0, $"seed {seed}: some open sea floor is merged");
        Assert.True(maxDrop <= levels.Max(l => l.MaxErrorM), $"seed {seed}: merged sea floor up to {maxDrop:0.000} m below the base floor");
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(2026UL)]
    public void NoTJunctionsInAnyCheckerboardOfTwoLevels(ulong seed)
    {
        // Every pair of levels laid out as a checkerboard of chunks (both ways round): no top face has a
        // vertex of any other top face inside one of its edges, within a chunk or across a chunk border.
        var levels = RealLevels(seed);
        var map = levels[0].Base;
        var meshes = levels.Select(l => Chunks(map).ToDictionary(c => c, c => Faces(TerrainMesher.Ground(l, c.X, c.Z)).Where(f => !f.Vertical).ToList())).ToList();
        for (var a = 0; a < levels.Count; a++)
        {
            for (var b = a + 1; b < levels.Count; b++)
            {
                foreach (var parity in new[] { 0, 1 })
                {
                    var faces = Chunks(map).SelectMany(c => meshes[(c.X + c.Z) % 2 == parity ? a : b][c]).ToList();
                    var vertices = new Dictionary<(int X, int Z), List<float>>();
                    foreach (var v in faces.SelectMany(f => f.Vertices))
                    {
                        var key = ((int)MathF.Round(v.X), (int)MathF.Round(v.Z));
                        (vertices.TryGetValue(key, out var ys) ? ys : vertices[key] = []).Add(v.Y);
                    }

                    var found = 0;
                    foreach (var f in faces)
                    {
                        for (var e = 0; e < 3; e++)
                        {
                            var (u, w) = (f.Vertices[e], f.Vertices[(e + 1) % 3]);
                            int dx = (int)MathF.Round(w.X - u.X), dz = (int)MathF.Round(w.Z - u.Z);
                            var steps = Gcd(Math.Abs(dx), Math.Abs(dz));
                            for (var k = 1; k < steps; k++)
                            {
                                var t = (float)k / steps;
                                var at = ((int)MathF.Round(u.X) + (dx / steps * k), (int)MathF.Round(u.Z) + (dz / steps * k));
                                var y = u.Y + ((w.Y - u.Y) * t);
                                found += vertices.TryGetValue(at, out var ys) && ys.Any(h => MathF.Abs(h - y) < 1e-3f) ? 1 : 0;
                            }
                        }
                    }

                    Assert.True(found == 0, $"seed {seed} steps {levels[a].CellStep}/{levels[b].CellStep} parity {parity}: {found} T-junctions");
                }
            }
        }
    }

    [Fact]
    public void MergedLandStaysWithinTheColourTolerance()
    {
        var levels = RealLevels(7);
        var map = levels[0].Base;
        var checkedCorners = 0;
        foreach (var level in levels.Skip(1))
        {
            var look = level.Look;
            foreach (var (cx, cz) in Chunks(map))
            {
                var fineKeys = Keys(TerrainMesher.Ground(levels[0], cx, cz));
                var mesh = TerrainMesher.Ground(level, cx, cz);
                var faces = Faces(mesh);
                for (var t = 0; t < faces.Count; t++)
                {
                    var f = faces[t];
                    if (fineKeys.Contains(f.Key) || NearWater(map, f))
                    {
                        continue;
                    }

                    // Undo the face's slope tint (the same for its three corners), as the mesher applies it.
                    var up = mesh.Normals[(t * 9) + 1];
                    var steep = Math.Clamp((1f - up) * look.SlopeShadeGain, 0f, 1f) * look.SlopeShade;
                    var colors = Enumerable.Range(0, 3).Select(k =>
                    {
                        var i = ((t * 3) + k) * 3;
                        return new Rgb(
                            (mesh.Colors[i] - (look.Cliff.R * steep)) / (1 - steep),
                            (mesh.Colors[i + 1] - (look.Cliff.G * steep)) / (1 - steep),
                            (mesh.Colors[i + 2] - (look.Cliff.B * steep)) / (1 - steep));
                    }).ToArray();
                    var (a, b, c) = (f.Vertices[0], f.Vertices[1], f.Vertices[2]);
                    var det = ((b.Z - c.Z) * (a.X - c.X)) + ((c.X - b.X) * (a.Z - c.Z));
                    foreach (var (gx, gz, _) in CornersUnder(f))
                    {
                        var wa = (((b.Z - c.Z) * (gx - c.X)) + ((c.X - b.X) * (gz - c.Z))) / det;
                        var wb = (((c.Z - a.Z) * (gx - c.X)) + ((a.X - c.X) * (gz - c.Z))) / det;
                        var wc = 1 - wa - wb;
                        var drawn = new Rgb(
                            (wa * colors[0].R) + (wb * colors[1].R) + (wc * colors[2].R),
                            (wa * colors[0].G) + (wb * colors[1].G) + (wc * colors[2].G),
                            (wa * colors[0].B) + (wb * colors[1].B) + (wc * colors[2].B));
                        var ground = CornerColor(map, look, gx, gz);
                        var delta = Math.Max(Math.Abs(drawn.R - ground.R), Math.Max(Math.Abs(drawn.G - ground.G), Math.Abs(drawn.B - ground.B)));
                        Assert.True(delta <= level.MaxColorDelta + 1e-3f, $"step {level.CellStep}: corner ({gx},{gz}) colour off by {delta:0.000}");
                        checkedCorners++;
                    }
                }
            }
        }

        Assert.True(checkedCorners > 0);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    public void ChunkBordersAreTheSameAtEveryLevel(ulong seed)
    {
        // Each level's chunk border carries exactly the level-0 vertices, so any two levels meet without cracks.
        var levels = RealLevels(seed);
        var map = levels[0].Base;
        var size = map.ChunkCells * levels[0].Look.CellM;
        foreach (var (cx, cz) in Chunks(map))
        {
            HashSet<(float, float, float)> Border(TerrainLevel level) => Vertices(TerrainMesher.Ground(level, cx, cz))
                .Where(v => MathF.Abs(v.X - (cx * size)) < Eps || MathF.Abs(v.X - ((cx + 1) * size)) < Eps || MathF.Abs(v.Z - (cz * size)) < Eps || MathF.Abs(v.Z - ((cz + 1) * size)) < Eps)
                .Select(v => (v.X, v.Y, v.Z)).ToHashSet();
            var reference = Border(levels[0]);
            Assert.All(levels.Skip(1), l => Assert.True(reference.SetEquals(Border(l)), $"seed {seed} step {l.CellStep} chunk ({cx},{cz}): border differs from level 0"));
        }
    }

    [Fact]
    public void TheWaterIsTheSameAtEveryLevel()
    {
        var levels = RealLevels(7);
        foreach (var (cx, cz) in Chunks(levels[0].Base))
        {
            var reference = TerrainMesher.Water(levels[0], cx, cz).Positions;
            Assert.All(levels.Skip(1), l => Assert.Equal(reference, TerrainMesher.Water(l, cx, cz).Positions));
        }
    }

    [Fact]
    public void CoarserLevelsOfTheRealIslandHaveFewerTrianglesWithinTheTolerance()
    {
        var levels = RealLevels(7);
        var map = levels[0].Base;
        var counts = levels.Select(l => Chunks(map).Sum(c => TerrainMesher.Ground(l, c.X, c.Z).TriangleCount)).ToList();
        for (var i = 1; i < levels.Count; i++)
        {
            Assert.True(counts[i] < counts[i - 1], $"level {i}: {counts[i]} ground triangles, finer level {counts[i - 1]}");
        }

        Assert.True(counts[^1] * 5 < counts[0] * 3, $"coarsest level: {counts[^1]} ground triangles, base grid {counts[0]}");

        // Every base grid corner under a merged face of dry land lies within the level's height tolerance of it.
        foreach (var level in levels.Skip(1))
        {
            foreach (var (cx, cz) in Chunks(map).Where(c => (c.X + c.Z) % 2 == 0))
            {
                var fineKeys = Keys(TerrainMesher.Ground(levels[0], cx, cz));
                foreach (var f in Faces(TerrainMesher.Ground(level, cx, cz)).Where(f => !fineKeys.Contains(f.Key) && !NearWater(map, f)))
                {
                    foreach (var (gx, gz, surface) in CornersUnder(f))
                    {
                        var corner = BaseCorner(map, level.Look, gx, gz);
                        Assert.True(MathF.Abs(surface - corner) <= level.MaxErrorM + Eps, $"step {level.CellStep}: corner ({gx},{gz}) lies {surface - corner:0.000} m from the merged face");
                    }
                }
            }
        }
    }

    private static int Gcd(int a, int b) => b == 0 ? Math.Max(a, 1) : Gcd(b, a % b);

    /// <summary>Ground colour of a dry grid corner: the mean of the biome colours of the cells around it.</summary>
    private static Rgb CornerColor(WorldMap map, TerrainLook look, int gx, int gz)
    {
        float r = 0, g = 0, b = 0;
        var count = 0;
        for (var z = gz - 1; z <= gz; z++)
        {
            for (var x = gx - 1; x <= gx; x++)
            {
                if (map.Contains(x, z))
                {
                    var c = look.BiomeColors[map.Cell(x, z).Biome];
                    (r, g, b, count) = (r + c.R, g + c.G, b + c.B, count + 1);
                }
            }
        }

        return new Rgb(r / count, g / count, b / count);
    }

    /// <summary>Height in metres of the base grid corner (gx, gz), from a cell that has it (the map edge included).</summary>
    private static float BaseCorner(WorldMap map, TerrainLook look, int gx, int gz)
    {
        int x = Math.Min(gx, map.WidthCells - 1), z = Math.Min(gz, map.DepthCells - 1);
        var corner = (gx - x, gz - z) switch
        {
            (0, 0) => 0,
            (1, 0) => 1,
            (1, 1) => 2,
            _ => 3,
        };
        return TerrainMesher.CornerHeights(map, x, z, look.CliffDrop)[corner] * look.UnitM;
    }

    /// <summary>The base grid corners a top face covers (cell size 1 m), with the face's height over each.</summary>
    private static IEnumerable<(int X, int Z, float Surface)> CornersUnder((float X, float Z, bool Vertical, (float X, float Y, float Z)[] Vertices, string Key) f)
    {
        var (a, b, c) = (f.Vertices[0], f.Vertices[1], f.Vertices[2]);
        var det = ((b.Z - c.Z) * (a.X - c.X)) + ((c.X - b.X) * (a.Z - c.Z));
        for (var gz = (int)MathF.Floor(f.Vertices.Min(v => v.Z)); gz <= (int)MathF.Ceiling(f.Vertices.Max(v => v.Z)); gz++)
        {
            for (var gx = (int)MathF.Floor(f.Vertices.Min(v => v.X)); gx <= (int)MathF.Ceiling(f.Vertices.Max(v => v.X)); gx++)
            {
                var wa = (((b.Z - c.Z) * (gx - c.X)) + ((c.X - b.X) * (gz - c.Z))) / det;
                var wb = (((c.Z - a.Z) * (gx - c.X)) + ((a.X - c.X) * (gz - c.Z))) / det;
                if (wa >= -Eps && wb >= -Eps && wa + wb <= 1 + Eps)
                {
                    yield return (gx, gz, (wa * a.Y) + (wb * b.Y) + ((1 - wa - wb) * c.Y));
                }
            }
        }
    }

    private static bool NearWater(WorldMap map, (float X, float Z, bool Vertical, (float X, float Y, float Z)[] Vertices, string Key) f) =>
        Around(map, f).Any(c => map.Cell(c.X, c.Z).Water != WaterKind.None);

    /// <summary>The cells a face covers (both sides for a wall on a cell edge) and their eight neighbours.</summary>
    private static IEnumerable<(int X, int Z)> Around(WorldMap map, (float X, float Z, bool Vertical, (float X, float Y, float Z)[] Vertices, string Key) f)
    {
        static (int From, int To) Span(float min, float max) => max - min < Eps
            ? ((int)MathF.Round(min) - 1, (int)MathF.Round(min))
            : ((int)MathF.Floor(min + Eps), (int)MathF.Ceiling(max - Eps) - 1);
        var (x0, x1) = Span(f.Vertices.Min(v => v.X), f.Vertices.Max(v => v.X));
        var (z0, z1) = Span(f.Vertices.Min(v => v.Z), f.Vertices.Max(v => v.Z));
        for (var z = z0 - 1; z <= z1 + 1; z++)
        {
            for (var x = x0 - 1; x <= x1 + 1; x++)
            {
                if (map.Contains(x, z))
                {
                    yield return (x, z);
                }
            }
        }
    }

    private static int SeaLevel() =>
        ContentSet.Load(Path.Combine(TestPaths.Root, "content")).WorldGen.Presets["start_island"].Terrain.SeaLevel;

    private static TerrainDetail Detail(params (int Step, double? Until)[] levels) => new()
    {
        Levels = [.. levels.Select((l, i) => new TerrainDetailLevel { CellStep = l.Step, UntilM = l.Until, MaxErrorMm = i == 0 ? null : 100, MaxColorDeltaPermille = i == 0 ? null : 30 })],
        SeaFloorMarginMm = 500,
        SwitchMarginM = 2,
    };

    private static IEnumerable<(int X, int Z)> Chunks(WorldMap map) =>
        Enumerable.Range(0, map.ChunksZ).SelectMany(z => Enumerable.Range(0, map.ChunksX).Select(x => (x, z)));

    private static IEnumerable<(float X, float Y, float Z, float Ny)> Vertices(MeshData mesh) =>
        Enumerable.Range(0, mesh.VertexCount).Select(i => (mesh.Positions[i * 3], mesh.Positions[(i * 3) + 1], mesh.Positions[(i * 3) + 2], mesh.Normals[(i * 3) + 1]));

    private static List<(float X, float Z, bool Vertical, (float X, float Y, float Z)[] Vertices, string Key)> Faces(MeshData mesh) =>
        [.. Vertices(mesh).Chunk(3).Select(t =>
        {
            var vertices = t.Select(v => (v.X, v.Y, v.Z)).ToArray();
            var key = string.Join(";", vertices.Select(v => FormattableString.Invariant($"{v.X:0.000},{v.Y:0.000},{v.Z:0.000}")).Order(StringComparer.Ordinal));
            return (vertices.Average(v => v.X), vertices.Average(v => v.Z), MathF.Abs(t[0].Ny) < 0.01f, vertices, key);
        })];

    private static HashSet<string> Keys(MeshData mesh) => Faces(mesh).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<TerrainLevel> RealLevels(ulong seed)
    {
        var content = ContentSet.Load(Path.Combine(TestPaths.Root, "content"));
        var island = WorldGenerator.Generate(content.WorldGen, content.Objects, "start_island", seed);
        var terrain = ContractSerializer.Deserialize<LookTerrain>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "terrain.json")));
        var look = TerrainLook.From(
            terrain,
            ContractSerializer.Deserialize<LookPalette>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "palette.json"))),
            island.Map,
            content.WorldGen.Presets["start_island"]);
        var levels = TerrainLevel.From(terrain.Detail, island.Map, look);
        Assert.True(levels.Count >= 2, "the terrain data has at least one coarse level");
        return levels;
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
}
