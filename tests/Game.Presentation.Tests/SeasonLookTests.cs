using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// The season look as shader parameters (B2-5 R24): which colours a season moves, that moving them in the
/// shader shows what a rebuild with the season palette would, and the blend in real time (which seasons,
/// how far, continuous also at x16).
/// </summary>
public sealed class SeasonLookTests
{
    private static readonly LookPalette Palette = Read<LookPalette>("palette.json");

    private static readonly SeasonSlots Slots = SeasonSlots.From(Palette);

    private static readonly WorldCalendar Calendar = WorldCalendar.FromJson(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "world", "time.json")));

    [Fact]
    public void TheSlotsAreTheColoursTheSeasonsChange()
    {
        var seasonal = Palette.Seasons.SelectMany(s => s.Colors).Select(c => c.Id).Distinct().ToList();
        Assert.Equal(seasonal.Order(StringComparer.Ordinal), Slots.Ids.Order(StringComparer.Ordinal));
        Assert.InRange(Slots.Ids.Count, 1, SeasonSlots.Max);
        Assert.Equal(Slots.Ids.Select(id => Rgb.Parse(Palette.Colors.Single(c => c.Id == id).Color)), Slots.BaseColors);
        Assert.Equal(-1, Slots.IndexOf("rock"));
        Assert.Equal(default, Slots.WeightsOf("rock"));
        Assert.Equal(1f, Slots.WeightsOf(Slots.Ids[0])[0]);

        // Five seasonal colours do not fit the four channels of the vertex attribute: a data error, not a silent cut.
        var tooMany = Read<LookPalette>("palette.json");
        tooMany.Seasons.First().Colors = [.. tooMany.Colors.Take(5).Select(c => new LookColor { Id = c.Id, Color = "#000000" })];
        Assert.Throws<InvalidDataException>(() => SeasonSlots.From(tooMany));
    }

    [Fact]
    public void ASeasonShowsItsColoursSnowAndLeavesFromTheData()
    {
        foreach (var season in Palette.Seasons)
        {
            var mix = SeasonMix.Of(Palette, Slots, season.Season);
            Assert.Equal(season.SnowCover, mix.SnowCover);
            Assert.Equal(season.LeafFall, mix.LeafFall);
            for (var i = 0; i < Slots.Ids.Count; i++)
            {
                var expected = season.Colors.FirstOrDefault(c => c.Id == Slots.Ids[i])?.Color ?? Palette.Colors.Single(c => c.Id == Slots.Ids[i]).Color;
                Assert.Equal(Rgb.Parse(expected), mix.Colors[i]);
            }
        }

        // No season, or one without an entry: the base colours; an epoch's colour wins over the season.
        Assert.Equal(new SeasonMix(Slots.BaseColors, 0, 0), SeasonMix.Of(Palette, Slots, null));
        Assert.Equal(SeasonMix.Of(Palette, Slots, null), SeasonMix.Of(Palette, Slots, "monsoon"));
        var kept = SeasonMix.Of(Palette, Slots, "autumn", new HashSet<string>(["foliage"], StringComparer.Ordinal));
        Assert.Equal(Slots.BaseColors[Slots.IndexOf("foliage")], kept.Colors[Slots.IndexOf("foliage")]);
        Assert.NotEqual(Slots.BaseColors[Slots.IndexOf("grass")], kept.Colors[Slots.IndexOf("grass")]);
    }

    [Fact]
    public void MixesAreEqualByValueSoTheShaderParametersAreWrittenOnlyWhenSomethingChanged()
    {
        // The view compares the mix of this frame with the one it applied (review GW3 #181): two mixes
        // with the same values in different lists must be equal, any changed value must not.
        var autumn = SeasonMix.Of(Palette, Slots, "autumn");
        var same = new SeasonMix([.. autumn.Colors], autumn.SnowCover, autumn.LeafFall);
        Assert.NotSame(autumn.Colors, same.Colors);
        Assert.True(autumn.Equals(same) && autumn == same && Equals(autumn, same));
        Assert.Equal(autumn.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(autumn, same with { SnowCover = autumn.SnowCover + 0.01 });
        Assert.NotEqual(autumn, same with { LeafFall = autumn.LeafFall + 0.01 });
        Assert.NotEqual(autumn, same with { Colors = [.. autumn.Colors.Select((c, i) => i == 0 ? c with { G = c.G + 0.01f } : c)] });
        Assert.False(autumn.Equals(null));

        // Frame after frame of the same season: the blender hands out an equal mix, during a blend a different one.
        var blender = new SeasonBlender(Palette, Slots);
        var first = blender.Update("autumn", 0.016);
        Assert.Equal(first, blender.Update("autumn", 0.016));
        Assert.Equal(first, blender.Update("autumn", 5));
        var blending = blender.Update("winter", 0.016);
        Assert.NotEqual(first, blending);
        var next = blender.Update("winter", 0.016);
        Assert.NotEqual(blending, next);

        // A paused frame in the middle of the blend: equal again, nothing to write.
        Assert.Equal(next, blender.Update("winter", 0));
    }

    [Theory]
    [InlineData("spring")]
    [InlineData("autumn")]
    [InlineData("winter")]
    public void MovingTheSeasonalSharesShowsWhatARebuildWithTheSeasonPaletteWould(string season)
    {
        // The ground of the real island, built once with the base palette: every vertex colour moved by its
        // seasonal shares must be the colour the mesher gives with the season palette (the old rebuild).
        var content = ContentSet.Load(Path.Combine(TestPaths.Root, "content"));
        var island = WorldGenerator.Generate(content.WorldGen, content.Objects, "start_island", 7);
        var terrain = Read<LookTerrain>("terrain.json");
        var preset = content.WorldGen.Presets["start_island"];
        var baseLook = TerrainLook.From(terrain, Palette, island.Map, preset);
        var seasonLook = TerrainLook.From(terrain, ShotPlan.PaletteFor(Palette, Read<LookEpochs>("epochs.json"), null, season), island.Map, preset);
        var mix = SeasonMix.Of(Palette, Slots, season);
        var (vertices, moved, worst) = (0, 0, 0f);
        for (var cz = 0; cz < island.Map.ChunksZ; cz += 3)
        {
            for (var cx = 0; cx < island.Map.ChunksX; cx += 3)
            {
                var built = TerrainMesher.Ground(island.Map, cx, cz, baseLook);
                var rebuilt = TerrainMesher.Ground(island.Map, cx, cz, seasonLook);
                Assert.Equal(rebuilt.Positions, built.Positions);
                Assert.Equal(built.VertexCount * 4, built.Tints.Count);
                for (var v = 0; v < built.VertexCount; v++)
                {
                    var tint = new TintWeights(built.Tints[v * 4], built.Tints[(v * 4) + 1], built.Tints[(v * 4) + 2], built.Tints[(v * 4) + 3]);
                    var shown = mix.Shade(new Rgb(built.Colors[v * 3], built.Colors[(v * 3) + 1], built.Colors[(v * 3) + 2]) { Tint = tint }, Slots);
                    var want = new Rgb(rebuilt.Colors[v * 3], rebuilt.Colors[(v * 3) + 1], rebuilt.Colors[(v * 3) + 2]);
                    worst = Math.Max(worst, Math.Max(Math.Abs(shown.R - want.R), Math.Max(Math.Abs(shown.G - want.G), Math.Abs(shown.B - want.B))));
                    vertices++;
                    moved += tint.A + tint.B + tint.C + tint.D > 0 ? 1 : 0;
                }
            }
        }

        Assert.True(vertices > 10000 && moved > vertices / 20, $"{moved} of {vertices} vertices carry a seasonal colour");
        Assert.True(worst < 1.5f / 255, $"largest difference to the rebuild: {worst * 255:0.00} of 255");
    }

    [Fact]
    public void TheFirstSeasonShowsAtOnceAndAChangeBlendsInRealTime()
    {
        var blender = new SeasonBlender(Palette, Slots);
        Assert.Equal(SeasonMix.Of(Palette, Slots, null), blender.Shown);
        Assert.Equal(SeasonMix.Of(Palette, Slots, "summer"), blender.Update("summer", 0.016));
        Assert.Equal(new SeasonBlend("summer", "summer", 1), blender.Blend);

        // The season of the snapshot changes: summer -> autumn over season_blend_real_seconds.
        var seconds = Palette.SeasonBlendRealSeconds;
        var (summer, autumn) = (SeasonMix.Of(Palette, Slots, "summer"), SeasonMix.Of(Palette, Slots, "autumn"));
        Assert.Equal(summer, blender.Update("autumn", 0));
        Assert.Equal(("summer", "autumn", 0.0), (blender.Blend.From, blender.Blend.To, blender.Blend.T));
        var half = blender.Update("autumn", seconds / 2);
        Assert.Equal(0.5, blender.Blend.T, 6);
        Assert.Equal(autumn.LeafFall / 2, half.LeafFall, 6);
        Assert.Equal(summer.Colors[0].Lerp(autumn.Colors[0], 0.5f), half.Colors[0]);

        // Paused (no real time passes for the look): nothing moves.
        Assert.Equal(half, blender.Update("autumn", 0));
        Assert.Equal(autumn, blender.Update("autumn", seconds));
        Assert.Equal(new SeasonBlend("autumn", "autumn", 1), blender.Blend);

        // A jump (load, screenshot tool) shows a season at once.
        Assert.Equal(SeasonMix.Of(Palette, Slots, "winter"), blender.Jump("winter"));
        Assert.Equal(new SeasonBlend("winter", "winter", 1), blender.Blend);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(4000)]
    public void TheLookNeverJumpsThroughAWholeYearAtAnySpeed(double speed)
    {
        // Frames of 1/60 s through more than a year: at x16 a season still lasts far longer than the
        // blend; at an absurd speed the seasons change in the middle of a blend. The look may never jump.
        const double Frame = 1.0 / 60;
        var ticksPerFrame = speed * Frame / Calendar.RealSecondsPerTickAtNormalSpeed;
        var wholeYear = Calendar.TicksPerYear * 1.2 / ticksPerFrame;
        var frames = (int)Math.Min(wholeYear, 400_000);
        var blender = new SeasonBlender(Palette, Slots);
        var previous = blender.Update(Calendar.At(0).SeasonId, Frame);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // The steepest part of the eased blend moves 1.5 / duration per second.
        var perFrame = (1.5 * Frame / Palette.SeasonBlendRealSeconds) + 1e-6;
        for (var f = 1; f <= frames; f++)
        {
            var season = Calendar.At((long)(f * ticksPerFrame)).SeasonId;
            var mix = blender.Update(season, Frame);
            seen.Add(blender.Blend.To!);
            Assert.Equal(season, blender.Blend.To);
            Assert.InRange(Math.Abs(mix.SnowCover - previous.SnowCover), 0, perFrame);
            Assert.InRange(Math.Abs(mix.LeafFall - previous.LeafFall), 0, perFrame);
            for (var i = 0; i < mix.Colors.Count; i++)
            {
                Assert.InRange(Math.Abs(mix.Colors[i].R - previous.Colors[i].R), 0, perFrame);
                Assert.InRange(Math.Abs(mix.Colors[i].G - previous.Colors[i].G), 0, perFrame);
                Assert.InRange(Math.Abs(mix.Colors[i].B - previous.Colors[i].B), 0, perFrame);
            }

            previous = mix;
        }

        if (frames >= wholeYear - 1)
        {
            Assert.Equal(Calendar.Settings.Seasons.Select(s => s.Id).Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void AtNormalSpeedAndAtX16ASeasonIsFullyShownBeforeTheNextBegins()
    {
        foreach (var speed in new[] { 1.0, 16.0 })
        {
            var realSecondsPerSeason = Calendar.TicksPerSeason * Calendar.RealSecondsPerTickAtNormalSpeed / speed;
            Assert.True(realSecondsPerSeason > 2 * Palette.SeasonBlendRealSeconds, $"x{speed}: a season lasts {realSecondsPerSeason:0} s, the blend {Palette.SeasonBlendRealSeconds:0} s");
        }
    }

    private static T Read<T>(string file)
        where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", file)));
}
