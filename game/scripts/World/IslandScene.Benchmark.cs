using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.View;
using GlassWorld.Simulation.Weather;
using Godot;
using AnimationLibrary = GlassWorld.Contracts.AnimationLibrary;

namespace GlassWorld.Game.World;

/// <summary>
/// The FPS benchmark of the island scene (B2-7, acceptance 5, R30). Arguments after <c>--</c>:
/// <c>--benchmark=CSV</c> (frame-time file: pass, time_s, frame_ms), <c>--bench-pass=ID|all</c>,
/// <c>--quality=ID</c>, <c>--resolution=WxH</c>, <c>--fullscreen</c>, and for short headless checks
/// <c>--bench-seconds=S</c> / <c>--bench-warmup=S</c>, <c>--bench-scenario=late</c> (A1775, B2810: the late game of the data). Everything else is data (content/look/benchmark.json):
/// the fixed world, the crowd, the camera path and the passes. VSync is off and the frame rate unlimited.
/// The crowd is placeholder figures of the presentation only: they borrow the look of the inhabitants, play
/// the same clips through the same figure code, and never enter the core or its state hash. A pass shows its
/// day phase, season, weather and wind through the look jumps of the screenshot tool (the core has no
/// season or time jump, and its weather table would replace a set weather within a pass); the core world
/// runs on unchanged. Prints <c>BENCH_ENV …</c> when the first measuring begins (what the run really runs on:
/// size, window mode, VSync, driver, build), <c>BENCH_PASS …</c> per pass and <c>BENCH_DONE …</c>, writes the
/// CSV and quits. The clock starts with the first frame drawn.
/// </summary>
public partial class IslandScene
{
    private LookBenchmark? _benchmark;
    private BenchmarkSchedule _schedule = null!;
    private BenchmarkCrowdPlan? _crowd;
    private readonly List<CrowdEntry> _crowdFigures = [];
    private readonly List<CrowdEntry> _crowdWalkers = [];
    private readonly BenchmarkRecorder _recorder = new();
    private FigureShared? _crowdShared;
    private string _benchCsv = string.Empty;
    private readonly BenchmarkClock _benchClock = new();
    private double _benchElapsed;
    private int _benchWarmupFrames;
    private bool _benchEnvShown;
    private string _benchDriver = string.Empty;
    private string _benchBuild = "dev";
    private int _benchPass = -1;
    private (double X, double Z) _crowdCentre;
    private WeatherReading? _benchWeather;
    private string _crowdWalkGait = string.Empty;

    // A1775 (B2810): the late game scenario (--bench-scenario=late), or null for the start scenario.
    private BenchmarkLatePlan? _late;
    private readonly List<WorldObject> _crowdHeld = [];
    private string _lateWeather = string.Empty;
    private readonly List<(string Id, double X, double Z)> _lateFires = [];

    /// <summary>A1775: plans the late game and lays its fires and things into the core's world (seed-fixed).</summary>
    private void PlanLateGame(LookBenchmark data, string scenario, ulong seed, GlassWorld.Simulation.SimulationHost host, Func<double, double, bool> onLand)
    {
        if (scenario != "late" || data.LateGame is not { } late)
        {
            throw new InvalidDataException($"--bench-scenario={scenario}: only 'late' is known, and it needs late_game in content/look/benchmark.json.");
        }

        if (late.WeatherCycle.FirstOrDefault(w => !_nature!.WeatherWithLook.Contains(w)) is { } unknown)
        {
            throw new InvalidDataException($"late_game weather '{unknown}' has no look (content/look/weather.json).");
        }

        var first = host.Snapshot().Inhabitants.Where(i => i.Appearance is not null).OrderBy(i => i.Id, StringComparer.Ordinal).FirstOrDefault();
        var centre = first is null ? (_width / 2.0, _depth / 2.0) : (first.Position.X, first.Position.Z);
        _late = BenchmarkLatePlan.Build(late, seed, centre, onLand);
        foreach (var place in _late.Placements)
        {
            var spawned = host.Objects.Spawn(place.TypeId, new Position(place.X, place.Z));
            if (place.TypeId == late.FireType)
            {
                _lateFires.Add((spawned.Id, place.X, place.Z));
            }
        }
    }

