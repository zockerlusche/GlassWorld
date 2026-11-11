using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// The mouth of a stream (TL #20750, W1738): where a stream meets the sea or a lake its band comes down
/// onto the standing water and ends there - no block above the beach, no band in the air at low tide, no
/// stream that stops short of the water.
/// </summary>
public sealed class StreamMouthTests
{
    private const float Tolerance = 1e-4f;

    private static readonly string[] Biomes = ["low", "high"];

    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(Path.Combine(TestPaths.Root, "content")));

    private static readonly StreamMouthLook Mouth = new(FadeM: 1.5f, ReachCells: 12, UnderM: 0.06f, WidenShare: 0.4f, SeaLevelUnits: 20, TideAmplitudeUnits: 3);

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
        ShoreLiftM: 0.03f,
        Mouth: Mouth);

    private static T Read<T>(params string[] path)
        where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine([TestPaths.Root, "content", .. path])));

    private static (WorldMap Map, TerrainLook Look) RealIsland(ulong seed)
    {
        var map = WorldGenerator.Generate(Content.Value.WorldGen, Content.Value.Objects, "start_island", seed).Map;
        return (map, TerrainLook.From(Read<LookTerrain>("look", "terrain.json"), Read<LookPalette>("look", "palette.json"), map, Content.Value.WorldGen.Presets["start_island"]));
    }

    private static sbyte Flow(int dx, int dz) => (sbyte)FlowDirections.All.ToList().IndexOf((dx, dz));

    /// <summary>
    /// A coast: land falling towards +x, a stream along the row z = 3 that reaches the sea at x = 8, and a
    /// sea floor that shelves gently (<paramref name="floor"/>, height units per sea cell from the coast on).
    /// </summary>
    private static WorldMap Coast(params int[] floor)
    {
        const int Land = 8;
        var size = Land + floor.Length;
        var builder = new WorldMapBuilder(size, 1, 1, Biomes);
        for (var z = 0; z < size; z++)
        {
            for (var x = 0; x < size; x++)
            {
                if (x >= Land)
                {
                    var h = floor[x - Land];
                    builder.Set(x, z, new MapCell(h, WaterKind.Salt, Math.Max(20 - h, 1), -1, 1000, 150, 0, 3000));
                }
                else if (z == 3)
                {
                    builder.Set(x, z, new MapCell(28 - x, WaterKind.Fresh, 2, Flow(1, 0), 1000, 150, 0, 3000));
                }
                else
                {
                    builder.Set(x, z, new MapCell(30 - x, WaterKind.None, 0, -1, 400, 150, 0, 1000));
                }
            }
        }

        return builder.Build();
    }

    /// <summary>A stream down the row z = 3 into a lake of <paramref name="depth"/> units at x = 8..11.</summary>
    private static WorldMap LakeShore(int depth)
    {
        var builder = new WorldMapBuilder(12, 1, 1, Biomes);
        for (var z = 0; z < 12; z++)
        {
            for (var x = 0; x < 12; x++)
            {
                builder.Set(x, z, x >= 8
                    ? new MapCell(20 - depth, WaterKind.Fresh, depth, -1, 1000, 150, 0, 3000)
                    : z == 3
                        ? new MapCell(28 - x, WaterKind.Fresh, 2, Flow(1, 0), 1000, 150, 0, 3000)
                        : new MapCell(30 - x, WaterKind.None, 0, -1, 400, 150, 0, 1000));
            }
        }

        return builder.Build();
    }

    private static float Ground(WorldMap map, TerrainLook look, (float X, float Y, float Z) p) => TerrainMesher.GroundHeight(map, look, p.X, p.Z);

    /// <summary>The islands the mouths are checked on: thirty in a row and the two the other look tests use.</summary>
    public static TheoryData<ulong> Seeds()
    {
        var seeds = new TheoryData<ulong>();
        for (var seed = 1UL; seed <= 30; seed++)
        {
            seeds.Add(seed);
        }

        seeds.Add(42);
        seeds.Add(2026);
        return seeds;
    }

    /// <summary>
    /// The mouth of seed 42 (generator version 5) that ends at (109.5, 11.5), cell for cell: a stream between
    /// two strips of swamp (still water one unit deep on ground of 23 units) runs out to the sea. Its bed is
    /// cut to 20 and 19 units; the first sea cells in its row lie at 19 and 18, but the drawn ground there
    /// is lifted by the swamp ground beside it (shared corner heights) - a bar before the cut bed.
    /// </summary>
    private static WorldMap BarBeforeTheSea()
    {
        int[] row = [19, 18, 18, 17, 16, 15, 14, 14, 13, 12];
        int[] beside = [19, 18, 16, 15, 15, 14, 14, 12, 12];
        const int Size = 16, Stream = 6;
        var builder = new WorldMapBuilder(Size, 1, 1, Biomes);
        for (var z = 0; z < Size; z++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (z == Stream)
                {
                    builder.Set(x, z, x < Stream
                        ? new MapCell(24 - x, WaterKind.Fresh, 2, Flow(1, 0), 1000, 150, 0, 3000)
                        : new MapCell(row[x - Stream], WaterKind.Salt, 20 - row[x - Stream], -1, 1000, 150, 0, 3000));
                }
                else
                {
                    builder.Set(x, z, x <= Stream
                        ? new MapCell(23, WaterKind.Fresh, 1, -1, 1000, 150, 0, 3000)
                        : new MapCell(beside[x - Stream - 1], WaterKind.Salt, 20 - beside[x - Stream - 1], -1, 1000, 150, 0, 3000));
                }
            }
        }

        return builder.Build();
    }

    [Fact]
    public void TheDataLetsAStreamRunToTheLowTideLine()
    {
        var data = Read<LookTerrain>("look", "terrain.json").Water;
        Assert.NotNull(data.StreamMouth);
        Assert.True(data.StreamMouth.FadeM > 0 && data.StreamMouth.ReachCells >= 4);
        Assert.InRange(data.StreamMouth.UnderMm, 10, 300);
        var (_, look) = RealIsland(7);
        var preset = Content.Value.WorldGen.Presets["start_island"];
        Assert.NotNull(look.Mouth);
        Assert.Equal((preset.Terrain.SeaLevel, preset.Tides.Amplitude), (look.Mouth.SeaLevelUnits, look.Mouth.TideAmplitudeUnits));
        Assert.True(preset.Tides.Amplitude > 0, "the start island has tides");

        // The sea surface of the look at a tide is the one the island view shows (TideFollower.AtPermille).
        foreach (var permille in (ReadOnlySpan<int>)[-1000, -400, 0, 1000])
        {
            Assert.Equal((preset.Terrain.SeaLevel + TideFollower.AtPermille(preset.Tides.Amplitude, permille)) * look.UnitM, look.Mouth.SeaLevelM(look.UnitM, permille), 5);
        }
    }

    [Fact]
    public void AtTheSeaTheBandRunsOnOverTheBeachToGroundUnderTheLowestTide()
    {
        // The sea floor shelves: 19, 19, 18, 18, 17, 16, 15 ... units; the lowest tide stands at 17.
        var map = Coast(19, 19, 18, 18, 17, 16, 15, 14, 12, 10);
        var course = Assert.Single(TerrainMesher.StreamCourses(map, Plain));
        Assert.NotNull(course.Mouth);
        Assert.Equal(StreamMouthKind.Sea, course.Mouth.Kind);
        Assert.Equal(2.0f, course.Mouth.LevelM, 5);
        float low = Mouth.SeaLevelM(Plain.UnitM, -1000), high = Mouth.SeaLevelM(Plain.UnitM, 1000);
        Assert.Equal((1.7f, 2.3f), (low, high));

        // The line runs on from its first sea cell (x = 8) down the floor and ends in the first cell whose
        // ground lies under the lowest tide by the margin and the film: not earlier, not later.
        var end = course.Points[^1];
        var under = low - Mouth.UnderM - Plain.ShoreLiftM;
        Assert.True(TerrainMesher.GroundHeight(map, Plain, end.X, end.Z) <= under);
        Assert.True(TerrainMesher.GroundHeight(map, Plain, end.X - 1, end.Z) > under, "it ends as soon as it is under the lowest tide");
        Assert.Equal(3.5, end.Z, 9);
        Assert.Equal(end.X - 8, course.Mouth.LengthM, 6);
        Assert.Contains(((int)end.X, 3), course.Cells);

        var sections = TerrainMesher.StreamSections(map, Plain, course);
        Assert.Equal(course.Points.Count, sections.Count);
        var total = sections[^1].AlongM;
        var crossedLow = false;
        for (var i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            var toEnd = total - s.AlongM;
            var ground = Ground(map, Plain, s.Centre);
            var full = ground + (float)(course.Points[i].Depth * Plain.UnitM);
            if (toEnd >= course.Mouth.LengthM + Mouth.FadeM)
            {
                // Up the stream nothing changes: ground plus the core's depth.
                Assert.InRange(s.Centre.Y, full - Tolerance, full + Tolerance);
            }
            else if (toEnd <= course.Mouth.LengthM)
            {
                // From the coast on: a film on the ground. Nothing stands above the beach.
                Assert.InRange(s.Centre.Y, ground + Plain.ShoreLiftM - Tolerance, ground + Plain.ShoreLiftM + Tolerance);
            }
            else
            {
                // In between the water thins out.
                Assert.InRange(s.Centre.Y, ground + Plain.ShoreLiftM - Tolerance, full + Tolerance);
            }

            // The edges close against the ground everywhere.
            Assert.True(s.Left.Y <= Ground(map, Plain, s.Left) + Plain.ShoreLiftM + Tolerance && s.Right.Y <= Ground(map, Plain, s.Right) + Plain.ShoreLiftM + Tolerance);
            Assert.True(s.Left.Y <= s.Centre.Y + Tolerance && s.Right.Y <= s.Centre.Y + Tolerance);

            // Once the band is under the lowest tide it stays there: the sea covers its end at every tide.
            crossedLow |= s.Centre.Y < low;
            Assert.True(!crossedLow || s.Centre.Y < low, "the band comes up again beyond the low-tide line");
        }

        // Low tide: the band crosses the beach the sea left dry - it is there, on the ground, above the water ...
        var beach = sections.Where(s => total - s.AlongM <= course.Mouth.LengthM && s.Centre.Y > low).ToList();
        Assert.True(beach.Count >= 8, "the stream is missing on the wet beach");
        Assert.True(beach.Max(s => s.Centre.X) - beach.Min(s => s.Centre.X) > 3, "it crosses metres of beach at low tide");

        // ... and ends under it; at high tide all of that, and the last of the cut bed, is simply under the sea.
        Assert.True(sections[^1].Centre.Y <= low - Mouth.UnderM + Tolerance);
        Assert.All(sections.Where(s => total - s.AlongM <= course.Mouth.LengthM), s => Assert.True(s.Centre.Y < high));
        Assert.True(sections[0].Centre.Y > high, "above the high-tide line the stream is still there");

        // The band fans out over the beach: from its width at the coast to the look's share more at its end.
        static float Width(StreamSection s) => MathF.Sqrt(MathF.Pow(s.Left.X - s.Right.X, 2) + MathF.Pow(s.Left.Z - s.Right.Z, 2));
        var coast = sections.Last(s => total - s.AlongM >= course.Mouth.LengthM);
        Assert.Equal(Width(coast) * (1 + Mouth.WidenShare), Width(sections[^1]), 3);
    }

    [Fact]
    public void BehindABarTheWaterStandsLevelAndNeverComesUpAgainBeyondItsWaterline()
    {
        // The shape CI found on seed 42: the ground along the line dips into the cut bed and rises again
        // onto the beach before the sea floor falls. A film that only follows the ground goes under the
        // mean sea in the bed and comes up again on the bar, beyond its own waterline.
        var map = BarBeforeTheSea();
        var look = Plain with { SwampAsGround = true };
        var course = Assert.Single(TerrainMesher.StreamCourses(map, look));
        Assert.Equal(StreamMouthKind.Sea, course.Mouth!.Kind);
        var line = course.Points;
        var sections = TerrainMesher.StreamSections(map, look, course);
        var total = sections[^1].AlongM;
        var ground = sections.Select(s => Ground(map, look, s.Centre)).ToArray();
        float mean = Mouth.SeaLevelM(look.UnitM), high = Mouth.SeaLevelM(look.UnitM, 1000), low = Mouth.SeaLevelM(look.UnitM, -1000);

        // The shape is there: in the last metres before the coast the ground lies under the mean sea, and
        // further down the line it rises above it again by more than the film.
        var dip = Enumerable.Range(0, ground.Length).Where(i => total - sections[i].AlongM > course.Mouth.LengthM).MinBy(i => ground[i]);
        var bar = Enumerable.Range(dip, ground.Length - dip).MaxBy(i => ground[i]);
        Assert.True(ground[dip] + look.ShoreLiftM < mean && ground[bar] > mean, $"bed {ground[dip]:0.000}, bar {ground[bar]:0.000}");
        Assert.True(ground[bar] - ground[dip] > 0.1f);

        // The heights the thinning alone gives go under the mean sea in the bed and come up again on the
        // bar, more than 0.1 m above it (CI reported 0.124 m for the island's mouth) ...
        var thinned = sections.Select((s, i) => TerrainMesher.AtMouth(look, course.Mouth, ground[i], (float)(line[i].Depth * look.UnitM), total - s.AlongM).Surface).ToArray();
        Assert.True(thinned[dip] < mean && thinned[bar] > mean + 0.1f, $"thinned to {thinned[dip]:0.000} in the bed and {thinned[bar]:0.000} on the bar");

        // ... the band does not: from where the highest tide reaches it, it never rises on its way.
        var reached = Array.FindIndex([.. sections], s => s.Centre.Y < high);
        Assert.True(reached >= 0 && reached < dip);
        for (var i = reached + 1; i < sections.Count; i++)
        {
            Assert.True(sections[i].Centre.Y <= sections[i - 1].Centre.Y + Tolerance, $"the band rises {sections[i].Centre.Y - sections[i - 1].Centre.Y:0.000} m at {total - sections[i].AlongM:0.0} m before its end");
        }

        // So at every tide it crosses the sea level once.
        foreach (var level in (ReadOnlySpan<float>)[low, mean, high])
        {
            var under = Array.FindIndex([.. sections], s => s.Centre.Y < level);
            Assert.True(under >= 0);
            Assert.All(sections.Skip(under), s => Assert.True(s.Centre.Y < level));
        }

        // The cut bed is filled up to the bar: the water stands level with the film on it, no deeper than
        // the stream is, and the band runs on across the bar without a break.
        Assert.Equal(ground[bar] + look.ShoreLiftM, sections[bar].Centre.Y, 4);
        Assert.Equal(sections[bar].Centre.Y, sections[dip].Centre.Y, 4);
        Assert.True(sections[dip].Centre.Y <= ground[dip] + (float)(line[dip].Depth * look.UnitM) + Tolerance);
        Assert.All(Enumerable.Range(dip, bar - dip + 1), i => Assert.True(sections[i].Centre.Y >= ground[i] + look.ShoreLiftM - Tolerance, "the band is hidden before the bar"));

        // Beyond the coast it is still a film at most: nothing above the beach.
        Assert.All(Enumerable.Range(0, sections.Count).Where(i => total - sections[i].AlongM <= course.Mouth.LengthM), i => Assert.True(sections[i].Centre.Y <= ground[i] + look.ShoreLiftM + Tolerance));
        Assert.True(sections[^1].Centre.Y <= low - Mouth.UnderM + Tolerance);
    }

    [Fact]
    public void RunningDownToTheSeaTheWaterNeverRises()
    {
        // Heights along a line (head first), the last four points beyond the coast; the highest tide at 2.3.
        float[] along = [0, 1, 2, 3, 4, 5, 6, 7, 8];
        float[] deepest = [3.2f, 2.7f, 2.13f, 2.25f, 2.3f, 2.2f, 2.0f, 2.1f, 1.8f];

        // A dip before the coast (1.96 under 2.12): filled to the level further down. A rise beyond the
        // coast (1.9 after 1.8): the band stays at 1.8 and passes under it.
        float[] heights = [3.0f, 2.5f, 1.96f, 2.05f, 2.12f, 2.0f, 1.8f, 1.9f, 1.6f];
        TerrainMesher.RunDownToTheSea(heights, deepest, along, 3.5, 2.3f);
        Assert.Equal([3.0f, 2.5f, 2.12f, 2.12f, 2.12f, 2.0f, 1.8f, 1.8f, 1.6f], heights);

        // The water never stands deeper than the stream is: where the rise is higher than that, the bed
        // is filled to its depth and the band passes under the rise.
        heights = [3.0f, 2.5f, 1.96f, 2.05f, 2.12f, 2.0f, 1.8f, 1.9f, 1.6f];
        deepest[2] = 2.02f;
        TerrainMesher.RunDownToTheSea(heights, deepest, along, 3.5, 2.3f);
        Assert.Equal([3.0f, 2.5f, 2.02f, 2.02f, 2.02f, 2.0f, 1.8f, 1.8f, 1.6f], heights);

        // Above the reach of the highest tide the heights stay as they are, also where they rise.
        heights = [3.0f, 3.1f, 2.6f, 2.7f, 2.2f, 2.25f, 1.8f, 1.7f, 1.6f];
        float[] same = [.. heights];
        TerrainMesher.RunDownToTheSea(heights, same, along, 3.5, 2.3f);
        Assert.Equal([3.0f, 3.1f, 2.6f, 2.7f, 2.2f, 2.2f, 1.8f, 1.7f, 1.6f], heights);

        // A line that falls all the way is left alone; lists of different lengths are refused.
        float[] falling = [3.0f, 2.5f, 2.2f, 2.0f, 1.9f, 1.8f, 1.7f, 1.6f, 1.5f];
        float[] before = [.. falling];
        TerrainMesher.RunDownToTheSea(falling, [.. falling.Select(h => h + 0.2f)], along, 3.5, 2.3f);
        Assert.Equal(before, falling);
        Assert.Throws<ArgumentException>(() => TerrainMesher.RunDownToTheSea(falling, [1f], along, 3.5, 2.3f));
    }

    [Fact]
    public void WithoutMouthDataAndWithoutASeaFloorThatFallsTheBandEndsWhereItDid()
    {
        // No mouth data: the line ends in the first sea cell, at ground plus depth, as before.
        var map = Coast(19, 19, 18, 18, 17, 16, 15, 14);
        var plain = Plain with { Mouth = null };
        var old = Assert.Single(TerrainMesher.StreamCourses(map, plain));
        Assert.Null(old.Mouth);
        Assert.Equal((8.5, 3.5), (old.Points[^1].X, old.Points[^1].Z));
        var last = TerrainMesher.StreamSections(map, plain, old)[^1];
        Assert.Equal(Ground(map, plain, last.Centre) + 0.2f, last.Centre.Y, 4);

        // A sea floor that rises again above the lowest tide: there is no lower ground to run to, the line
        // ends in its first sea cell - as a film, not as a block.
        var bar = Coast(18, 19, 19, 19);
        var course = Assert.Single(TerrainMesher.StreamCourses(bar, Plain));
        Assert.Equal(8.5, course.Points[^1].X, 9);
        Assert.Equal(0.5, course.Mouth!.LengthM, 9);
        var end = TerrainMesher.StreamSections(bar, Plain, course)[^1];
        Assert.Equal(Ground(bar, Plain, end.Centre) + Plain.ShoreLiftM, end.Centre.Y, 4);

        // Over a level flat it runs straight on, as far as the flat goes.
        Assert.Equal(11.5, Assert.Single(TerrainMesher.StreamCourses(Coast(19, 19, 19, 19), Plain)).Points[^1].X, 9);

        // The reach of the data limits how far it runs on.
        var shortReach = Plain with { Mouth = Mouth with { ReachCells = 2 } };
        Assert.Equal(10.5, Assert.Single(TerrainMesher.StreamCourses(Coast(19, 19, 18, 18, 17, 16, 15, 14), shortReach)).Points[^1].X, 9);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(1)]
    public void InALakeTheBandNeverLiesAboveTheSurface(int depth)
    {
        var map = LakeShore(depth);
        var look = Plain with { SwampMaxUnits = 0 };
        var course = Assert.Single(TerrainMesher.StreamCourses(map, look));
        Assert.NotNull(course.Mouth);
        Assert.Equal(StreamMouthKind.Lake, course.Mouth.Kind);
        Assert.Equal(2.0f, course.Mouth.LevelM, 5);
        Assert.Equal(0.5, course.Mouth.LengthM, 9);
        Assert.Equal(8.5, course.Points[^1].X, 9);
        var sections = TerrainMesher.StreamSections(map, look, course);
        var total = sections[^1].AlongM;
        for (var i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            var ground = Ground(map, look, s.Centre);
            var full = ground + (float)(course.Points[i].Depth * look.UnitM);
            if (total - s.AlongM > course.Mouth.LengthM)
            {
                Assert.InRange(s.Centre.Y, full - Tolerance, full + Tolerance);
                continue;
            }

            // In the lake: under its surface by the margin - or, where the bank is still dry, a film on it.
            Assert.True(s.Centre.Y <= MathF.Max(course.Mouth.LevelM - Mouth.UnderM, ground + look.ShoreLiftM) + Tolerance, $"the band lies {s.Centre.Y - course.Mouth.LevelM:0.000} m above the lake");
            Assert.True(s.Centre.Y <= full + Tolerance);
        }

        // A lake deeper than the stream covers its end; in one as shallow as a puddle the end is a film on its bed.
        Assert.True(depth < 3 || sections[^1].Centre.Y <= course.Mouth.LevelM - Mouth.UnderM + Tolerance, "the band ends under the lake's surface");
    }

    [Fact]
    public void TheHeightAtAMouthFollowsTheDistanceToTheCoast()
    {
        var sea = new StreamMouth(StreamMouthKind.Sea, 2.0f, 3.0);

        // Up the stream and without a mouth: ground plus depth, the width unchanged.
        Assert.Equal((5.2f, 1f), TerrainMesher.AtMouth(Plain, null, 5, 0.2f, 0));
        Assert.Equal((5.2f, 1f), TerrainMesher.AtMouth(Plain with { Mouth = null }, sea, 5, 0.2f, 0));
        Assert.Equal((5.2f, 1f), TerrainMesher.AtMouth(Plain, sea, 5, 0.2f, 3.0 + Mouth.FadeM));

        // The water thins out before the coast (never rising on the way) and is a film from there on.
        var last = 5.2f;
        for (var before = Mouth.FadeM; before >= 0; before -= 0.1f)
        {
            var (surface, widen) = TerrainMesher.AtMouth(Plain, sea, 5, 0.2f, 3.0 + before);
            Assert.InRange(surface, 5.03f - Tolerance, last + Tolerance);
            Assert.Equal(1f, widen);
            last = surface;
        }

        Assert.Equal(5.03f, TerrainMesher.AtMouth(Plain, sea, 5, 0.2f, 3.0).Surface, 5);
        Assert.Equal(5.03f, TerrainMesher.AtMouth(Plain, sea, 5, 0.2f, 1.0).Surface, 5);

        // A stream shallower than the film keeps its depth; the band widens to its end.
        Assert.Equal(5.01f, TerrainMesher.AtMouth(Plain, sea, 5, 0.01f, 1.0).Surface, 5);
        Assert.Equal(1.2f, TerrainMesher.AtMouth(Plain, sea, 5, 0.2f, 1.5).Widen, 5);
        Assert.Equal(1.4f, TerrainMesher.AtMouth(Plain, sea, 5, 0.2f, 0).Widen, 5);

        // A lake: only its own stretch is held under the surface.
        var lake = new StreamMouth(StreamMouthKind.Lake, 5.1f, 0.5);
        Assert.Equal(5.2f, TerrainMesher.AtMouth(Plain, lake, 5, 0.2f, 0.6).Surface, 5);
        Assert.Equal(5.04f, TerrainMesher.AtMouth(Plain, lake, 5, 0.2f, 0.5).Surface, 5);
        Assert.Equal(4.9f, TerrainMesher.AtMouth(Plain, lake, 4.7f, 0.2f, 0).Surface, 5);
        Assert.Equal(5.13f, TerrainMesher.AtMouth(Plain, lake, 5.1f, 0.2f, 0.2).Surface, 5);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TheMouthsOfTheRealIslandsEndAtTheWaterlineAtLowAndAtHighTide(ulong seed)
    {
        var (map, look) = RealIsland(seed);
        var mouth = look.Mouth!;
        float low = mouth.SeaLevelM(look.UnitM, -1000), mean = mouth.SeaLevelM(look.UnitM), high = mouth.SeaLevelM(look.UnitM, 1000);
        Assert.True(low < mean && mean < high);
        var courses = TerrainMesher.StreamCourses(map, look);
        var (seaMouths, lakeMouths, underLowTide, shown, exposed) = (0, 0, 0, 0, 0);
        foreach (var course in courses.Where(c => c.Mouth is not null))
        {
            var sections = TerrainMesher.StreamSections(map, look, course);
            var total = sections[^1].AlongM;
            var name = $"seed {seed}: the stream that ends at ({course.Points[^1].X:0.0}, {course.Points[^1].Z:0.0})";
            for (var i = 0; i < sections.Count; i++)
            {
                // Nowhere higher than before: ground plus the core's depth in the middle, the bank at the edges.
                var s = sections[i];
                Assert.True(s.Centre.Y <= Ground(map, look, s.Centre) + (float)(course.Points[i].Depth * look.UnitM) + Tolerance);
                Assert.True(s.Left.Y <= Ground(map, look, s.Left) + look.ShoreLiftM + Tolerance && s.Right.Y <= Ground(map, look, s.Right) + look.ShoreLiftM + Tolerance);
            }

            var inWater = sections.Where(s => total - s.AlongM <= course.Mouth!.LengthM).ToList();
            Assert.NotEmpty(inWater);
            if (course.Mouth!.Kind == StreamMouthKind.Lake)
            {
                lakeMouths++;
                Assert.All(inWater, s => Assert.True(
                    s.Centre.Y <= MathF.Max(course.Mouth.LevelM - mouth.UnderM, Ground(map, look, s.Centre) + look.ShoreLiftM) + Tolerance,
                    $"{name} lies {s.Centre.Y - course.Mouth.LevelM:0.000} m above the lake at ({s.Centre.X:0.0}, {s.Centre.Z:0.0})"));
                continue;
            }

            seaMouths++;
            Assert.Equal(mean, course.Mouth.LevelM, 5);

            // On the beach and the sea floor the band is a film on the ground: nothing hangs in the air,
            // whatever the tide leaves dry.
            Assert.All(inWater, s => Assert.True(
                s.Centre.Y <= Ground(map, look, s.Centre) + look.ShoreLiftM + Tolerance,
                $"{name} stands {s.Centre.Y - Ground(map, look, s.Centre):0.000} m above the beach at ({s.Centre.X:0.0}, {s.Centre.Z:0.0})"));

            // It ends under the lowest tide - unless the sea floor falls no further there (the edge of the map, a flat).
            var (ex, ez) = ((int)Math.Floor(course.Points[^1].X), (int)Math.Floor(course.Points[^1].Z));
            var falls = FlowDirections.All.Any(d => map.Contains(ex + d.Dx, ez + d.Dz) && map.Cell(ex + d.Dx, ez + d.Dz) is { Water: WaterKind.Salt } n && n.Height < map.Cell(ex, ez).Height);
            var endsUnder = sections[^1].Centre.Y <= low - mouth.UnderM + Tolerance;
            Assert.True(endsUnder || !falls, $"{name} ends {sections[^1].Centre.Y - low:0.000} m above the lowest tide although the sea floor falls on");
            underLowTide += endsUnder ? 1 : 0;

            // At every tide the stream ends at the waterline: once its middle is under the sea it does not
            // come up again further out (no patch of stream beyond the water's edge).
            foreach (var level in (ReadOnlySpan<float>)[low, mean, high])
            {
                var under = false;
                foreach (var s in sections)
                {
                    Assert.True(!under || s.Centre.Y <= level + 0.03f, $"{name} comes up {s.Centre.Y - level:0.000} m out of the sea at {level:0.0} m beyond its waterline");
                    under |= s.Centre.Y < level - 0.03f;
                }
            }

            // The reason it holds: from the first point the highest tide can reach, the water never rises
            // on its way - where the ground rises above water that came down to it, the band passes under
            // it, level, instead of climbing it; it never lies under the ground for any other reason.
            var reached = Array.FindIndex([.. sections], s => s.Centre.Y < high);
            for (var i = Math.Max(reached, 0) + 1; reached >= 0 && i < sections.Count; i++)
            {
                var (here, before) = (sections[i].Centre.Y, sections[i - 1].Centre.Y);
                Assert.True(here <= before + Tolerance, $"{name} rises {here - before:0.000} m at ({sections[i].Centre.X:0.0}, {sections[i].Centre.Z:0.0}) within reach of the tide");
                var film = Ground(map, look, sections[i].Centre) + MathF.Min((float)(course.Points[i].Depth * look.UnitM), look.ShoreLiftM);
                Assert.True(here >= film - Tolerance || here >= before - Tolerance, $"{name} drops under the ground at ({sections[i].Centre.X:0.0}, {sections[i].Centre.Z:0.0})");
            }

            // Low tide: where the sea floor lets it, the band reaches the low-tide line and is there above it.
            if (endsUnder && inWater.Count > 4)
            {
                Assert.Contains(sections, s => s.Centre.Y > low);
            }

            // The stream is not lost on the way: most of what the lowest tide leaves dry of its last
            // stretch shows the band on the ground.
            var dry = Enumerable.Range(0, sections.Count).Where(i => total - sections[i].AlongM <= course.Mouth.LengthM + mouth.FadeM && Ground(map, look, sections[i].Centre) > low).ToList();
            shown += dry.Count(i => sections[i].Centre.Y >= Ground(map, look, sections[i].Centre) - Tolerance);
            exposed += dry.Count;
        }

        Assert.True(seaMouths >= 3, $"seed {seed}: {seaMouths} streams reach the sea");
        Assert.True(underLowTide * 10 >= seaMouths * 8, $"seed {seed}: {underLowTide} of {seaMouths} streams end under the lowest tide");
        Assert.True(shown * 10 >= exposed * 8, $"seed {seed}: the band shows on {shown} of {exposed} points of the beach at low tide");
        _ = lakeMouths;
    }
}
