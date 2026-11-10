using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// The sea follows the tide and the look epoch blends the palette (B2-5 R15, R16): the shown water level
/// glides in real time (continuous at x16, exactly the old height without tides); the epoch palette is the
/// base the season works on, shown as shader parameters like a rebuild with that palette would, and an
/// epoch change blends over the real time of the data, also when a second change comes during a blend.
/// The tide shot set looks at a coast the tide moves.
/// </summary>
public sealed class TideEpochLookTests
{
    private static readonly LookPalette Palette = Read<LookPalette>("palette.json");

    private static readonly LookEpochs Epochs = Read<LookEpochs>("epochs.json");

    private static readonly SeasonSlots Slots = SeasonSlots.From(Palette, Epochs);

    private static readonly ContentSet Content = ContentSet.Load(Path.Combine(TestPaths.Root, "content"));

    private static readonly WorldPreset Preset = Content.WorldGen.Presets["start_island"];

    private static double FollowSeconds => Read<LookTerrain>("terrain.json").TideFollowRealSeconds ?? 0;

    [Fact]
    public void WithoutTidesTheSeaStaysExactlyAtTheMeanLevel()
    {
        // Amplitude 0 turns the tides off: every reading is level 0, and the shown level is exactly 0
        // (not nearly 0), so the sea plane lies exactly where it lay before the tides.
        var off = new Tides(new WorldPresetTides { Amplitude = 0, PeriodTicks = Preset.Tides.PeriodTicks, Curve = Preset.Tides.Curve });
        var follower = new TideFollower(FollowSeconds);
        Assert.Equal(0.0, follower.Units);
        for (var tick = 0L; tick < 3 * Preset.Tides.PeriodTicks; tick += 7)
        {
            Assert.Equal(0.0, follower.Update(off.Read(tick), 1 / 60.0));
        }

        // No island, no tide reading: the same.
        Assert.Equal(0.0, new TideFollower(FollowSeconds).Update(null, 1 / 60.0));
        Assert.Equal(0.0, TideFollower.Level(null));
    }

    [Fact]
    public void TheLevelComesFromTheCurvePositionOfTheReading()
    {
        var tides = new Tides(Preset.Tides);
        Assert.True(tides.Active && FollowSeconds > 0, "the start island has tides and the sea follows them softly");
        var levels = Enumerable.Range(0, tides.PeriodTicks).Select(t => TideFollower.Level(tides.Read(t))).ToList();
        Assert.InRange(levels.Max(), tides.Amplitude - 0.05, tides.Amplitude);
        Assert.InRange(levels.Min(), -tides.Amplitude, -tides.Amplitude + 0.05);

        // Finer than the tenths of the reading (no visible steps), and never further from them than a tenth.
        Assert.True(levels.Distinct().Count() > 2 * tides.Amplitude * 10, "the level has more steps than tenths");
        Assert.All(Enumerable.Range(0, tides.PeriodTicks), t => Assert.InRange(Math.Abs(levels[t] - (tides.Read(t).LevelTenths / 10.0)), 0, 0.1));
        Assert.Equal((double)tides.Amplitude, TideFollower.AtPermille(tides.Amplitude, 1000));
        Assert.Equal(-(double)tides.Amplitude, TideFollower.AtPermille(tides.Amplitude, -1000));
        Assert.Equal(0.0, TideFollower.AtPermille(tides.Amplitude, 0));
    }

