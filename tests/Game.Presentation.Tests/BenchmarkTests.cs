using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>The FPS benchmark without Godot (B2-7, acceptance 5): schedule, camera path, crowd, CSV.</summary>
public sealed class BenchmarkTests
{
    [Fact]
    public void TheDataDescribesFourPassesOfAHundredFigures()
    {
        var data = Data();
        Assert.Equal(["day", "night", "storm", "winter"], data.Passes.Select(p => p.Id));
        Assert.Equal(100, data.Crowd.Count);
        Assert.Equal((10.0, 120.0), (data.WarmupS, data.DurationS));
        Assert.Equal(data.DurationS, data.Camera.Last().AtS);
        BenchmarkCameraPath.Check([.. data.Camera], walkers: 1);
    }

    [Fact]
    public void EveryPassIsWarmedUpThenMeasuredOneAfterTheOther()
    {
        var schedule = BenchmarkSchedule.From(Data(), BenchmarkSchedule.All, durationS: 3, warmupS: 1);
        Assert.Equal(16, schedule.TotalS);
        Assert.Equal(new BenchmarkMoment(0, BenchmarkStage.Warmup, 0), schedule.At(0));
        Assert.Equal(new BenchmarkMoment(0, BenchmarkStage.Measure, 0.5), schedule.At(1.5));
        Assert.Equal(new BenchmarkMoment(1, BenchmarkStage.Warmup, 0), schedule.At(4.5));
        Assert.Equal(new BenchmarkMoment(3, BenchmarkStage.Measure, 2.5), schedule.At(15.5));
        Assert.Equal(BenchmarkStage.Done, schedule.At(16).Stage);
    }

    [Fact]
    public void OnePassCanBeChosenAndAnUnknownOneIsRefused()
    {
        var schedule = BenchmarkSchedule.From(Data(), "storm");
        Assert.Equal("storm", Assert.Single(schedule.Passes).Id);
        Assert.Equal(130, schedule.TotalS);
        Assert.Throws<InvalidDataException>(() => BenchmarkSchedule.From(Data(), "fog"));
    }

    [Theory]
    [InlineData("3840x1600", 3840, 1600)]
    [InlineData("1920X1080", 1920, 1080)]
    public void TheResolutionArgumentIsWidthByHeight(string text, int width, int height) =>
        Assert.Equal((width, height), BenchmarkSchedule.ParseSize(text));

    [Theory]
    [InlineData("1920")]
    [InlineData("0x100")]
    [InlineData("axb")]
    public void ABadResolutionIsRefused(string text) =>
        Assert.Throws<FormatException>(() => BenchmarkSchedule.ParseSize(text));

    [Fact]
    public void TheCameraRunsLinearlyBetweenKeysAndBlendsToTheFollowedFigure()
    {
        BenchmarkCameraKey[] keys =
        [
            new() { AtS = 0, YawDeg = 0, PitchDeg = 60, DistanceM = 100 },
            new() { AtS = 10, YawDeg = 360, PitchDeg = 40, DistanceM = 20, Follow = 0 },
        ];
        (double X, double Z) Target(int? follow) => follow is null ? (0, 0) : (10, -10);

        Assert.Equal(new BenchmarkCameraPose(0, 0, 0, 60, 100), BenchmarkCameraPath.At(keys, -1, 1, Target));
        Assert.Equal(new BenchmarkCameraPose(5, -5, 180, 50, 60), BenchmarkCameraPath.At(keys, 5, 1, Target));
        Assert.Equal(new BenchmarkCameraPose(10, -10, 360, 40, 20), BenchmarkCameraPath.At(keys, 99, 1, Target));

        // A shorter pass stretches the path: half the duration reaches the same pose at half the time.
        Assert.Equal(BenchmarkCameraPath.At(keys, 5, 1, Target), BenchmarkCameraPath.At(keys, 2.5, 0.5, Target));
        Assert.Throws<InvalidDataException>(() => BenchmarkCameraPath.Check(keys, walkers: 0));
        Assert.Throws<InvalidDataException>(() => BenchmarkCameraPath.Check([keys[1], keys[0]], walkers: 1));
    }

