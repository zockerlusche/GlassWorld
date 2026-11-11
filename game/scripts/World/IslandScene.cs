using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Decisions;
using Godot;
using AnimationLibrary = GlassWorld.Contracts.AnimationLibrary;

namespace GlassWorld.Game.World;

/// <summary>
/// The generated island on its own (B2-3): terrain, water, light of a day phase and the island camera.
/// Arguments after <c>--</c>: <c>--seed=N</c>, <c>--preset=ID</c>; <c>--shots=DIR</c> runs the screenshot
/// tool (B2 R32: every point × projection × phase, and the epochs, seasons and weather it lists, from
/// <c>content/look/shots.json</c>, see <see cref="ShotPlan"/>, then quits; <c>--shot-set=NAME</c> takes
/// <c>content/look/shots_NAME.json</c> instead, e.g. <c>dimensions</c>: epochs × seasons × weather; the world
/// pauses while a shot is pending and a point with an inhabitant follows that figure; <c>clouds</c>: the
/// cloud cover over unseen land, R25);
/// <c>--follow=POINT</c> follows the inhabitant of a shot point with its yaw and distance while the world runs
/// (with Godot's <c>--write-movie</c> and <c>--fixed-fps</c> a repeatable clip); <c>--speed=F</c> is the time
/// factor, <c>--warm-ticks=N</c> runs the world N ticks before the first picture;
/// <c>--check</c> prints <c>ISLAND_OK …</c> for the smoke run and quits; <c>--quality=ID</c> picks a quality
/// level of <c>content/look/quality.json</c> (grass, shadows, outline; see <see cref="QualityPlan"/>); <c>--benchmark=CSV</c> runs the FPS benchmark (B2-7, see
/// <c>IslandScene.Benchmark.cs</c>). Presentation only.
/// </summary>
public partial class IslandScene : Node3D
{
    private readonly Queue<Shot> _plan = new();
    private CameraRig _rig = null!;
    private DirectionalLight3D _sun = null!;
    private WorldEnvironment _environment = null!;
    private LookDaylight _daylight = null!;
    private QualityPlan _quality = null!;
    private LookShots _shots = null!;
    private string? _shotDirectory;
    private bool _check;

    // --clock-light: the light follows the world clock (day, dusk, night) as in the game, not a fixed phase.
    private bool _clockLight;
    private int _wait;
    private Shot? _current;
    private IslandView? _view;
    private NatureLook? _nature;
    private string? _epoch;
    private GlassWorld.Simulation.Map.TideReading? _tide;
    private GrassView? _grass;
    private CloudCover? _cover;
    private string _season = string.Empty;
    private Func<double, double, double> _heightAt = (_, _) => 0;
    private readonly Dictionary<string, DaylightMix> _phases = new(StringComparer.Ordinal);
    private string _phase = "day";
    private (DaylightMix Light, WeatherSky? Sky)? _lightShown;
    private GlassWorld.Simulation.Weather.WeatherReading? _weather;
    private float _width;
    private float _depth;
    private SimulationHost _host = null!;
    private readonly Dictionary<string, FigureView> _figures = new(StringComparer.Ordinal);
    private readonly List<FigureView> _figureOrder = [];
    private readonly HashSet<string> _badPoints = new(StringComparer.Ordinal);
    private double _ticksPerSecond;
    private int _maxTicksPerFrame;
    private double _tickDebt;
    private long _warmTicks;
    private ObjectsView? _objects;
    private GlassWorld.Simulation.Map.GeneratedIsland? _island;
    private readonly Dictionary<string, SceneSpot?> _spots = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public override void _Ready()
    {
        try
        {
            Build();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A failing build must end the run (smoke, screenshots), not leave Godot waiting.
            GD.PushError($"ISLAND_FAIL {e.GetType().Name}: {e.Message}");
            GetTree().Quit(1);
            _shotDirectory = null;
        }
    }

