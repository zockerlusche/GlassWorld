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
/// tool (B2 R32: every point × projection × phase from <c>content/look/shots.json</c>, then quits; the world
/// pauses while a shot is pending and a point with an inhabitant follows that figure);
/// <c>--follow=POINT</c> follows the inhabitant of a shot point with its yaw and distance while the world runs
/// (with Godot's <c>--write-movie</c> and <c>--fixed-fps</c> a repeatable clip); <c>--speed=F</c> is the time
/// factor, <c>--warm-ticks=N</c> runs the world N ticks before the first picture;
/// <c>--check</c> prints <c>ISLAND_OK …</c> for the smoke run and quits. Presentation only.
/// </summary>
public partial class IslandScene : Node3D
{
    private readonly Queue<(ShotPoint Point, string Projection, string Phase)> _plan = new();
    private CameraRig _rig = null!;
    private DirectionalLight3D _sun = null!;
    private WorldEnvironment _environment = null!;
    private LookDaylight _daylight = null!;
    private LookShots _shots = null!;
    private string? _shotDirectory;
    private bool _check;
    private int _wait;
    private (ShotPoint Point, string Projection, string Phase)? _current;
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
        var seed = ulong.Parse(Arg("--seed") ?? "1", CultureInfo.InvariantCulture);
        var preset = Arg("--preset") ?? "start_island";
        _shotDirectory = Arg("--shots");
        _check = args.Contains("--check");