    [Fact]
    public void TheCrowdMixesItsGroupsByWeightOnLandAndIsTheSameForTheSameSeed()
    {
        var data = Data().Crowd;
        var plan = Crowd(data, 7, (x, _) => x < 20);
        Assert.Equal(100, plan.Figures.Count);
        Assert.Equal(plan.Figures.Count, plan.Figures.Select(f => f.Id).Distinct().Count());
        Assert.All(plan.Figures, f => Assert.StartsWith(BenchmarkCrowdPlan.IdPrefix, f.Id, StringComparison.Ordinal));
        Assert.All(plan.Figures, f => Assert.True(f.HomeX < 20));
        Assert.Equal([40, 30, 30], data.Groups.Select((_, g) => plan.Figures.Count(f => f.Group == g)));
        Assert.Equal(40, plan.Walkers.Count);

        // Mixed, not in blocks: the first ten figures already hold every group.
        Assert.Equal(data.Groups.Count, plan.Figures.Take(10).Select(f => f.Group).Distinct().Count());
        Assert.Equal([0, 1], plan.Figures.Select(f => f.Template).Distinct().Order());
        Assert.Equal(plan.Figures, Crowd(data, 7, (x, _) => x < 20).Figures);
        Assert.NotEqual(plan.Figures, Crowd(data, 8, (x, _) => x < 20).Figures);
    }

    [Fact]
    public void WalkersGoRoundTheirCircleAndStandingFiguresChangeTheirAction()
    {
        var data = Data().Crowd;
        var plan = Crowd(data, 7, (_, _) => true);
        var walker = plan.Walkers[0];
        var (x0, z0) = plan.Position(walker, 0);
        var (x1, z1) = plan.Position(walker, 1);
        Assert.Equal(data.WalkRadiusM, Math.Sqrt(Math.Pow(x0 - walker.HomeX, 2) + Math.Pow(z0 - walker.HomeZ, 2)), 6);
        Assert.InRange(Math.Sqrt(Math.Pow(x1 - x0, 2) + Math.Pow(z1 - z0, 2)), data.WalkSpeedMS * 0.9, data.WalkSpeedMS);

        var standing = plan.Figures.First(f => !plan.GroupOf(f).Walks && plan.GroupOf(f).Actions.Count > 1);
        Assert.Equal((standing.HomeX, standing.HomeZ), plan.Position(standing, 42));
        var step = plan.ActionStep(standing, 0);
        var later = 0.0;
        while (plan.ActionStep(standing, later) == step)
        {
            later += 0.1;
        }

        Assert.InRange(later, 0, data.ActionChangeS + 0.1);
        Assert.NotEqual(plan.Action(standing, 0), plan.Action(standing, later));
    }

    [Fact]
    public void TooLittleLandOrAnUnknownActionIsRefused()
    {
        var data = Data().Crowd;
        Assert.Throws<InvalidDataException>(() => Crowd(data, 7, (_, _) => false));
        var actions = new HashSet<string>(["idle"], StringComparer.Ordinal);
        Assert.Throws<InvalidDataException>(() => BenchmarkCrowdPlan.Build(data, 7, (0, 0), 2, (_, _) => true, actions));
    }