    /// <summary>Whether a spot is dry ground of the island (inside the map, above the sea).</summary>
    private Func<double, double, bool> IsLandFor(GlassWorld.Simulation.SimulationHost host, string preset, double unitM, Func<double, double, double> groundAt)
    {
        var level = host.Content.WorldGen.Presets[preset].Terrain.SeaLevel * unitM;
        return (x, z) => x > 0 && z > 0 && x < _width && z < _depth && groundAt(x, z) > level;
    }

    /// <summary>A1775 (review GW3 #542): a fire that burned out in an earlier pass is lit again at its spot.</summary>
    private void RelightLateFires()
    {
        for (var i = 0; _late is not null && i < _lateFires.Count; i++)
        {
            var (id, x, z) = _lateFires[i];
            if (!_host.Objects.TryGet(id, out _))
            {
                _lateFires[i] = (_host.Objects.Spawn(_benchmark!.LateGame!.FireType, new Position(x, z)).Id, x, z);
            }
        }
    }

    /// <summary>A1775 (review GW3 #542): the shape of a carried thing from the object view, as in the game.</summary>
    private (Mesh Mesh, Material Material, double SizeM)? CarriedShape(string type) =>
        _objects is { } view && view.LayerOf(view.Looks.For(type).VisualId) is { } layer ? (layer.Mesh, layer.Material, view.Looks.For(type).SizeM) : null;

    /// <summary>A1775: the id of the thing a crowd figure carries in the late game.</summary>
    private static string HeldId(string figureId) => figureId + "_held";

    private void BuildBenchmark(
        LookBenchmark data,
        string csv,
        Func<string, string?> arg,
        string[] args,
        ulong seed,
        string quality,
        List<InhabitantSnapshot> templates,
        FigureShared shared,
        AnimationLibrary library,
        Func<double, double, float> ground,
        double seaLevel,
        HashSet<string> seasons,
        Func<InhabitantSnapshot, FigureSpec> spec)
    {
        double? Seconds(string name) => arg(name) is { } text ? double.Parse(text, CultureInfo.InvariantCulture) : null;
        _schedule = BenchmarkSchedule.From(data, arg("--bench-pass"), Seconds("--bench-seconds"), Seconds("--bench-warmup"));
        foreach (var pass in _schedule.Passes)
        {
            // Unknown looks end the run at once, not after minutes of measuring.
            _ = DaylightMix.Of(_daylight, pass.Phase);
            if (!seasons.Contains(pass.Season) || !_nature!.WeatherWithLook.Contains(pass.Weather))
            {
                throw new InvalidDataException($"Benchmark pass '{pass.Id}': season '{pass.Season}' or weather '{pass.Weather}' has no look (content/look/palette.json, weather.json).");
            }
        }

        if (DisplayServer.GetName() != "headless")
        {
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            if (arg("--resolution") is { } size)
            {
                var (width, height) = BenchmarkSchedule.ParseSize(size);
                DisplayServer.WindowSetSize(new Vector2I(width, height));
            }

            if (args.Contains("--fullscreen"))
            {
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.ExclusiveFullscreen);
            }
        }

        Engine.MaxFps = 0;

        // The crowd around the first inhabitant, on land only.
        _crowdCentre = templates.Count > 0 ? (templates[0].Position.X, templates[0].Position.Z) : (_width / 2.0, _depth / 2.0);
        var actions = library.Mappings.Where(m => m.Kind == AnimationMappingKind.Action).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        bool OnLand(double x, double z) => x > 0 && z > 0 && x < _width && z < _depth && ground(x, z) > seaLevel;
        _crowd = BenchmarkCrowdPlan.Build(data.Crowd, seed, _crowdCentre, templates.Count, OnLand, actions);
        BenchmarkCameraPath.Check([.. data.Camera], _crowd.Walkers.Count);
        _crowdShared = shared;
        _crowdWalkGait = data.Crowd.WalkGait;
        for (var i = 0; _late is not null && i < _crowd.Figures.Count; i++)
        {
            // A1775: every figure carries a thing - presentation only, never in the core's world.
            var figure = _crowd.Figures[i];
            _crowdHeld.Add(new WorldObject(HeldId(figure.Id), _late.HeldType(i), new Position(figure.HomeX, figure.HomeZ)));
        }

        foreach (var plan in _crowd.Figures)
        {
            var entry = new CrowdEntry(plan, templates[plan.Template]);
            var snapshot = CrowdSnapshot(entry, 0);
            entry.View = FigureView.Create(snapshot, spec(entry.Template), shared, ground);
            entry.View.ShapeOf = CarriedShape;
            AddChild(entry.View);
            entry.View.Apply(snapshot, 0, 1, place: true, objects: _crowdHeld);
            _crowdFigures.Add(entry);
        }