    [Fact]
    public void TheFirstReadingShowsAtOnceAndAPauseHoldsTheWater()
    {
        var tides = new Tides(Preset.Tides);
        var follower = new TideFollower(FollowSeconds);

        // A world that starts (or loads) at high water shows high water, it does not rise from the mean level.
        Assert.Equal(TideFollower.Level(tides.Read(0)), follower.Update(tides.Read(0), 1 / 60.0));
        var quarter = tides.Read(tides.PeriodTicks / 4);
        var held = follower.Update(quarter, 0);
        Assert.Equal(TideFollower.Level(tides.Read(0)), held);

        // With time the water moves towards the reading, without overshooting, and ends there.
        var before = held;
        for (var i = 0; i < 60 * 60; i++)
        {
            var now = follower.Update(quarter, 1 / 60.0);
            Assert.InRange(now, TideFollower.Level(quarter), before);
            before = now;
        }

        Assert.Equal(TideFollower.Level(quarter), before, 6);

        // The screenshot tool sets a level at once; time constant 0 follows at once too.
        Assert.Equal(-1.5, follower.Jump(-1.5));
        var instant = new TideFollower(0);
        instant.Update(tides.Read(0), 1 / 60.0);
        Assert.Equal(TideFollower.Level(quarter), instant.Update(quarter, 1 / 60.0));
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(16, 60)]
    [InlineData(16, 30)]
    [InlineData(16, 144)]
    public void TheSeaGlidesAtEveryGameSpeed(int speed, int framesPerSecond)
    {
        // The world steps as in the game: real time x speed, whole ticks per frame. The core's level then
        // stands for some frames and steps in others; the shown level moves a little every frame.
        var calendar = WorldCalendar.FromJson(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "world", "time.json")));
        var time = calendar.Settings;
        var ticksPerSecond = time.TicksPerHour * time.HoursPerDay / (double)time.NormalRealSecondsPerDay * speed;
        var tides = new Tides(Preset.Tides);
        var follower = new TideFollower(FollowSeconds);
        var dt = 1.0 / framesPerSecond;

        // Fastest rise of the curve in height units per real second (its steepest segment).
        var curve = Preset.Tides.Curve.ToList();
        var steepest = curve.Zip(curve.Skip(1)).Max(p => Math.Abs(p.Second.LevelPermille - p.First.LevelPermille) / (double)(p.Second.PhasePermille - p.First.PhasePermille));
        var fastest = steepest * tides.Amplitude / tides.PeriodTicks * ticksPerSecond;

        var (tick, debt, shownStep, coreStep, lag, standing) = (0L, 0.0, 0.0, 0.0, 0.0, 0);
        var (shown, core) = (follower.Update(tides.Read(0), dt), TideFollower.Level(tides.Read(0)));
        var frames = (int)(2.5 * tides.PeriodTicks / ticksPerSecond * framesPerSecond);
        for (var frame = 0; frame < frames; frame++)
        {
            debt += dt * ticksPerSecond;
            var steps = (long)Math.Floor(debt);
            debt -= steps;
            tick += steps;
            var reading = tides.Read(tick);
            var (nextShown, nextCore) = (follower.Update(reading, dt), TideFollower.Level(reading));
            shownStep = Math.Max(shownStep, Math.Abs(nextShown - shown));
            coreStep = Math.Max(coreStep, Math.Abs(nextCore - core));
            lag = Math.Max(lag, Math.Abs(nextShown - nextCore));
            standing += nextShown.Equals(shown) ? 1 : 0;
            (shown, core) = (nextShown, nextCore);
        }

        // Never faster than the tide itself moves in that real time (plus a little for the tick steps),
        // never far behind it, and the whole range is still shown.
        var perTick = fastest / ticksPerSecond;
        Assert.True(shownStep <= (fastest * dt * 1.05) + (perTick * dt / FollowSeconds), $"largest step {shownStep} per frame, the tide moves {fastest * dt}");
        Assert.True(lag <= (fastest * FollowSeconds) + (2 * perTick), $"lag {lag} height units");
        Assert.True(lag < 0.5 * tides.Amplitude, $"lag {lag} of amplitude {tides.Amplitude}");
        if (ticksPerSecond < framesPerSecond)
        {
            // Fewer ticks than frames: the core's level jumps a tick's worth in one frame, the shown level
            // spreads it over the frames and does not stand still in between.
            Assert.True(shownStep < coreStep, $"shown {shownStep}, core {coreStep}");
            Assert.True(standing < frames / 100, $"{standing} of {frames} frames without movement");
        }
    }

    [Fact]
    public void TheColoursOfTheEpochsHaveTintSlotsBesideTheSeasons()
    {
        var epochal = Epochs.Epochs.SelectMany(e => e.Colors).Select(c => c.Id).Distinct().ToList();
        var seasonal = SeasonSlots.From(Palette).Ids;
        Assert.NotEmpty(epochal);
        Assert.Equal(seasonal.Concat(epochal).Distinct().Order(StringComparer.Ordinal), Slots.Ids.Order(StringComparer.Ordinal));
        Assert.InRange(Slots.Ids.Count, 5, SeasonSlots.Max);
        Assert.All(Enumerable.Range(0, Slots.Ids.Count), i => Assert.Equal(1f, Slots.WeightsOf(Slots.Ids[i])[i]));
        Assert.All(Enumerable.Range(0, Slots.Ids.Count), i => Assert.Equal(1f, Slots.WeightsOf(Slots.Ids[i]).Sum));

        // An epoch colour the palette does not have is a data error.
        var wrong = Read<LookEpochs>("epochs.json");
        wrong.Epochs.Last().Colors.Add(new LookColor { Id = "chrome", Color = "#ffffff" });
        Assert.Throws<InvalidDataException>(() => SeasonSlots.From(Palette, wrong));
        Assert.Throws<InvalidDataException>(() => EpochMix.Of(Epochs, Slots, "space_age"));
        Assert.Throws<InvalidDataException>(() => EpochMix.Of(Epochs, SeasonSlots.From(Palette), "metal"));
    }

    [Fact]
    public void TheStartEpochChangesNothing()
    {
        // The first epoch of the data overrides no colour: the season mix passes through untouched (the
        // very same object, so nothing is written to the shader), with any season.
        var first = Epochs.Epochs.First();
        Assert.Empty(first.Colors);
        foreach (var season in Palette.Seasons.Select(s => (string?)s.Season).Append(null))
        {
            var mix = SeasonMix.Of(Palette, Slots, season);
            Assert.Same(mix, EpochMix.Of(Epochs, Slots, first.Id).Over(mix));
            Assert.Same(mix, EpochMix.Of(Epochs, Slots, null).Over(mix));
        }

        // The blender shows the first epoch it sees at once, and the same mix every frame after it.
        var blender = new EpochBlender(Epochs, Slots);
        var shown = blender.Update(first.Id, 0.016);
        Assert.Equal(new EpochBlend(first.Id, first.Id, 1), blender.Blend);
        Assert.Same(shown, blender.Update(first.Id, 0.016));
        Assert.Equal(EpochMix.Of(Epochs, Slots, null), shown with { Details = [] });

        // A loaded world in a later epoch shows that epoch at once, without a blend from the base palette.
        var loaded = new EpochBlender(Epochs, Slots);
        Assert.Equal(EpochMix.Of(Epochs, Slots, "metal"), loaded.Update("metal", 0.016));
        Assert.Equal(new EpochBlend("metal", "metal", 1), loaded.Blend);
    }

    [Fact]
    public void AnEpochIsTheBasePaletteTheSeasonWorksOn()
    {
        // Epoch under season as shader parameters = the palette of both (ShotPlan.PaletteFor: base, season, epoch).
        foreach (var epoch in Epochs.Epochs)
        {
            foreach (var season in Palette.Seasons.Select(s => (string?)s.Season).Append(null))
            {
                var shown = EpochMix.Of(Epochs, Slots, epoch.Id).Over(SeasonMix.Of(Palette, Slots, season));
                var want = ShotPlan.PaletteFor(Palette, Epochs, epoch.Id, season);
                Assert.Equal(Slots.Ids.Select(id => Rgb.Parse(want.Colors.Single(c => c.Id == id).Color)), shown.Colors);
                Assert.Equal(SeasonMix.Of(Palette, Slots, season).SnowCover, shown.SnowCover);
                Assert.Equal(epoch.Details.Order(StringComparer.Ordinal), EpochMix.Of(Epochs, Slots, epoch.Id).Details.Select(d => d.Id));
            }
        }

        // Where an epoch and a season name the same colour, the epoch's wins.
        var both = Read<LookEpochs>("epochs.json");
        both.Epochs.Last().Colors.Add(new LookColor { Id = "foliage", Color = "#123456" });
        var slots = SeasonSlots.From(Palette, both);
        var autumn = EpochMix.Of(both, slots, both.Epochs.Last().Id).Over(SeasonMix.Of(Palette, slots, "autumn"));
        Assert.Equal(Rgb.Parse("#123456"), autumn.Colors[slots.IndexOf("foliage")]);
        Assert.NotEqual(slots.BaseColors[slots.IndexOf("grass")], autumn.Colors[slots.IndexOf("grass")]);
    }

    [Theory]
    [InlineData("metal", null)]
    [InlineData("metal", "autumn")]
    public void MovingTheEpochColoursInTheShaderShowsWhatARebuildWould(string epoch, string? season)
    {
        // The ground built once with the base palette, its epoch shares moved as the shader does, against
        // the ground rebuilt with the palette of the epoch and the season (models unchanged, B2 R19).
        var island = WorldGenerator.Generate(Content.WorldGen, Content.Objects, "start_island", 7);
        var terrain = Read<LookTerrain>("terrain.json");
        var baseLook = TerrainLook.From(terrain, Palette, island.Map, Preset, Epochs);
        var epochLook = TerrainLook.From(terrain, ShotPlan.PaletteFor(Palette, Epochs, epoch, season), island.Map, Preset, Epochs);
        var mix = EpochMix.Of(Epochs, Slots, epoch).Over(SeasonMix.Of(Palette, Slots, season));
        var (vertices, moved, worst) = (0, 0, 0f);
        var n = SeasonSlots.Max;
        for (var cz = 0; cz < island.Map.ChunksZ; cz += 3)
        {
            for (var cx = 0; cx < island.Map.ChunksX; cx += 3)
            {
                var built = TerrainMesher.Ground(island.Map, cx, cz, baseLook);
                var rebuilt = TerrainMesher.Ground(island.Map, cx, cz, epochLook);
                Assert.Equal(rebuilt.Positions, built.Positions);
                Assert.Equal(built.VertexCount * n, built.Tints.Count);
                for (var v = 0; v < built.VertexCount; v++)
                {
                    var t = built.Tints;
                    var tint = new TintWeights(t[v * n], t[(v * n) + 1], t[(v * n) + 2], t[(v * n) + 3], t[(v * n) + 4], t[(v * n) + 5], t[(v * n) + 6], t[(v * n) + 7]);
                    var shown = mix.Shade(new Rgb(built.Colors[v * 3], built.Colors[(v * 3) + 1], built.Colors[(v * 3) + 2]) { Tint = tint }, Slots);
                    var want = new Rgb(rebuilt.Colors[v * 3], rebuilt.Colors[(v * 3) + 1], rebuilt.Colors[(v * 3) + 2]);
                    worst = Math.Max(worst, Math.Max(Math.Abs(shown.R - want.R), Math.Max(Math.Abs(shown.G - want.G), Math.Abs(shown.B - want.B))));
                    vertices++;
                    moved += tint[Slots.IndexOf("rock")] > 0 ? 1 : 0;
                }
            }
        }

        Assert.True(vertices > 10000 && moved > vertices / 100, $"{moved} of {vertices} vertices carry the epoch's rock colour");
        Assert.True(worst < 1.5f / 255, $"largest difference to the rebuild: {worst * 255:0.00} of 255");
    }

    [Fact]
    public void AnEpochChangeBlendsOverTheRealTimeOfTheData()
    {
        var duration = Epochs.BlendRealSeconds;
        Assert.True(duration > 0);
        var ground = Slots.IndexOf("ground");
        var (from, to) = (EpochMix.Of(Epochs, Slots, "wilderness"), EpochMix.Of(Epochs, Slots, "tools"));
        var season = SeasonMix.Of(Palette, Slots, "summer");
        var blender = new EpochBlender(Epochs, Slots);
        blender.Update("wilderness", 0.016);

        // The change itself shows nothing yet (no jump); a paused game (0 seconds) holds the blend.
        Assert.Equal(from.Over(season), blender.Update("tools", 0).Over(season));
        Assert.Equal(new EpochBlend("wilderness", "tools", 0), blender.Blend);

        // Real seconds alone drive it (the game speed does not): half the time is half the way.
        const double dt = 1 / 60.0;
        var before = blender.Shown.Over(season).Colors[ground];
        var largest = 0f;
        var frames = (int)Math.Round(duration / dt);
        for (var i = 1; i <= frames; i++)
        {
            var now = blender.Update("tools", dt).Over(season).Colors[ground];
            largest = Math.Max(largest, Math.Max(Math.Abs(now.R - before.R), Math.Max(Math.Abs(now.G - before.G), Math.Abs(now.B - before.B))));
            before = now;
            if (i == frames / 2)
            {
                Assert.Equal(0.5, blender.Blend.T, 2);
                Assert.Equal(0.5, blender.Shown.Hold[ground], 2);
                Assert.Equal(0.5, blender.Shown.Detail("trodden_paths"), 2);
                Assert.Equal(blender.Update("tools", 0), blender.Update("tools", 0));
            }
        }

        // Calm: no frame moves a colour by more than a small part of the whole way; then the new epoch stands.
        var way = Math.Abs(to.Colors[ground].R - Slots.BaseColors[ground].R) + Math.Abs(to.Colors[ground].G - Slots.BaseColors[ground].G) + Math.Abs(to.Colors[ground].B - Slots.BaseColors[ground].B);
        Assert.True(way > 0 && largest < way * 2 * dt / duration * 1.5, $"largest step {largest} of {way}");
        blender.Update("tools", dt);
        Assert.Equal(to, blender.Shown);
        Assert.Equal(new EpochBlend("tools", "tools", 1), blender.Blend);
        Assert.Equal(1.0, blender.Shown.Detail("trodden_paths"));
        Assert.Same(blender.Update("tools", dt), blender.Update("tools", dt));
    }

    [Fact]
    public void ASecondChangeDuringABlendGoesOnFromWhatIsShown()
    {
        var duration = Epochs.BlendRealSeconds;
        var season = SeasonMix.Of(Palette, Slots, "autumn");
        var blender = new EpochBlender(Epochs, Slots);
        blender.Update("wilderness", 0.016);
        blender.Update("workshop", 0);
        const double dt = 1 / 60.0;
        for (var t = 0.0; t < duration * 0.4; t += dt)
        {
            blender.Update("workshop", dt);
        }

        // In the middle of the blend the epoch changes again: what is shown stays, the blend starts anew from it.
        var mid = blender.Shown;
        Assert.InRange(mid.Hold[Slots.IndexOf("bark")], 0.05, 0.95);
        Assert.Equal(mid.Over(season), blender.Update("metal", 0).Over(season));
        Assert.Equal(new EpochBlend("workshop", "metal", 0), blender.Blend);

        var before = blender.Shown.Over(season);
        var largest = 0f;
        for (var t = 0.0; t < duration + 1; t += dt)
        {
            var now = blender.Update("metal", dt).Over(season);
            for (var i = 0; i < now.Colors.Count; i++)
            {
                largest = Math.Max(largest, Math.Max(Math.Abs(now.Colors[i].R - before.Colors[i].R), Math.Max(Math.Abs(now.Colors[i].G - before.Colors[i].G), Math.Abs(now.Colors[i].B - before.Colors[i].B))));
            }

            // A detail on the way out and one on the way in are both partly there, never beyond 0..1.
            Assert.All(blender.Shown.Details, d => Assert.InRange(d.Weight, 0, 1));
            before = now;
        }

        Assert.True(largest < 0.01f, $"largest colour step per frame {largest}");
        Assert.Equal(EpochMix.Of(Epochs, Slots, "metal"), blender.Shown with { Details = [.. blender.Shown.Details.Where(d => d.Weight > 0)] });
        Assert.Equal((1.0, 0.0), (blender.Shown.Detail("glow"), blender.Shown.Detail("no_such_detail")));
        Assert.Equal(EpochMix.Of(Epochs, Slots, "metal").Over(season), blender.Shown.Over(season));
    }

    [Fact]
    public void TheTideShotSetShowsACoastAtLowAndHighWaterAndALaterEpoch()
    {
        var shots = Read<LookShots>("shots_tide.json");
        var plan = ShotPlan.Build(shots, Palette.Seasons.Select(s => s.Season).ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        Assert.Equal(shots.Points.Count, plan.Count);
        Assert.Equal(plan.Count, plan.Select(s => s.FileName(1280, 720)).Distinct().Count());

        // The same coast view at the lowest, the mean and the highest water.
        var coast = plan.Where(s => s.Point.Id.StartsWith("coast_", StringComparison.Ordinal)).ToList();
        Assert.Equal(new int?[] { -1000, 0, 1000 }, coast.Select(s => s.Point.TidePermille));
        Assert.Single(coast.Select(s => (s.Point.TargetXPermille, s.Point.TargetZPermille, s.Point.YawDeg, s.Point.DistanceM, s.Epoch)).Distinct());

        // It is a coast the tide moves on the island the scene shows (seed 1): land and sea around the
        // target, with beach the high water floods and shallows the low water leaves dry.
        var map = WorldGenerator.Generate(Content.WorldGen, Content.Objects, "start_island", 1).Map;
        var (sea, amplitude) = (Preset.Terrain.SeaLevel, Preset.Tides.Amplitude);
        var (x, z) = (coast[0].Point.TargetXPermille * map.WidthCells / 1000, coast[0].Point.TargetZPermille * map.DepthCells / 1000);
        var around = (from dz in Enumerable.Range(-10, 21) from dx in Enumerable.Range(-10, 21) select map.Cell(x + dx, z + dz)).ToList();
        Assert.True(around.Count(c => c.Water == WaterKind.None && c.Height - sea <= amplitude) > 40, "beach that floods at high water");
        Assert.True(around.Count(c => c.Water == WaterKind.Salt && c.WaterDepth <= amplitude) > 40, "shallows that fall dry at low water");

        // One view in the first and in a later epoch (the point forces its epoch; the set lists none).
        var rocks = plan.Where(s => s.Point.Id.StartsWith("rocks_", StringComparison.Ordinal)).ToList();
        Assert.Null(shots.Epochs);
        Assert.Equal(new[] { Epochs.Epochs.First().Id, "metal" }, rocks.Select(s => s.Epoch));
        Assert.Contains("_metal_", rocks[1].FileName(1280, 720), StringComparison.Ordinal);
        Assert.NotEmpty(Epochs.Epochs.Single(e => e.Id == "metal").Colors);
        (x, z) = (rocks[0].Point.TargetXPermille * map.WidthCells / 1000, rocks[0].Point.TargetZPermille * map.DepthCells / 1000);
        Assert.Equal("rocks_hills", map.BiomeIds[map.Cell(x, z).Biome]);
    }

    [Fact]
    public void APointWithItsOwnEpochIsShotOnceInIt()
    {
        var shots = Read<LookShots>("shots_dimensions.json");
        Assert.True(shots.Epochs is { Count: > 1 });
        var free = ShotPlan.Build(shots, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        var point = shots.Points.First();
        point.Epoch = "hut";
        var forced = ShotPlan.Build(shots, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        Assert.Equal(free.Count(s => s.Point.Id == point.Id) / shots.Epochs!.Count, forced.Count(s => s.Point.Id == point.Id));
        Assert.All(forced.Where(s => s.Point.Id == point.Id), s => Assert.Equal("hut", s.Epoch));
        Assert.Equal(free.Count(s => s.Point.Id != point.Id), forced.Count(s => s.Point.Id != point.Id));
    }

    private static T Read<T>(string file)
        where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", file)));
}
