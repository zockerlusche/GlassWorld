using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// Quality levels as data (B2-7 R30): a level decides the grass, the sun's shadow and the ink outline;
/// how a shadow quality is rendered is a table in <c>content/look/quality.json</c>.
/// </summary>
public sealed class QualityPlanTests
{
    private static readonly LookQuality Quality = Read<LookQuality>("quality.json");

    private static readonly LookDaylight Daylight = Read<LookDaylight>("daylight.json");

    private static readonly Dictionary<string, DaylightMix> Mixes = Daylight.Phases.ToDictionary(p => p.Phase, p => DaylightMix.Of(Daylight, p.Phase), StringComparer.Ordinal);

    [Fact]
    public void EveryLevelOfTheDataGivesAPlan()
    {
        foreach (var level in Quality.Levels)
        {
            var plan = QualityPlan.From(Quality, level.Id);
            Assert.Same(level, plan.Level);
            Assert.Equal(level.Shadows != QualityLevelShadows.Off, plan.Shadows is not null);
            Assert.Equal(level.Outline != QualityLevelOutline.Off, plan.HullOutline);
        }

        // The levels as they are today: low without shadows, medium with the cheaper shadow, high with the full one.
        Assert.Null(QualityPlan.From(Quality, "low").Shadows);
        Assert.Same(Quality.ShadowSettings.Low, QualityPlan.From(Quality, "medium").Shadows);
        Assert.Same(Quality.ShadowSettings.High, QualityPlan.From(Quality, "high").Shadows);
        Assert.Equal("level=low shadows=off outline=inverted_hull", QualityPlan.From(Quality, "low").Describe());
        Assert.Equal("level=medium shadows=low outline=inverted_hull", QualityPlan.From(Quality, "medium").Describe());
    }