        var content = ContentLocator.ContentDirectory();
        T Look<T>(string file)
            where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(content, "look", file)));
        var host = SimulationHost.CreateGeneratedWorld(content, seed, new MockDecisionPort(), "de", preset);
        _host = host;
        var speed = double.Parse(Arg("--speed") ?? "1", CultureInfo.InvariantCulture);
        _warmTicks = long.Parse(Arg("--warm-ticks") ?? "0", CultureInfo.InvariantCulture);
        var time = host.World.Calendar.Settings;
        _ticksPerSecond = time.TicksPerHour * time.HoursPerDay / (double)time.NormalRealSecondsPerDay * speed;
        _maxTicksPerFrame = time.MaxTicksPerFrame;
        var island = host.Map!;
        var view = IslandView.Create(island, host.Content.WorldGen.Presets[preset], content);
        AddChild(view);
        _width = island.Map.WidthCells * view.Look!.CellM;
        _depth = island.Map.DepthCells * view.Look.CellM;

        _daylight = Look<LookDaylight>("daylight.json");
        _shots = Look<LookShots>("shots.json");
        _environment = new WorldEnvironment { Environment = new Godot.Environment { TonemapMode = Godot.Environment.ToneMapper.Linear } };
        AddChild(_environment);
        _sun = new DirectionalLight3D { Name = "Sun", ShadowEnabled = true, ShadowBlur = 2f };
        AddChild(_sun);
        ApplyPhase("day");

        // Figures of the inhabitants (B2-4): appearance from the core, clips from the library.
        var figuresLook = Look<LookFigures>("figures.json");
        var appearanceLook = Look<LookAppearance>("appearance.json");
        var library = ContractSerializer.Deserialize<AnimationLibrary>(File.ReadAllText(Path.Combine(content, "animation", "library.json")));
        var shared = new FigureShared(library, figuresLook, time, speed);
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

        // The camera target follows the ground, but never sinks below the sea surface.
        var seaLevel = host.Content.WorldGen.Presets[preset].Terrain.SeaLevel * terrain.UnitM;
        _rig = CameraRig.Create(Look<LookCamera>("camera.json"), _width, _depth, () => _figures.Values.Select(f => (f.InhabitantId, f.Position)), (x, z) => Math.Max(seaLevel, IslandView.GroundHeight(ground, terrain, x, z)));
        _rig.FigurePosition = id => _figures.TryGetValue(id, out var f) ? f.Position : null;
        AddChild(_rig);
        if (Arg("--follow") is { } follow && _shotDirectory is null)
        {
            var point = _shots.Points.FirstOrDefault(p => p.Id == follow) ?? throw new InvalidDataException($"Unknown shot point '{follow}' for --follow.");
            if (FigureOf(point) is { } figure)
            {
                _rig.Model.Set(figure.Position.X, figure.Position.Z, point.YawDeg, point.DistanceM);
                _rig.FollowFigure(figure.InhabitantId, point.DistanceM);
                _rig.Apply();
            }
        }

        if (_check)
        {
            GD.Print($"ISLAND_OK seed={seed} chunks={view.GroundChunks} ground_triangles={view.GroundTriangles} water_triangles={view.WaterTriangles} figures={_figures.Count}");
            GetTree().Quit(view.GroundChunks == island.Map.ChunksX * island.Map.ChunksZ && view.GroundTriangles > 0 && view.WaterTriangles > 0 && _figures.Count == host.Inhabitants.All.Count() ? 0 : 1);
            return;
        }

        if (_shotDirectory is not null)
        {
            foreach (var point in _shots.Points)
            {
                foreach (var projection in _shots.Projections)
                {
                    foreach (var phase in _shots.Phases)
                    {
                        _plan.Enqueue((point, projection, phase));
                    }
                }
            }
        }
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        // Screenshot runs keep the world still (it was warmed with --warm-ticks), so every shot repeats
        // exactly whatever the frame rate; normal and --follow runs step the world by real time.
        if (_shotDirectory is null)
        {
            StepWorld(delta);
        }

        if (_shotDirectory is null)
        {
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

            _rig.Model.Set(tx, tz, next.Point.YawDeg, next.Point.DistanceM);
            if (figure is not null)
            {
                _rig.FollowFigure(figure.InhabitantId, next.Point.DistanceM);
            }

            _rig.Apply();
            ApplyPhase(next.Phase);
            _wait = _shots.SettleFrames;
            return;
        }

        if (--_wait > 0)
        {
            return;
        }

        var (point, projection, shotPhase) = _current.Value;
        Directory.CreateDirectory(_shotDirectory);
        var size = GetViewport().GetTexture().GetSize();
        var file = Path.Combine(_shotDirectory, string.Create(CultureInfo.InvariantCulture, $"island_{point.Id}_{projection}_{shotPhase}_{size.X}x{size.Y}.png"));
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

    private void ApplySnapshot(int ticks, bool place = false)
    {
        var snapshot = _host.Snapshot();
        foreach (var inhabitant in snapshot.Inhabitants)
        {
            if (_figures.TryGetValue(inhabitant.Id, out var figure))
            {
                figure.Apply(inhabitant, snapshot.Time.Tick, ticks, place);
            }
        }
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

    /// <summary>Light, sky and ambient of a day phase from the daylight data (B2-5 blends them over time).</summary>
    private void ApplyPhase(string phase)
    {
        var data = _daylight.Phases.FirstOrDefault(p => p.Phase.ToString().Equals(phase, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Unknown day phase '{phase}' in shots.json.");
        _sun.LightColor = Color.FromHtml(data.LightColor);
        _sun.LightEnergy = (float)data.LightEnergy;
        _sun.RotationDegrees = new Vector3(-(float)data.SunElevationDeg, (float)_daylight.SunAzimuthDeg, 0);
        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = Color.FromHtml(data.SkyTop),
            SkyHorizonColor = Color.FromHtml(data.SkyHorizon),
            GroundHorizonColor = Color.FromHtml(data.SkyHorizon),
            GroundBottomColor = Color.FromHtml(data.SkyHorizon),
            SunAngleMax = 0,
        };
        var environment = _environment.Environment;
        environment.BackgroundMode = Godot.Environment.BGMode.Sky;
        environment.Sky = new Sky { SkyMaterial = sky };
        environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        environment.AmbientLightColor = Color.FromHtml(data.AmbientColor);
        environment.AmbientLightEnergy = (float)data.AmbientEnergy;
    }
}