        _crowdWalkers.AddRange(_crowd.Walkers.Select(w => _crowdFigures.First(e => ReferenceEquals(e.Plan, w))));
        _benchmark = data;

        _benchCsv = Path.GetFullPath(csv);
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"BENCH_START seed={seed} scenario={(_late is null ? "start" : "late")} placed={_late?.Placements.Count ?? 0} quality={quality} passes={string.Join(",", _schedule.Passes.Select(p => p.Id))} warmup_s={_schedule.WarmupS} duration_s={_schedule.DurationS} figures={_figures.Count + _crowdFigures.Count} revealed_permille={(_cover is null ? "none" : ((int)(_cover.Map.SeenShare * 1000)).ToString(CultureInfo.InvariantCulture))} size={Size()} csv={_benchCsv}"));

        // Asked once, before the clock runs (the query can take a moment); empty without a driver (headless).
        _benchDriver = string.Join(' ', OS.GetVideoAdapterDriverInfo());

        // The build id of an export (tools/export-windows.sh writes build.txt beside the executable), read
        // here as well, so the first measured frame touches no file.
        var beside = Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath()) ?? string.Empty, "build.txt");
        _benchBuild = OS.HasFeature("editor") || !File.Exists(beside) ? "dev" : File.ReadAllText(beside);
    }

    /// <summary>One benchmark frame: time it, run the schedule, the world, the crowd, the look and the camera.</summary>
    private void StepBenchmark(double delta)
    {
        var frameMs = _benchClock.Step(Time.GetTicksUsec());
        _benchElapsed = _benchClock.ElapsedS;
        var moment = _schedule.At(_benchElapsed);
        if (moment.Pass != _benchPass)
        {
            if (_benchPass >= 0)
            {
                GD.Print(_recorder.Summary(_schedule.Passes[_benchPass].Id).Line(_benchmark!.TargetMeanFps, _benchmark.TargetP95FrameMs) + string.Create(CultureInfo.InvariantCulture, $" size={Size()} warmup_frames={_benchWarmupFrames}"));
            }

            _benchPass = moment.Pass;
            _benchWarmupFrames = 0;
            if (moment.Stage == BenchmarkStage.Done)
            {
                FinishBenchmark();
                return;
            }

            ShowPass(_schedule.Passes[moment.Pass]);
        }

        var pass = _schedule.Passes[moment.Pass];
        if (moment.Stage == BenchmarkStage.Measure)
        {
            if (!_benchEnvShown)
            {
                // Read back after the warm-up, when window mode and size have settled.
                _benchEnvShown = true;
                GD.Print(BenchEnvironment().Line());
            }

            if (frameMs > 0)
            {
                // A step whose time did not move on is no frame (review GW3 #225).
                _recorder.Add(pass.Id, moment.MeasureS, frameMs);
            }
        }
        else
        {
            _benchWarmupFrames++;
        }

        if (_late is not null && _late.WeatherAt(moment.MeasureS) is var weather && weather != _lateWeather)
        {
            // A1775: the late game's weather changes within the pass (the look blends as in the game).
            _lateWeather = weather;
            _benchWeather = new WeatherReading(weather, [weather], pass.WindPermille, 0, 0, 0, 0, 0, 0) { Wind = pass.WindPermille / 1000.0 };
        }

        StepWorld(delta);
        var time = _host.World.Calendar;
        var pose = BenchmarkCameraPath.At([.. _benchmark!.Camera], moment.MeasureS, _schedule.DurationS / _benchmark.DurationS, CameraTarget);
        _view?.FollowTide(_tide, delta);
        _nature!.Follow(pass.Season, pass.Epoch ?? _epoch, _benchWeather, delta, Focus(pose.X, pose.Z), _ticksPerSecond * time.RealSecondsPerTickAtNormalSpeed, time.RealSecondsPerTickAtNormalSpeed * time.Settings.TicksPerHour / 60.0);
        ApplyPhase(pass.Phase);
        _rig.Model.Set(pose.X, pose.Z, pose.YawDeg, pose.DistanceM, pose.PitchDeg);
        _rig.Apply();
    }

    /// <summary>Shows a pass at once: weather, season, epoch and day phase, the particles settled.</summary>
    private void ShowPass(BenchmarkPass pass)
    {
        var wind = pass.WindPermille / 1000.0;
        _benchWeather = new WeatherReading(pass.Weather, [pass.Weather], pass.WindPermille, 0, 0, 0, 0, 0, 0) { Wind = wind };
        _lateWeather = string.Empty; // A1775: the late game's cycle starts again with each pass
        RelightLateFires();
        _nature!.Show(pass.Season, pass.Weather, Focus(_crowdCentre.X, _crowdCentre.Z), pass.Epoch ?? _epoch);
        ApplyPhase(pass.Phase);
    }

    private void FinishBenchmark()
    {
        var folder = Path.GetDirectoryName(_benchCsv);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        using (var writer = new StreamWriter(_benchCsv))
        {
            _recorder.WriteCsv(writer);
        }

        var ok = _schedule.Passes.All(p => _recorder.Summary(p.Id).Meets(_benchmark!.TargetMeanFps, _benchmark.TargetP95FrameMs));
        GD.Print($"BENCH_DONE passes={_schedule.Passes.Count} rows={_recorder.Count} ok={(ok ? "yes" : "no")} csv={_benchCsv}");
        _benchmark = null;
        GetTree().Quit();
    }

    /// <summary>The target of a camera key: the crowd centre, or the walking crowd figure it follows.</summary>
    private (double X, double Z) CameraTarget(int? follow)
    {
        if (follow is not { } index)
        {
            return _crowdCentre;
        }

        var view = _crowdWalkers[index].View!;
        return (view.Position.X, view.Position.Z);
    }

    /// <summary>The crowd follows the core's ticks: each figure walks to where it is when those ticks have passed.</summary>
    private void ApplyCrowd(int ticks, bool place)
    {
        if (_crowd is null || _crowdShared is null || _crowdFigures.Count == 0)
        {
            return;
        }

        var at = place ? _benchElapsed : _benchElapsed + (Math.Max(1, ticks) * _crowdShared.RealSecondsPerTick);
        foreach (var entry in _crowdFigures)
        {
            var group = _crowd.GroupOf(entry.Plan);
            var step = _crowd.ActionStep(entry.Plan, at);
            if (step != entry.Step && (group.WaveS is > 0 || group.TalkS is > 0))
            {
                // Expressions: a new action comes with a wave and a few words (mouth).
                entry.View!.React(new Reaction(0, group.WaveS ?? 0, group.TalkS ?? 0));
            }

            entry.Step = step;
            entry.View!.Apply(CrowdSnapshot(entry, at), 0, ticks, place, objects: _crowdHeld);
        }
    }

    private InhabitantSnapshot CrowdSnapshot(CrowdEntry entry, double timeS)
    {
        var (x, z) = _crowd!.Position(entry.Plan, timeS);
        return entry.Template with
        {
            Id = entry.Plan.Id,
            Position = new Position(x, z),
            Action = _crowd.Action(entry.Plan, timeS),
            Gait = _crowd.GroupOf(entry.Plan).Walks ? _crowdWalkGait : null,
            TargetId = null,
            OpenQuestionIds = [],
            Facing = null,
            Topic = null,
            Held = _late is null ? null : HeldId(entry.Plan.Id),
        };
    }

    /// <summary>What the run really runs on, read back from the window, the renderer and the export.</summary>
    private BenchmarkEnvironment BenchEnvironment()
    {
        return new BenchmarkEnvironment(
            Size(),
            DisplayServer.WindowGetMode().ToString(),
            DisplayServer.WindowGetVsyncMode().ToString(),
            Engine.MaxFps,
            RenderingServer.GetVideoAdapterName(),
            _benchDriver,
            RenderingServer.GetVideoAdapterApiVersion(),
            _benchBuild);
    }

    private static string Size()
    {
        var size = DisplayServer.WindowGetSize();
        return string.Create(CultureInfo.InvariantCulture, $"{size.X}x{size.Y}");
    }

    /// <summary>A crowd figure: its plan, the inhabitant whose look it borrows, its view and its last action step.</summary>
    private sealed class CrowdEntry(CrowdFigure plan, InhabitantSnapshot template)
    {
        public CrowdFigure Plan { get; } = plan;

        public InhabitantSnapshot Template { get; } = template;

        public FigureView? View { get; set; }

        public long Step { get; set; } = -1;
    }
}