    [Fact]
    public void TheLevelIsPickedLikeTheGrassLevelAndAnUnknownOneIsRefused()
    {
        Assert.Same(GrassPlan.Level(Quality), QualityPlan.From(Quality).Level);
        Assert.Same(GrassPlan.Level(Quality, "low"), QualityPlan.From(Quality, "low").Level);
        var error = Assert.Throws<InvalidDataException>(() => QualityPlan.From(Quality, "ultra"));
        Assert.Contains("ultra", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultLevelDrawsWhatTheGameDrewBeforeTheLevelsSwitchedAnything()
    {
        // These values pin the default look. They are what Godot 4.7.2 rendered before quality.json was
        // applied: the engine defaults of DirectionalLight3D (4 splits, 100 m) and of the project settings
        // rendering/lights_and_shadows/directional_shadow/size (4096) and soft_shadow_filter_quality
        // (2 = soft low), and the inverted hull on objects and figures. Changing them changes the default
        // look of the game: do it on purpose, with pictures.
        var plan = QualityPlan.From(Quality);
        Assert.Equal("high", plan.Level.Id);
        var shadows = Assert.IsType<ShadowRendering>(plan.Shadows);
        Assert.Equal(100.0, shadows.MaxDistanceM);
        Assert.Equal(4096, (int)shadows.AtlasSize);
        Assert.Equal(ShadowRenderingMode.Parallel_4_splits, shadows.Mode);
        Assert.Equal(ShadowRenderingSoftFilter.Soft_low, shadows.SoftFilter);
        Assert.True(plan.HullOutline);
        Assert.False(plan.OutlineFallback);
        Assert.Equal("level=high shadows=high outline=inverted_hull", plan.Describe());

        // The default light is untouched in every day phase.
        Assert.All(Mixes.Values, mix => Assert.Same(mix, plan.Light(mix)));
    }

    [Fact]
    public void TheCheaperShadowNeverCostsMoreThanTheFullOne()
    {
        var (low, high) = (Quality.ShadowSettings.Low, Quality.ShadowSettings.High);
        Assert.True(low.MaxDistanceM <= high.MaxDistanceM);
        Assert.True((int)low.AtlasSize <= (int)high.AtlasSize);
        Assert.True(low.Mode <= high.Mode, "not more shadow splits than the full shadow");
        Assert.True(low.SoftFilter <= high.SoftFilter);
        Assert.NotEqual((low.MaxDistanceM, low.AtlasSize, low.Mode, low.SoftFilter), (high.MaxDistanceM, high.AtlasSize, high.Mode, high.SoftFilter));
    }

    [Fact]
    public void WithoutShadowsTheSunCastsNoneInAnyDayPhase()
    {
        var off = QualityPlan.From(Quality, "low");
        Assert.Null(off.Shadows);
        Assert.Contains(Daylight.Phases, p => Mixes[p.Phase].Shadows);
        foreach (var phase in Daylight.Phases)
        {
            var mix = Mixes[phase.Phase];
            var shown = off.Light(mix);
            Assert.False(shown.Shadows);

            // Only the shadow goes: light, sky and ambient stay.
            Assert.Equal(mix with { ShadowOpacity = 0 }, shown);
        }

        // Blends between phases (dusk, dawn) have no shadow either.
        var between = Mixes["day"].Lerp(Mixes["dusk"], 0.5);
        Assert.True(between.Shadows);
        Assert.False(off.Light(between).Shadows);
    }

    [Fact]
    public void AMomentWithoutShadowStaysWithoutOneAtEveryLevel()
    {
        // The daylight data (the moon casts no shadow) and the weather (thick clouds) still win.
        var dark = Mixes["day"] with { ShadowOpacity = 0 };
        Assert.All(Quality.Levels, l => Assert.Same(dark, QualityPlan.From(Quality, l.Id).Light(dark)));
        if (!Daylight.Moon.Shadows)
        {
            Assert.All(Quality.Levels, l => Assert.False(QualityPlan.From(Quality, l.Id).Light(Mixes["night"]).Shadows));
        }
    }

    [Fact]
    public void OutlineOffDrawsNoHullAndPostProcessFallsBackToTheHull()
    {
        var off = QualityPlan.From(With(QualityLevelShadows.High, QualityLevelOutline.Off));
        Assert.False(off.HullOutline);
        Assert.False(off.OutlineFallback);
        Assert.Equal("level=test shadows=high outline=off", off.Describe());

        // No post-process outline is built yet: the hull is drawn and the view reports it once.
        var post = QualityPlan.From(With(QualityLevelShadows.Off, QualityLevelOutline.Post_process));
        Assert.True(post.HullOutline);
        Assert.True(post.OutlineFallback);
        Assert.Equal("level=test shadows=off outline=inverted_hull", post.Describe());

        // The data names only what is drawn.
        Assert.DoesNotContain(Quality.Levels, l => l.Outline == QualityLevelOutline.Post_process);
    }

    [Fact]
    public void TheFiguresFollowTheOutlineOfTheLevel()
    {
        var time = ContractSerializer.Deserialize<WorldTimeSettings>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "world", "time.json")));
        var library = ContractSerializer.Deserialize<AnimationLibrary>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "animation", "library.json")));
        var figures = Read<LookFigures>("figures.json");
        Assert.True(new FigureShared(library, figures, time, 1).HullOutline);
        var off = QualityPlan.From(With(QualityLevelShadows.High, QualityLevelOutline.Off));
        Assert.False(new FigureShared(library, figures, time, 1) { HullOutline = off.HullOutline }.HullOutline);
    }

    [Fact]
    public void TheQualityShotSetShowsShadowsByDayAndWithALowSun()
    {
        var shots = Read<LookShots>("shots_quality.json");
        var plan = ShotPlan.Build(shots, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        Assert.Equal(shots.Points.Count * shots.Phases.Count, plan.Count);
        Assert.Contains("day", shots.Phases);
        Assert.Contains(shots.Points, p => p.Inhabitant is not null);

        // A phase with a low sun (long shadows) that casts a shadow at all.
        Assert.Contains(shots.Phases, p => Mixes[p].Shadows && Mixes[p].LightElevationDeg < 20);
    }

    private static LookQuality With(QualityLevelShadows shadows, QualityLevelOutline outline) => new()
    {
        ContractVersion = Quality.ContractVersion,
        DefaultLevel = "test",
        ShadowSettings = Quality.ShadowSettings,
        Levels = [new QualityLevel { Id = "test", GrassDensity = 1, GrassDistanceM = 50, Shadows = shadows, Outline = outline }],
    };

    private static T Read<T>(string file)
        where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", file)));
}