    private void Build()
    {
        var args = OS.GetCmdlineUserArgs();
        string? Arg(string name) => args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];
        var content = ContentLocator.ContentDirectory();
        T Look<T>(string file)
            where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(content, "look", file)));

        // The benchmark (B2-7) takes its fixed world from its data; --seed and --preset still win.
        var benchmark = Arg("--benchmark") is not null ? Look<LookBenchmark>("benchmark.json") : null;
        var seed = ulong.Parse(Arg("--seed") ?? benchmark?.Seed.ToString(CultureInfo.InvariantCulture) ?? "1", CultureInfo.InvariantCulture);
        var preset = Arg("--preset") ?? benchmark?.Preset ?? "start_island";
        _shotDirectory = benchmark is null ? Arg("--shots") : null;
        _check = args.Contains("--check");
        _clockLight = args.Contains("--clock-light");
        var host = SimulationHost.CreateGeneratedWorld(content, seed, MockDecisionPort.FromContent(content), "de", preset);
        _host = host;
        var speed = double.Parse(Arg("--speed") ?? "1", CultureInfo.InvariantCulture);
        _warmTicks = long.Parse(Arg("--warm-ticks") ?? "0", CultureInfo.InvariantCulture);
        var time = host.World.Calendar.Settings;
        _ticksPerSecond = time.TicksPerHour * time.HoursPerDay / (double)time.NormalRealSecondsPerDay * speed;
        _maxTicksPerFrame = time.MaxTicksPerFrame;
        var island = host.Map!;
        var view = IslandView.Create(island, host.Content.WorldGen.Presets[preset], content);
        AddChild(view);
        _view = view;
        _width = island.Map.WidthCells * view.Look!.CellM;
        _depth = island.Map.DepthCells * view.Look.CellM;

        _daylight = Look<LookDaylight>("daylight.json");
        _shots = Look<LookShots>(Arg("--shot-set") is { } set ? $"shots_{Path.GetFileName(set)}.json" : "shots.json");
        _environment = new WorldEnvironment { Environment = new Godot.Environment() };
        AddChild(_environment);
        _sun = new DirectionalLight3D { Name = "Sun" };
        AddChild(_sun);
        DaylightLook.ApplyRendering(_daylight, _sun, _environment.Environment);

        // The quality level (B2-7 R30): grass, the sun's shadow and the ink outline.
        var qualityData = Look<LookQuality>("quality.json");
        _quality = QualityPlan.From(qualityData, Arg("--quality"));
        QualityLook.Apply(_quality, _sun);
        GD.Print($"ISLAND_QUALITY seed={seed} {_quality.Describe()}");
        ApplyPhase("day");

        // Figures of the inhabitants (B2-4): appearance from the core, clips from the library.
        var figuresLook = Look<LookFigures>("figures.json");
        var appearanceLook = Look<LookAppearance>("appearance.json");
        var library = ContractSerializer.Deserialize<AnimationLibrary>(File.ReadAllText(Path.Combine(content, "animation", "library.json")));
        var shared = new FigureShared(library, figuresLook, time, speed) { HullOutline = _quality.HullOutline };
        var ground = island.Map;
        var terrain = view.Look;
        foreach (var inhabitant in host.Snapshot().Inhabitants)
        {
            if (inhabitant.Appearance is not { } appearance)
            {
                continue;
            }

            var spec = FigureSpec.From(appearance, inhabitant.Sex, figuresLook, appearanceLook);
            var figure = FigureView.Create(inhabitant, spec, shared, (x, z) => IslandView.GroundHeight(ground, terrain, x, z));
            AddChild(figure);
            _figures[inhabitant.Id] = figure;
        }

        // Shot points name inhabitants by their index in id order.
        _figureOrder.AddRange(_figures.Values.OrderBy(f => f.InhabitantId, StringComparer.Ordinal));

        // Warm-up: let the world run a while before pictures (figures walk, sit, eat).
        for (var i = 0; i < _warmTicks; i++)
        {
            host.World.Step();
        }

        // The figures stand where the warm-up left them (no walk from the start spot).
        ApplySnapshot(1, place: true);

        // The core's objects and the decorative plants (TL #19722), drawn as in the main game.
        var objectLooks = Look<LookObjects>("objects.json");
        var palette = Look<LookPalette>("palette.json");
        var decor = Decor.Plan(objectLooks.Decor, island.Island, terrain.CellM);

        // Living nature (B2-5 R21, R24): season colours, snow and wind as shader parameters; grass and
        // small plants in detail levels (density and distance from the quality level).
        _nature = NatureLook.Create(content, palette);
        var (grass, otherDecor) = GrassPlan.Split(decor, _nature.Data.Grass);
        Func<double, double, double> groundAt = (x, z) => IslandView.GroundHeight(ground, terrain, x, z);
        _objects = ObjectsView.Create(objectLooks, palette, groundAt, otherDecor, _nature.Season.Slots, TerrainMesher.StreamSources(ground, terrain), _quality.HullOutline);
        AddChild(_objects);
        _objects.Refresh(host.Snapshot().Objects);
        _grass = GrassView.Create(_objects, grass, groundAt, _nature.Data.Grass, _quality.Level);
        AddChild(_grass);
        AddChild(_nature);
        ApplyPhase("day");
        _island = island.Island;

        // The camera target follows the ground, but never sinks below the sea surface.
        var seaLevel = host.Content.WorldGen.Presets[preset].Terrain.SeaLevel * terrain.UnitM;
        _heightAt = (x, z) => Math.Max(seaLevel, IslandView.GroundHeight(ground, terrain, x, z));

        // Cloud cover over what the player has not seen yet (B2-6 R25); what is seen by now is clear at once.
        if (host.Exploration is { } exploration)
        {
            bool IsLand(int x, int z) => IslandView.GroundHeight(ground, terrain, (x + 0.5) * terrain.CellM, (z + 0.5) * terrain.CellM) > seaLevel;
            _cover = CloudCover.Create(exploration, IsLand, _nature.Data.Cover, palette, terrain.CellM, host.Content.WorldGen.Presets[preset].Exploration?.RevealRealSeconds ?? 0, _nature.Wind.DirectionX, _nature.Wind.DirectionZ);
            AddChild(_cover);

            // --revealed=PERMILLE: at least this share of the land shown as seen from the start, whatever runs
            // (a video of the benchmark's camera path, S2). Not what a player sees on the first day.
            if (Arg("--revealed") is { } share)
            {
                _cover.ShowShare(int.Parse(share, CultureInfo.InvariantCulture));
            }
        }
        _rig = CameraRig.Create(Look<LookCamera>("camera.json"), _width, _depth, () => _figures.Values.Select(f => (f.InhabitantId, f.Position)), (x, z) => Math.Max(seaLevel, IslandView.GroundHeight(ground, terrain, x, z)));
        _rig.FindFigure = id => _figures.TryGetValue(id, out var f) ? f.Position : null;
        AddChild(_rig);
        if (Arg("--follow") is { } follow && _shotDirectory is null)
        {
            // The running world seen like a shot of the point (videos for the visual check, S2): its view and
            // its share of revealed land; with an inhabitant the camera goes along, without one it stands still.
            var point = _shots.Points.FirstOrDefault(p => p.Id == follow) ?? throw new InvalidDataException($"Unknown shot point '{follow}' for --follow.");
            if (point.RevealedPermille is { } revealed)
            {
                _cover?.ShowShare(revealed);
            }

            _rig.Model.FovOverride = point.FovDeg;
            if (FigureOf(point) is { } figure)
            {
                _rig.Model.Set(figure.Position.X, figure.Position.Z, point.YawDeg, point.DistanceM, point.PitchDeg);
                _rig.FollowFigure(figure.InhabitantId, point.DistanceM);
                _rig.Apply();
            }
            else if (point.Inhabitant is null)
            {
                _rig.Model.Set(_width * point.TargetXPermille / 1000.0, _depth * point.TargetZPermille / 1000.0, point.YawDeg, point.DistanceM, point.PitchDeg);
                _rig.Apply();
            }
            else
            {
                // The point names an inhabitant this world does not have (FigureOf warned): no picture of the
                // wrong place - the run ends with an error (review GW3 #266).
                throw new InvalidDataException($"Shot point '{follow}' for --follow names inhabitant {point.Inhabitant}, which this world does not have.");
            }
        }

        if (benchmark is not null)
        {
            // 100 placeholder figures for the presentation only, the camera path and the passes (B2-7).
            var templates = host.Snapshot().Inhabitants.Where(i => i.Appearance is not null).OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
            BuildBenchmark(benchmark, Arg("--benchmark")!, Arg, args, seed, _quality.Level.Id, templates, shared, library, (x, z) => IslandView.GroundHeight(ground, terrain, x, z), seaLevel, palette.Seasons.Select(s => s.Season).ToHashSet(StringComparer.Ordinal), figure => FigureSpec.From(figure.Appearance!, figure.Sex, figuresLook, appearanceLook));
        }

        if (_check)
        {
            GD.Print($"ISLAND_OK seed={seed} chunks={view.GroundChunks} ground_triangles={view.GroundTriangles} water_triangles={view.WaterTriangles} figures={_figures.Count} objects={_objects!.Drawn} decor={decor.Count}");
            GD.Print($"ISLAND_NATURE seed={seed} grass_plants={_grass.Plants} grass_layers={_grass.Layers} season_colors={_nature.Season.Slots.Ids.Count} weather_looks={_nature.WeatherWithLook.Count}");
            GD.Print(_cover is null ? $"ISLAND_COVER seed={seed} none" : $"ISLAND_COVER seed={seed} cells={_cover.Map.Width}x{_cover.Map.Depth} version={_cover.Map.Version} seen_permille={(int)(_cover.Map.SeenShare * 1000)}");
            GD.Print($"ISLAND_LOD seed={seed} ground={string.Join(",", view.LevelTriangles.Select(l => $"{l.CellStep}:{l.Ground}"))} water={view.WaterTriangles}");

            // What the quality level left in the scene, read back from the nodes (not from the plan).
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"ISLAND_QUALITY_STATE seed={seed} sun_shadow={(_sun.ShadowEnabled ? 1 : 0)} shadow_mode={(int)_sun.DirectionalShadowMode} shadow_distance_m={_sun.DirectionalShadowMaxDistance:0.#} hull_meshes={HullMeshes()}"));

            // Every level of the data on this sun by day, one after the other (as a switch at runtime
            // would): a level without shadows must leave the light's shadow off, the others on.
            var day = DaylightMix.Of(_daylight, "day");
            foreach (var level in qualityData.Levels)
            {
                var plan = QualityPlan.From(qualityData, level.Id);
                QualityLook.Apply(plan, _sun);
                DaylightLook.Apply(_daylight, plan.Light(day), _sun, _environment.Environment);
                GD.Print(string.Create(CultureInfo.InvariantCulture, $"ISLAND_QUALITY_LEVEL seed={seed} {plan.Describe()} sun_shadow={(_sun.ShadowEnabled ? 1 : 0)} shadow_mode={(int)_sun.DirectionalShadowMode} shadow_distance_m={_sun.DirectionalShadowMaxDistance:0.#}"));
            }
            GetTree().Quit(view.GroundChunks == island.Map.ChunksX * island.Map.ChunksZ && view.GroundTriangles > 0 && view.WaterTriangles > 0 && _figures.Count == host.Inhabitants.All.Count() ? 0 : 1);
            return;
        }

        if (_shotDirectory is not null)
        {
            var seasons = palette.Seasons.Select(s => s.Season).ToHashSet(StringComparer.Ordinal);

            // A weather without an entry in content/look/weather.json is rendered unchanged and noted in the file name.
            foreach (var shot in ShotPlan.Build(_shots, seasons, _nature.WeatherWithLook))
            {
                _plan.Enqueue(shot);
            }
        }
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (_benchmark is not null)
        {
            StepBenchmark(delta);
            return;
        }

        // Screenshot runs keep the world still (it was warmed with --warm-ticks), so every shot repeats
        // exactly whatever the frame rate; normal and --follow runs step the world by real time.
        if (_shotDirectory is null)
        {
            StepWorld(delta);
            if (!_check && _nature is not null && _rig is not null && _season.Length > 0)
            {
                // The season, the look epoch, the wind and the tide of the running world (from the snapshot).
                var time = _host.World.Calendar;
                _view?.FollowTide(_tide, delta);
                _nature.Follow(_season, _epoch, _weather, delta, Focus(_rig.Model.Target.X, _rig.Model.Target.Z), _ticksPerSecond * time.RealSecondsPerTickAtNormalSpeed, time.RealSecondsPerTickAtNormalSpeed * time.Settings.TicksPerHour / 60.0);
                _cover?.Advance(delta);
                if (_clockLight)
                {
                    ApplyLight(DaylightClock.At(_daylight, time, _host.World.Tick, _tickDebt));
                }
                else
                {
                    ApplyPhase(_phase);
                }
            }

            return;
        }

        if (_current is null)
        {
            if (_plan.Count == 0)
            {
                GD.Print("ISLAND_SHOTS_DONE");
                GetTree().Quit();
                _shotDirectory = null;
                return;
            }

            var next = _plan.Dequeue();
            _current = next;
            _rig.Model.OrthographicOverride = next.Projection == "orthographic";
            var (tx, tz) = (_width * next.Point.TargetXPermille / 1000.0, _depth * next.Point.TargetZPermille / 1000.0);
            FigureView? figure = null;
            if (next.Point.Inhabitant is not null && (figure = FigureOf(next.Point)) is null)
            {
                // No such inhabitant in this world: no picture of this point (reported once).
                _current = null;
                return;
            }

            if (figure is not null)
            {
                // A close view of one inhabitant (pictures with figures, T1): the camera follows it.
                (tx, tz) = (figure.Position.X, figure.Position.Z);
            }

            var yawDeg = next.Point.YawDeg;
            if (figure is null && next.Point.Scene is { } scene)
            {
                // The scene finder (TL #19722): a spot with a stream, a spring and a hill near a start.
                if (!_spots.TryGetValue(next.Point.Id, out var spot))
                {
                    _spots[next.Point.Id] = spot = SceneFinder.Find(scene, _island!, _width / _island!.Map.WidthCells);
                    GD.Print(spot is { } s
                        ? $"ISLAND_SCENE point={next.Point.Id} x={s.X:0.0} z={s.Z:0.0} yaw={s.YawDeg:0} streams={s.StreamCells} relief={s.Relief}"
                        : $"ISLAND_SCENE point={next.Point.Id} none (map target)");
                }

                if (spot is { } found)
                {
                    (tx, tz) = (found.X, found.Z);
                    yawDeg = found.YawDeg ?? yawDeg;
                }
            }

            _rig.Model.Set(tx, tz, yawDeg, next.Point.DistanceM, next.Point.PitchDeg);
            _rig.Model.FovOverride = next.Point.FovDeg;
            if (figure is not null)
            {
                _rig.FollowFigure(figure.InhabitantId, next.Point.DistanceM);
            }

            _rig.Apply();

            // The look epoch and the season are shader parameters, as in the running game (B2-5 R16, R24;
            // colours the epoch sets stay the epoch's). The weather (B2-5 R23) is shown at once: clouds,
            // fog, rain or snow, light and snow cover.
            _nature!.Show(next.Season, next.WeatherHasLook ? next.Weather : null, Focus(tx, tz), next.Epoch);

            // The sea at the tide level the point asks for (B2-5 R15), else at the level of the world tick.
            _view!.ShowTide(next.Point.TidePermille is { } permille && _tide is { } tide ? TideFollower.AtPermille(tide.Amplitude, permille) : TideFollower.Level(_tide));
            ApplyPhase(next.Phase);

            // The cloud cover as the world has it, or with the share of seen land the point asks for (R25).
            if (next.Point.RevealedPermille is { } revealed)
            {
                _cover?.ShowShare(revealed);
            }

            _wait = _shots.SettleFrames;
            return;
        }

        if (--_wait > 0)
        {
            return;
        }

        Directory.CreateDirectory(_shotDirectory);
        var size = GetViewport().GetTexture().GetSize();
        var file = Path.Combine(_shotDirectory, _current.FileName((int)size.X, (int)size.Y));
        GetViewport().GetTexture().GetImage().SavePng(file);
        GD.Print($"ISLAND_SHOT {file}");
        _current = null;
    }

    /// <summary>Drives the world through a time store (R17): real time × speed, at most max_ticks_per_frame.</summary>
    private void StepWorld(double delta)
    {
        if (_check || _figures.Count == 0)
        {
            return;
        }

        _tickDebt = Math.Min(_tickDebt + (delta * _ticksPerSecond), _maxTicksPerFrame);
        var steps = (int)Math.Floor(_tickDebt);
        if (steps == 0)
        {
            return;
        }

        _tickDebt -= steps;
        for (var i = 0; i < steps; i++)
        {
            _host.World.Step();
        }

        ApplySnapshot(steps);
    }

    /// <summary>The camera target on the ground (never below the sea), where leaves fall.</summary>
    private Vector3 Focus(double x, double z) => new((float)x, (float)_heightAt(x, z), (float)z);

    private void ApplySnapshot(int ticks, bool place = false)
    {
        var snapshot = _host.Snapshot();
        (_season, _weather, _epoch, _tide) = (snapshot.Time.SeasonId, snapshot.Weather, snapshot.LookEpoch, snapshot.Tide);
        if (_host.Exploration is { } exploration)
        {
            _cover?.Take(exploration, snapshot.ExploredVersion);
        }

        foreach (var inhabitant in snapshot.Inhabitants)
        {
            if (_figures.TryGetValue(inhabitant.Id, out var figure))
            {
                figure.Apply(inhabitant, snapshot.Time.Tick, ticks, place, snapshot.Inhabitants);
            }
        }

        ApplyCrowd(ticks, place);
    }

    /// <summary>The figure a shot point names (index in id order), or null with a warning once per point.</summary>
    private FigureView? FigureOf(ShotPoint point)
    {
        if (point.Inhabitant is not { } index)
        {
            return null;
        }

        if (index >= 0 && index < _figureOrder.Count)
        {
            return _figureOrder[index];
        }

        if (_badPoints.Add(point.Id))
        {
            GD.PushWarning($"ISLAND_SHOT_SKIPPED point={point.Id} inhabitant={index} of {_figureOrder.Count}");
        }

        return null;
    }

    /// <summary>Meshes of the scene (objects, plants, figures) whose material chain has the ink hull pass.</summary>
    private int HullMeshes()
    {
        var hull = GD.Load<Shader>("res://shaders/outline_hull.gdshader");
        var count = 0;
        foreach (var mesh in FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>())
        {
            for (var pass = mesh.MaterialOverride; pass is not null; pass = pass.NextPass)
            {
                if (pass is ShaderMaterial { Shader: var shader } && shader == hull)
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    /// <summary>Light, sky and ambient of a day phase from the daylight data (B2-5 blends them over time).</summary>
    private void ApplyPhase(string phase)
    {
        _phase = phase;
        if (!_phases.TryGetValue(phase, out var day))
        {
            _phases[phase] = day = DaylightMix.Of(_daylight, phase);
        }

        ApplyLight(day);
    }

    /// <summary>A daylight mix under the shown weather and the quality level; applied only when it changed.</summary>
    private void ApplyLight(DaylightMix day)
    {
        // Under the shown weather (B2-5 R23): dimmer or tinted light, clouds and fog; applied only when it changed.
        WeatherSky? sky = null;
        if (_nature is not null)
        {
            (day, sky) = _nature.Shade(day, _daylight.NightMinBrightness);
        }

        // A quality level without shadows: no shadow in any phase.
        day = _quality.Light(day);

        if (_lightShown != (day, sky))
        {
            _lightShown = (day, sky);
            DaylightLook.Apply(_daylight, day, _sun, _environment.Environment, sky, _nature?.WeatherData.Clouds);
            _nature?.Light(day.WaterLight);
        }
    }
}