    [Fact]
    public void TheRecorderWritesOneRowPerFrameAndSumsUpEachPass()
    {
        var recorder = new BenchmarkRecorder();
        for (var i = 1; i <= 100; i++)
        {
            recorder.Add("day", i / 100.0, i <= 95 ? 10 : 40);
        }

        recorder.Add("night", 0.02, 20);
        var day = recorder.Summary("day");
        Assert.Equal(100, day.Frames);
        Assert.Equal(11.5, day.MeanFrameMs, 9);
        Assert.Equal(10, day.P95FrameMs);
        Assert.Equal(40, day.MaxFrameMs);
        Assert.Equal(1000 / 11.5, day.MeanFps, 9);
        Assert.True(day.Meets(60, 16.7));
        Assert.False(recorder.Summary("night").Meets(60, 16.7));
        Assert.Equal(0, recorder.Summary("storm").Frames);
        Assert.Equal("BENCH_PASS pass=day frames=100 mean_fps=87.0 p95_ms=10.00 max_ms=40.00 ok=yes", day.Line(60, 16.7));

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        recorder.WriteCsv(writer);
        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(102, lines.Length);
        Assert.Equal(BenchmarkRecorder.Header, lines[0]);
        Assert.Equal("day,0.0100,10.000", lines[1]);
        Assert.Equal("night,0.0200,20.000", lines[^1]);
    }

    [Fact]
    public void ThePercentileIsTheNearestRank()
    {
        Assert.Equal(0, BenchmarkRecorder.Percentile([], 0.95));
        Assert.Equal(5, BenchmarkRecorder.Percentile([5], 0.95));
        Assert.Equal(19, BenchmarkRecorder.Percentile([.. Enumerable.Range(1, 20).Select(i => (double)i)], 0.95));
    }

    [Fact]
    public void AQualityLevelCanBeChosenByIdAndAnUnknownOneIsRefused()
    {
        var quality = ContractSerializer.Deserialize<LookQuality>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "quality.json")));
        Assert.Equal(quality.DefaultLevel, GrassPlan.Level(quality).Id);
        Assert.Equal("low", GrassPlan.Level(quality, "low").Id);
        Assert.Throws<InvalidDataException>(() => GrassPlan.Level(quality, "ultra"));
    }

    private static BenchmarkCrowdPlan Crowd(BenchmarkCrowd data, ulong seed, Func<double, double, bool> onLand) =>
        BenchmarkCrowdPlan.Build(data, seed, (0, 0), 2, onLand, Actions());

    private static HashSet<string> Actions()
    {
        var library = ContractSerializer.Deserialize<AnimationLibrary>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "animation", "library.json")));
        return library.Mappings.Where(m => m.Kind == AnimationMappingKind.Action).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
    }

    private static LookBenchmark Data() =>
        ContractSerializer.Deserialize<LookBenchmark>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "benchmark.json")));

    [Fact]
    public void TheClockStartsWithTheFirstFrameSoASlowStartIsNoWarmUp()
    {
        var clock = new BenchmarkClock();

        // The first frame comes eight seconds after the process began: nothing has passed yet.
        Assert.Equal(0, clock.Step(8_000_000));
        Assert.Equal(0, clock.ElapsedS);

        Assert.Equal(12.5, clock.Step(8_012_500));
        Assert.Equal(20, clock.Step(8_032_500));
        Assert.Equal(0.0325, clock.ElapsedS, 9);

        // A time that does not move on is no frame time.
        Assert.Equal(0, clock.Step(8_032_500));
    }

    [Fact]
    public void TheEnvironmentLineSplitsAtSpacesWhateverTheDriverIsCalled()
    {
        var line = new BenchmarkEnvironment("3840x1600", "ExclusiveFullscreen", "Disabled", 0, "NVIDIA GeForce RTX 3080", "nvidia 32.0.15.6094", "1.3.280", "661e11f5\n").Line();

        Assert.Equal("BENCH_ENV size=3840x1600 mode=ExclusiveFullscreen vsync=Disabled max_fps=0 adapter=NVIDIA_GeForce_RTX_3080 driver=nvidia_32.0.15.6094 api=1.3.280 build=661e11f5", line);
        Assert.All(line.Split(' ')[1..], pair => Assert.Single(pair, '='));
    }

    [Fact]
    public void AnEnvironmentValueThatIsMissingReadsUnknown()
    {
        var line = new BenchmarkEnvironment("1920x1080", "Windowed", "Disabled", 0, string.Empty, " ", "a=b", "dev").Line();

        Assert.Contains(" adapter=unknown driver=unknown api=a-b build=dev", line, StringComparison.Ordinal);
    }
}
