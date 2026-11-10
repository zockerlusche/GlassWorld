using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// The water look (W1724): springs are stones at the head of a stream, stream lines turn gently and never
/// break off in the middle, the bands carry their place for the flow lines, the weather sets the waves,
/// swamp blends into the ground and reeds stand along the water.
/// </summary>
public sealed class WaterLookTests
{
    private static readonly string[] Biomes = ["low", "high"];

    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(Path.Combine(TestPaths.Root, "content")));

    private static readonly TerrainLook Plain = new(
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

    private static T Read<T>(params string[] path)
        where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine([TestPaths.Root, "content", .. path])));

    private static GeneratedIsland Island(ulong seed) =>
        WorldGenerator.Generate(Content.Value.WorldGen, Content.Value.Objects, "start_island", seed);

    private static TerrainLook LookOf(WorldMap map) =>
        TerrainLook.From(Read<LookTerrain>("look", "terrain.json"), Read<LookPalette>("look", "palette.json"), map, Content.Value.WorldGen.Presets["start_island"]);

    private static MapCell Dry(int height = 40) => new(height, WaterKind.None, 0, -1, 400, 150, 1, 1000);

    private static MapCell Run(sbyte flow, int height = 38) => new(height, WaterKind.Fresh, 2, flow, 1000, 150, 0, 3000);

    private static MapCell Still(int depth, int height = 36) => new(height, WaterKind.Fresh, depth, -1, 1000, 150, 0, 3000);

    private static WorldMap Map(int size, Func<int, int, MapCell> cell)
    {
        var builder = new WorldMapBuilder(size, 1, 1, Biomes);
        for (var z = 0; z < size; z++)
        {
            for (var x = 0; x < size; x++)
            {
                builder.Set(x, z, cell(x, z));
            }
        }

        return builder.Build();
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(2026UL)]
    public void TheStreamsOfTheRealIslandsTurnGentlyWithinTheirCellsAndKeepTheirEnds(ulong seed)
    {
        var map = Island(seed).Map;
        var look = LookOf(map);
        var water = Read<LookTerrain>("look", "terrain.json").Water;
        var limit = water.StreamMaxTurnDegPerM!.Value;
        Assert.True(water.StreamTurnGoalDegPerM < limit, "the relaxing aims below the limit");
        var courses = TerrainMesher.StreamCourses(map, look);

        // The same walk without any smoothing: the polyline through the cells, as the core's flow runs.
        var raw = TerrainMesher.StreamCourses(map, look with { StreamRelaxPasses = 0, StreamSmoothing = 0 });
        Assert.Equal(raw.Count, courses.Count);
        Assert.NotEmpty(courses);
        double worst = 0, worstRaw = 0;
        for (var c = 0; c < courses.Count; c++)
        {
            var (line, cells) = (courses[c].Points, courses[c].Cells.ToHashSet());
            var polyline = raw[c].Points;

            // The ends stay where the cells put them.
            Assert.Equal((polyline[0].X, polyline[0].Z), (line[0].X, line[0].Z));
            Assert.Equal((polyline[^1].X, polyline[^1].Z), (line[^1].X, line[^1].Z));

            // No kink: the turn per metre stays below the limit of the data.
            var turn = TerrainMesher.MaxTurnDegPerM(line);
            Assert.True(turn <= limit, $"seed {seed}: the stream from ({line[0].X:0.0}, {line[0].Z:0.0}) turns {turn:0.0} degrees per metre (limit {limit})");
            (worst, worstRaw) = (Math.Max(worst, turn), Math.Max(worstRaw, TerrainMesher.MaxTurnDegPerM(polyline)));

            // The line never leaves the cells of its stream by more than the tolerance of the data.
            Assert.All(line, p => Assert.True(
                TerrainMesher.DistanceToCells(p.X, p.Z, cells, look.CellM) <= look.StreamCellToleranceM + 1e-6,
                $"seed {seed}: ({p.X:0.00}, {p.Z:0.00}) lies {TerrainMesher.DistanceToCells(p.X, p.Z, cells, look.CellM):0.000} m outside its stream"));

            // The direction stays the core's flow: the line passes every point of the polyline in its order.
            var at = 0;
            foreach (var p in polyline)
            {
                var (best, least) = (at, double.MaxValue);
                for (var i = at; i < line.Count; i++)
                {
                    var distance = Math.Sqrt(Math.Pow(line[i].X - p.X, 2) + Math.Pow(line[i].Z - p.Z, 2));
                    if (distance < least)
                    {
                        (best, least) = (i, distance);
                    }
                }

                Assert.True(least <= look.StreamMaxShiftM + 0.35, $"seed {seed}: the line passes ({p.X:0.0}, {p.Z:0.0}) at {least:0.00} m");
                at = best;
            }
        }

        // The smoothing matters: the cell polylines turn far more sharply than the drawn lines.
        Assert.True(worstRaw > limit && worst < worstRaw * 0.7, $"seed {seed}: raw {worstRaw:0.0}, drawn {worst:0.0} degrees per metre");

        // Every running cell still lies under a band (within a cell diagonal of a centre line).
        var points = courses.SelectMany(c => c.Points).ToList();
        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                if (map.Cell(x, z) is { Water: WaterKind.Fresh, Flow: >= 0 })
                {
                    var (cx, cz) = (x + 0.5, z + 0.5);
                    Assert.True(points.Any(p => ((p.X - cx) * (p.X - cx)) + ((p.Z - cz) * (p.Z - cz)) <= 2.0), $"seed {seed}: running cell ({x}, {z}) has no band");
                }
            }
        }
    }

    [Fact]
    public void TheStreamOfThePictureRunsThroughWithoutABreakAndLeavesItsLakeAtFullWidth()
    {
        // D658 (seed 7): the stream from the lake at (89, 42) down to the swamp broke off in the middle of
        // the picture, where its flow steps into a cell beside its own line near (71, 31), and went on as a
        // second band. It is one line now, from the lake to its mouth.
        var map = Island(7).Map;
        var course = Assert.Single(TerrainMesher.StreamCourses(map, LookOf(map)), c => c.Cells.Contains((73, 31)));
        Assert.Contains((69, 29), course.Cells);
        Assert.Contains((60, 20), course.Cells);

        // It is an outflow: it starts in the lake cell behind its head, at full width, not as a seep.
        Assert.False(course.Source);
        Assert.Equal((89.5, 42.5), (course.Points[0].X, course.Points[0].Z));
        Assert.Equal(WaterKind.Fresh, map.Cell(89, 42).Water);
        Assert.True(map.Cell(89, 42).Flow < 0);
        Assert.InRange(course.Points[0].WidthM, course.Points[^1].WidthM * 0.5, course.Points[^1].WidthM * 1.5);
    }

    [Fact]
    public void AStreamGoesOnThroughACellBesideItsOwnLine()
    {
        // The flow of a two-cell stream wanders between its cells: from (3, 4) north to (3, 2), then back
        // south-west into (2, 3) - a cell that lies beside the line at (3, 3) - and on to the west.
        var map = Map(8, (x, z) => (x, z) switch
        {
            (3, 4) or (3, 3) => Run(2),
            (3, 2) => Run(5),
            ( <= 2, 3) => Run(4),
            _ => Dry(),
        });
        var line = Assert.Single(TerrainMesher.StreamLines(map, Plain with { StreamSmoothing = 0 }));
        Assert.Equal((3.5, 4.5), (line[0].X, line[0].Z));

        // (2, 3) lies beside (3, 3): the line runs between them there, then through (2, 3) itself.
        Assert.Equal([(3.5, 4.5), (3.0, 3.5), (3.5, 2.5), (2.5, 3.5), (1.5, 3.5), (0.5, 3.5)], line.Select(p => (p.X, p.Z)));
    }

    [Fact]
    public void AHeadWithALakeBehindItIsAnOutflowAndEveryOtherHeadASource()
    {
        // A lake cell at (1, 1), a stream leaving it to the east; a second stream springs from dry ground.
        var map = Map(8, (x, z) => (x, z) switch
        {
            (1, 1) => Still(4),
            ( >= 2 and <= 5, 1) => Run(0),
            ( >= 2 and <= 5, 5) => Run(0),
            _ => Dry(),
        });
        var look = Plain with { StreamSmoothing = 0, StreamSourceTaperM = 2, StreamSourceWidthShare = 0.2f };
        var courses = TerrainMesher.StreamCourses(map, look);
        Assert.Equal(2, courses.Count);
        var outflow = courses.Single(c => c.Points[0].Z == 1.5);
        Assert.False(outflow.Source);
        Assert.Equal(1.5, outflow.Points[0].X);
        Assert.Contains((1, 1), outflow.Cells);
        Assert.All(outflow.Points, p => Assert.Equal(look.StreamWidthM, p.WidthM, 6));

        var spring = courses.Single(c => c.Points[0].Z == 5.5);
        Assert.True(spring.Source);
        Assert.Equal(2.5, spring.Points[0].X);
        Assert.Equal(look.StreamWidthM * 0.2, spring.Points[0].WidthM, 6);
        Assert.Equal(look.StreamWidthM, spring.Points[^1].WidthM, 6);

        // Only the spring is a source for the spring look: at the head of its band, running east.
        var source = Assert.Single(TerrainMesher.StreamSources(map, look));
        Assert.Equal((2.5, 5.5), (source.X, source.Z));
        Assert.Equal(1, source.FlowX, 9);
        Assert.Equal(0, source.FlowZ, 9);
    }

    [Fact]
    public void RelaxingRoundsACornerWithinTheCellsAndStopsAtItsGoal()
    {
        // East for five cells, then south: a sharp corner at (5.5, 1.5).
        var map = Map(10, (x, z) => (x, z) switch
        {
            ( <= 4, 1) => Run(0),
            (5, >= 1 and <= 8) => Run(6),
            _ => Dry(),
        });
        var raw = Assert.Single(TerrainMesher.StreamCourses(map, Plain with { StreamSmoothing = 0 }));
        Assert.Equal(90, TerrainMesher.MaxTurnDegPerM(raw.Points), 6);
        Assert.Equal(0, TerrainMesher.MaxTurnDegPerM(raw.Points.Take(5).ToList()), 6);
        Assert.Equal(0, TerrainMesher.MaxTurnDegPerM(raw.Points.Take(2).ToList()), 6);

        var look = Plain with { StreamSmoothing = 0, StreamRelaxPasses = 60, StreamTurnGoalDegPerM = 60, StreamCellToleranceM = 0.2f, StreamMaxShiftM = 0.6f };
        var cells = raw.Cells.ToHashSet();
        var relaxed = TerrainMesher.Relax(raw.Points, cells, look);
        Assert.Equal(raw.Points.Count, relaxed.Count);
        Assert.Equal((raw.Points[0], raw.Points[^1]), (relaxed[0], relaxed[^1]));
        Assert.InRange(TerrainMesher.MaxTurnDegPerM(relaxed), 1, 60);
        Assert.All(relaxed, p => Assert.True(TerrainMesher.DistanceToCells(p.X, p.Z, cells, 1) <= 0.2 + 1e-9));
        Assert.All(relaxed.Zip(raw.Points), pair => Assert.True(Math.Sqrt(Math.Pow(pair.First.X - pair.Second.X, 2) + Math.Pow(pair.First.Z - pair.Second.Z, 2)) <= 0.6 + 1e-9));

        // A goal already met leaves the line alone. A goal out of reach (the cells do not let go of the
        // corner) keeps the smoothest pass: at least as smooth as the line that stopped at its goal.
        Assert.Equal(raw.Points, TerrainMesher.Relax(raw.Points, cells, look with { StreamTurnGoalDegPerM = 95 }));
        var smoothest = TerrainMesher.MaxTurnDegPerM(TerrainMesher.Relax(raw.Points, cells, look with { StreamTurnGoalDegPerM = 1 }));
        Assert.InRange(smoothest, 1.5, TerrainMesher.MaxTurnDegPerM(relaxed));

        // Without a goal every pass is used; without passes nothing moves.
        Assert.NotEqual(raw.Points, TerrainMesher.Relax(raw.Points, cells, look with { StreamTurnGoalDegPerM = 0 }));
        Assert.Same(raw.Points, TerrainMesher.Relax(raw.Points, cells, look with { StreamRelaxPasses = 0 }));

        // The drawn line (relaxed, corners cut, settled) keeps the tolerance at every point and still follows the flow.
        var drawn = Assert.Single(TerrainMesher.StreamCourses(map, look with { StreamSmoothing = 3 })).Points;
        Assert.All(drawn, p => Assert.True(TerrainMesher.DistanceToCells(p.X, p.Z, cells, 1) <= 0.2 + 1e-9));
        for (var i = 1; i < drawn.Count; i++)
        {
            Assert.True(drawn[i].X >= drawn[i - 1].X - 1e-9 && drawn[i].Z >= drawn[i - 1].Z - 1e-9, "the band follows the core's direction");
        }

        Assert.Equal(0.0, TerrainMesher.DistanceToCells(5.5, 3.2, cells, 1));
        Assert.Equal(0.5, TerrainMesher.DistanceToCells(6.5, 3.2, cells, 1), 9);
        Assert.Equal(double.PositiveInfinity, TerrainMesher.DistanceToCells(9.5, 9.5, cells, 1));
    }

    [Fact]
    public void AStreamBandCarriesItsPlaceForTheFlowLines()
    {
        // A straight stream to the south: metres along the line and -1, 0, 1 across it, face by face.
        var map = Map(6, (x, z) => x == 2 && z <= 4 ? Run(6, 40 - z) : Dry(42 - z));
        var line = Assert.Single(TerrainMesher.StreamLines(map, Plain));
        var band = TerrainMesher.Streams(map, Plain);
        Assert.Equal(band.VertexCount * 2, band.Band.Count);
        Assert.Empty(TerrainMesher.Water(map, 0, 0, Plain).Band);
        Assert.Empty(TerrainMesher.Ground(map, 0, 0, Plain).Band);
        for (var i = 0; i < band.VertexCount; i++)
        {
            float x = band.Positions[i * 3], z = band.Positions[(i * 3) + 2];
            var (along, across) = (band.Band[i * 2], band.Band[(i * 2) + 1]);

            // The line runs along z from 0.5: the distance along it is z - 0.5; across is the side of x = 2.5.
            Assert.Equal(z - 0.5f, along, 4);
            Assert.Equal(Math.Sign(x - 2.5f) == 0 ? 0f : across, across);
            Assert.Contains(across, new[] { -1f, 0f, 1f });
            Assert.Equal(MathF.Abs(across) * (float)line[0].WidthM / 2, MathF.Abs(x - 2.5f), 4);
        }

        // Both edges and the centre line occur, and the same side always has the same sign.
        var sides = Enumerable.Range(0, band.VertexCount).Select(i => (Side: Math.Sign(band.Positions[i * 3] - 2.5f), Across: band.Band[(i * 2) + 1])).Distinct().ToList();
        Assert.Equal(3, sides.Count);
        Assert.Equal(3, sides.Select(s => s.Side).Distinct().Count());
    }

    [Fact]
    public void SwampIsWetGroundThatBlendsIntoTheLandInsteadOfAPlateWithAHardEdge()
    {
        // A swamp of four cells (still fresh water, one unit deep) in dry land, and a lake cell apart from it.
        MapCell Cell(int x, int z) => (x, z) switch
        {
            (2 or 3, 2 or 3) => Still(1, 39),
            (6, 6) => Still(5, 35),
            _ => Dry(),
        };
        var map = Map(8, Cell);
        var plates = TerrainMesher.Water(map, 0, 0, Plain);
        Assert.Contains(Colors(plates), c => c == Plain.Swamp);

        var soft = Plain with { SwampAsGround = true };
        Assert.True(soft.IsSwampGround(map.Cell(2, 2)));
        Assert.False(soft.IsSwampGround(map.Cell(6, 6)));
        Assert.False(Plain.IsSwampGround(map.Cell(2, 2)));
        var water = TerrainMesher.Water(map, 0, 0, soft);
        Assert.DoesNotContain(Colors(water), c => c == Plain.Swamp);
        Assert.Contains(Colors(water), c => c == Plain.Fresh);

        // No water surface lies over the swamp or its banks any more (the lake keeps its own).
        for (var i = 0; i < water.VertexCount; i++)
        {
            Assert.True(water.Positions[i * 3] >= 5 && water.Positions[(i * 3) + 2] >= 5, "only the lake is drawn as water");
        }

        // The ground carries the swamp colour instead: pure in the middle (the corner all four swamp cells
        // share), pure land colour away from it, and blends at the corners in between - a soft border as
        // between two biomes, not a step along the cell edges.
        var ground = TerrainMesher.Ground(map, 0, 0, soft);
        var land = Plain.BiomeColors[1];
        var around = Enumerable.Range(0, ground.VertexCount)
            .Select(i => (X: ground.Positions[i * 3], Z: ground.Positions[(i * 3) + 2], Color: new Rgb(ground.Colors[i * 3], ground.Colors[(i * 3) + 1], ground.Colors[(i * 3) + 2])))
            .Where(v => v.X <= 5 && v.Z <= 5).ToList();
        Assert.All(around.Where(v => v.X == 3f && v.Z == 3f), v => Assert.True(Near(v.Color, Plain.Swamp, 0.02f)));
        Assert.All(around.Where(v => v.X == 0f && v.Z == 0f), v => Assert.True(Near(v.Color, land, 0.02f)));
        var edge = around.Where(v => v.X == 2f && v.Z == 3f).ToList();
        Assert.NotEmpty(edge);
        Assert.All(edge, v => Assert.True(!Near(v.Color, Plain.Swamp, 0.05f) && !Near(v.Color, land, 0.05f), "the border corner is a blend"));

        // The same corner under the old plates: plain land colour mixed with the bed, never the swamp colour.
        var before = TerrainMesher.Ground(map, 0, 0, Plain);
        Assert.DoesNotContain(Colors(before), c => Near(c, Plain.Swamp, 0.02f));
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    public void TheSwampOfTheRealIslandHasNoWaterPlates(ulong seed)
    {
        var map = Island(seed).Map;
        var look = LookOf(map);
        Assert.True(look.SwampAsGround);
        var swamp = 0;
        for (var cz = 0; cz < map.ChunksZ; cz++)
        {
            for (var cx = 0; cx < map.ChunksX; cx++)
            {
                var water = TerrainMesher.Water(map, cx, cz, look, WaterKind.Fresh);
                Assert.DoesNotContain(Colors(water), c => c == look.Swamp);
                swamp += TerrainMesher.Water(map, cx, cz, look with { SwampAsGround = false }, WaterKind.Fresh).TriangleCount - water.TriangleCount;
            }
        }

        Assert.True(swamp > 0, $"seed {seed}: the island has swamp that used to be drawn as plates");
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    public void ReedsAndBankPlantsStandAlongTheFreshWater(ulong seed)
    {
        var data = Read<LookObjects>("look", "objects.json");
        var island = Island(seed);
        var map = island.Map;
        var all = Decor.Plan(data.Decor, island, 1.0);
        var bank = data.Decor!.Select((d, i) => (Kind: d, Number: i + 1)).Where(k => k.Kind.FreshWaterWithinCells is not null).ToList();
        Assert.True(bank.Count >= 2, "reeds and at least one more bank plant");
        Assert.Contains(bank, k => k.Kind.Type == "decor_reed");

        // The bank plants come last in the data, so they change no plant that stood before them.
        var before = Decor.Plan([.. data.Decor!.Where(d => d.FreshWaterWithinCells is null)], island, 1.0);
        Assert.Equal(before.Select(p => (p.Id, p.TypeId, p.Position)), all.Take(before.Count).Select(p => (p.Id, p.TypeId, p.Position)));
        Assert.Equal(all.Select(p => (p.Id, p.Position)), Decor.Plan(data.Decor, island, 1.0).Select(p => (p.Id, p.Position)));

        var grass = Read<LookNature>("look", "nature.json").Grass.Types;
        foreach (var (kind, number) in bank)
        {
            // Drawn with the grass: in chunks with detail levels, not one by one.
            Assert.Contains(kind.Type, grass);
            var plants = all.Where(p => p.TypeId == kind.Type).ToList();
            Assert.True(plants.Count >= 150, $"seed {seed}: only {plants.Count} {kind.Type}");
            var reach = kind.FreshWaterWithinCells!.Value;
            foreach (var plant in plants)
            {
                // The id names the cell of the group centre: fresh water lies within the reach of the data.
                var parts = plant.Id.Split('-');
                Assert.Equal(number.ToString(System.Globalization.CultureInfo.InvariantCulture), parts[1]);
                var (gx, gz) = (int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture));
                var wet = false;
                for (var z = gz - reach; z <= gz + reach && !wet; z++)
                {
                    for (var x = gx - reach; x <= gx + reach && !wet; x++)
                    {
                        wet = map.Contains(x, z) && map.Cell(x, z).Water == WaterKind.Fresh;
                    }
                }

                Assert.True(wet, $"{plant.Id} stands away from fresh water");
                Assert.Equal(WaterKind.None, map.Cell((int)plant.Position.X, (int)plant.Position.Z).Water);
            }
        }

        // Streams and springs carry them, not only lakes: most running cells have a bank plant within three metres.
        var banks = all.Where(p => bank.Any(k => k.Kind.Type == p.TypeId)).Select(p => p.Position).ToList();
        var running = new List<(int X, int Z)>();
        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                if (map.Cell(x, z) is { Water: WaterKind.Fresh, Flow: >= 0 })
                {
                    running.Add((x, z));
                }
            }
        }

        var planted = running.Count(c => banks.Any(p => Math.Pow(p.X - (c.X + 0.5), 2) + Math.Pow(p.Z - (c.Z + 0.5), 2) <= 9));
        Assert.True(planted * 10 >= running.Count * 6, $"seed {seed}: bank plants near {planted} of {running.Count} stream cells");
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    public void ASpringAtAStreamIsAHeapOfStonesAtTheStartOfTheBandAndAPondElsewhere(ulong seed)
    {
        var data = Read<LookObjects>("look", "objects.json");
        var island = Island(seed);
        var map = island.Map;
        var look = LookOf(map);
        var looks = new ObjectLooks(data, Read<LookPalette>("look", "palette.json").Colors.Select(c => c.Id).ToHashSet(StringComparer.Ordinal));
        var sources = TerrainMesher.StreamSources(map, look);
        var springs = map.Hydrology!.Springs().ToHashSet();
        var objects = island.Objects.Where(o => o.TypeId == "spring_water")
            .Select((o, i) => (Object: new WorldObject($"s{i}", o.TypeId, new Position(o.X + 0.5, o.Z + 0.5)), AtSpring: springs.Contains((o.X, o.Z)))).ToList();
        Assert.Contains(objects, o => o.AtSpring);
        Assert.Contains(objects, o => !o.AtSpring);

        // The data: the stones are a visual of their own, which is no object type of the catalogue.
        var stones = looks.For(data.StreamSources!.AsType);
        Assert.Equal(ObjectLookShape.Spring, stones.Shape);
        Assert.True(stones.Lies);
        Assert.False(Content.Value.Objects.Contains(data.StreamSources.AsType));
        Assert.Equal(ObjectLookShape.Pool, looks.For("spring_water").Shape);

        // Without the sources of an island every drinking place is the pond.
        Func<double, double, double> ground = (x, z) => IslandGround(map, look, x, z);
        Assert.All(objects, o => Assert.Null(looks.SourceOf(o.Object)));
        Assert.DoesNotContain(stones.VisualId, looks.Layout(objects.Select(o => o.Object), ground).Keys);

        looks.UseStreamSources(sources);
        var layout = looks.Layout(objects.Select(o => o.Object), ground);
        var heaps = layout[stones.VisualId].ToDictionary(p => p.ObjectId, StringComparer.Ordinal);
        var ponds = layout[looks.For("spring_water").VisualId].Select(p => p.ObjectId).ToHashSet(StringComparer.Ordinal);
        foreach (var (obj, atSpring) in objects)
        {
            if (!atSpring)
            {
                // A drinking place in a hollow or at a lake: the pond, where the core put it.
                Assert.Contains(obj.Id, ponds);
                continue;
            }

            // A spring of the core: the heap of stones, at the point of a stream band the water comes out
            // at, its local +x side against the flow, so the water runs off at its open side.
            var source = looks.SourceOf(obj);
            Assert.True(source is not null, $"seed {seed}: the spring at ({obj.Position.X}, {obj.Position.Z}) has no stream source");
            var placed = heaps[obj.Id];
            Assert.Equal((source!.Value.X, source.Value.Z), (placed.X, placed.Z));
            Assert.InRange(Math.Sqrt(Math.Pow(placed.X - obj.Position.X, 2) + Math.Pow(placed.Z - obj.Position.Z, 2)), 0, data.StreamSources.MaxDistanceM);
            var (ax, _, _) = placed.Axes();
            var along = ((ax.X * source.Value.FlowX) + (ax.Z * source.Value.FlowZ)) / Math.Sqrt((ax.X * ax.X) + (ax.Z * ax.Z));
            Assert.True(along < -0.99, $"the heap at ({placed.X:0.0}, {placed.Z:0.0}) is not turned against the flow ({along:0.000})");

            // The source lies on a band: a point of a stream line is there.
            Assert.Contains(TerrainMesher.StreamLines(map, look).SelectMany(l => l), p => Math.Abs(p.X - placed.X) < 1e-9 && Math.Abs(p.Z - placed.Z) < 1e-9);
        }

        // The same world, the same places.
        Assert.Equal(layout[stones.VisualId], looks.Layout(objects.Select(o => o.Object), ground)[stones.VisualId]);
    }

    [Fact]
    public void ThePondHasNoRingOfItsOwnColour()
    {
        // TL #20434: the rim of the pond takes the colour of the bank around fresh water, not a colour of its own.
        var pond = Read<LookObjects>("look", "objects.json").Visuals.Single(v => v.Id == "spring");
        var bank = Read<LookTerrain>("look", "terrain.json").BiomeColors.Single(b => b.Biome == "freshwater").Color;
        Assert.Equal(bank, pond.Colors.ElementAt(2));
    }

    [Fact]
    public void TheWeatherSetsTheWavesFromNoneToLarge()
    {
        var weather = Read<LookWeather>("look", "weather.json");
        var slots = SeasonSlots.From(Read<LookPalette>("look", "palette.json"));
        double Waves(string id) => WeatherMix.Of(weather, slots, id).Waves;

        // None in clear calm weather, small under clouds and in rain, large in a storm.
        Assert.Equal(0, Waves("clear"));
        Assert.InRange(Waves("cloudy"), 0.1, 0.5);
        Assert.InRange(Waves("rain"), Waves("cloudy"), 0.7);
        Assert.Equal(1, Waves("storm"));
        Assert.Equal(0, WeatherMix.Neutral(slots).Waves);
        Assert.All(weather.Looks, l => Assert.InRange(l.Waves ?? 0, 0, 1));

        // A change of weather is a soft way from one to the other, and waves alone tell two looks apart.
        var (calm, storm) = (WeatherMix.Of(weather, slots, "clear"), WeatherMix.Of(weather, slots, "storm"));
        Assert.Equal(0.25, calm.Lerp(storm, 0.25).Waves, 9);
        Assert.Equal(0.75, storm.Lerp(calm, 0.25).Waves, 9);
        Assert.NotEqual(calm, calm with { Waves = 0.5 });

        // The crest layers of the terrain look: small crests come first and are full before the large ones start.
        var waves = Read<LookTerrain>("look", "terrain.json").Waves!;
        Assert.All(new[] { waves.Small, waves.Large }, l => Assert.True(l.FullAt > l.StartsAt && l.ThicknessM < l.SpacingM / 2));
        Assert.True(waves.Small.StartsAt < Waves("cloudy") && waves.Small.FullAt <= waves.Large.StartsAt + 1e-9 && waves.Large.FullAt <= Waves("storm"));
        Assert.True(waves.Small.SpacingM < waves.Large.SpacingM && waves.Small.LengthM < waves.Large.LengthM);
        Assert.True(Waves("cloudy") < waves.Large.StartsAt, "small waves show no large crest");
        Assert.InRange(waves.LakeShare, 0.1, 1);
    }

    [Fact]
    public void TheWaterShotSetShowsTheViewsTheLookIsJudgedBy()
    {
        var shots = Read<LookShots>("look", "shots_water.json");
        var seasons = Read<LookPalette>("look", "palette.json").Seasons.Select(s => s.Season).ToHashSet(StringComparer.Ordinal);
        var weather = Read<LookWeather>("look", "weather.json").Looks.Select(l => l.Weather).ToHashSet(StringComparer.Ordinal);
        var plan = ShotPlan.Build(shots, seasons, weather);
        Assert.Equal(["water_view", "spring_close", "stream_bend", "sea_clear", "sea_storm", "lake_clear", "lake_storm"], plan.Select(s => s.Point.Id));
        Assert.All(plan, s => Assert.True(s is { Season: "summer", SeasonHasLook: true, WeatherHasLook: true, Phase: "day" }));
        Assert.Equal([null, null, null, "clear", "storm", "clear", "storm"], plan.Select(s => s.Weather));
        Assert.Equal("island_sea_storm_perspective_day_summer_storm_800x600.png", plan[4].FileName(800, 600));

        // The first view is the view of the season set (D658), found by the same scene.
        var season = Read<LookShots>("look", "shots_seasons.json").Points.Single();
        var view = shots.Points.First();
        Assert.Equal((season.DistanceM, season.YawDeg, season.Scene!.RadiusCells, season.Scene.MinStreamCells, season.Scene.MinRelief, season.Scene.MaxStartDistanceCells), (view.DistanceM, view.YawDeg, view.Scene!.RadiusCells, view.Scene.MinStreamCells, view.Scene.MinRelief, view.Scene.MaxStartDistanceCells));

        // On the island of the pictures (seed 7) the close views look at what they are named for.
        var map = Island(7).Map;
        var look = LookOf(map);
        (double X, double Z) Target(string id) => (shots.Points.Single(p => p.Id == id).TargetXPermille / 1000.0 * map.WidthCells, shots.Points.Single(p => p.Id == id).TargetZPermille / 1000.0 * map.DepthCells);
        var spring = Target("spring_close");
        Assert.Contains(TerrainMesher.StreamSources(map, look), s => Math.Abs(s.X - spring.X) < 1 && Math.Abs(s.Z - spring.Z) < 1);
        var bend = Target("stream_bend");
        Assert.Contains(TerrainMesher.StreamLines(map, look).SelectMany(l => l), p => Math.Abs(p.X - bend.X) < 1 && Math.Abs(p.Z - bend.Z) < 1);
        var lake = Target("lake_clear");
        Assert.True(map.Cell((int)lake.X, (int)lake.Z) is { Water: WaterKind.Fresh, Flow: < 0 } cell && cell.WaterDepth > look.SwampMaxUnits);
        Assert.Equal(Target("lake_clear"), Target("lake_storm"));
        Assert.Equal(Target("sea_clear"), Target("sea_storm"));
    }

    private static double IslandGround(WorldMap map, TerrainLook look, double x, double z) => TerrainMesher.GroundHeight(map, look, x, z);

    private static bool Near(Rgb a, Rgb b, float tolerance) =>
        MathF.Abs(a.R - b.R) <= tolerance && MathF.Abs(a.G - b.G) <= tolerance && MathF.Abs(a.B - b.B) <= tolerance;

    private static IEnumerable<Rgb> Colors(MeshData mesh) =>
        Enumerable.Range(0, mesh.VertexCount).Select(i => new Rgb(mesh.Colors[i * 3], mesh.Colors[(i * 3) + 1], mesh.Colors[(i * 3) + 2]));
}
