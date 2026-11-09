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
/// pauses while a shot is pending and a point with an inhabitant follows that figure);
/// <c>--follow=POINT</c> follows the inhabitant of a shot point with its yaw and distance while the world runs
/// (with Godot's <c>--write-movie</c> and <c>--fixed-fps</c> a repeatable clip); <c>--speed=F</c> is the time
/// factor, <c>--warm-ticks=N</c> runs the world N ticks before the first picture;
/// <c>--check</c> prints <c>ISLAND_OK …</c> for the smoke run and quits. Presentation only.
/// </summary>
public partial class IslandScene : Node3D
{
    private readonly Queue<Shot> _plan = new();
    private CameraRig _rig = null!;
    private DirectionalLight3D _sun = null!;
    private WorldEnvironment _environment = null!;
    private LookDaylight _daylight = null!;
    private LookShots _shots = null!;
    private string? _shotDirectory;
    private bool _check;
    private int _wait;
    private Shot? _current;
    private IslandView? _view;
    private LookPalette? _palette;
    private LookEpochs? _epochs;
    private (string? Epoch, string? Season) _variant;
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
        _shots = Look<LookShots>(Arg("--shot-set") is { } set ? $"shots_{Path.GetFileName(set)}.json" : "shots.json");
        _environment = new WorldEnvironment { Environment = new Godot.Environment() };
        AddChild(_environment);
        _sun = new DirectionalLight3D { Name = "Sun" };
        AddChild(_sun);
        DaylightLook.ApplyRendering(_daylight, _sun, _environment.Environment);
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

        // The core's objects and the decorative plants (TL #19722), drawn as in the main game.
        var objectLooks = Look<LookObjects>("objects.json");
        var decor = Decor.Plan(objectLooks.Decor, island.Island, terrain.CellM);
        _objects = ObjectsView.Create(objectLooks, Look<LookPalette>("palette.json"), (x, z) => IslandView.GroundHeight(ground, terrain, x, z), decor);
        AddChild(_objects);
        _objects.Refresh(host.Snapshot().Objects);
        _island = island.Island;

        // The camera target follows the ground, but never sinks below the sea surface.
        var seaLevel = host.Content.WorldGen.Presets[preset].Terrain.SeaLevel * terrain.UnitM;
        _rig = CameraRig.Create(Look<LookCamera>("camera.json"), _width, _depth, () => _figures.Values.Select(f => (f.InhabitantId, f.Position)), (x, z) => Math.Max(seaLevel, IslandView.GroundHeight(ground, terrain, x, z)));
        _rig.FindFigure = id => _figures.TryGetValue(id, out var f) ? f.Position : null;
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
            GD.Print($"ISLAND_OK seed={seed} chunks={view.GroundChunks} ground_triangles={view.GroundTriangles} water_triangles={view.WaterTriangles} figures={_figures.Count} objects={_objects!.Drawn} decor={_objects.DecorCount}");
            GD.Print($"ISLAND_LOD seed={seed} ground={string.Join(",", view.LevelTriangles.Select(l => $"{l.CellStep}:{l.Ground}"))} water={view.WaterTriangles}");
            GetTree().Quit(view.GroundChunks == island.Map.ChunksX * island.Map.ChunksZ && view.GroundTriangles > 0 && view.WaterTriangles > 0 && _figures.Count == host.Inhabitants.All.Count() ? 0 : 1);
            return;
        }

        if (_shotDirectory is not null)
        {
            _view = view;
            _palette = Look<LookPalette>("palette.json");
            _epochs = Look<LookEpochs>("epochs.json");
            var seasons = _palette.Seasons.Select(s => s.Season).ToHashSet(StringComparer.Ordinal);

            // TODO(B2-5): no weather has a look yet; fill this set from the weather look data once it exists.
            // Until then weather pictures are rendered unchanged and noted in the file name.
            var weatherWithLook = new HashSet<string>(StringComparer.Ordinal);
            foreach (var shot in ShotPlan.Build(_shots, seasons, weatherWithLook))
            {
                _plan.Enqueue(shot);
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

            _rig.Model.Set(tx, tz, yawDeg, next.Point.DistanceM);
            if (figure is not null)
            {
                _rig.FollowFigure(figure.InhabitantId, next.Point.DistanceM);
            }

            _rig.Apply();
            ApplyPhase(next.Phase);
            if ((next.Epoch, next.Season) != _variant)
            {
                // Epoch and season change only the palette (B2 R19); without those lists this never runs.
                _variant = (next.Epoch, next.Season);
                _view!.ApplyPalette(ShotPlan.PaletteFor(_palette!, _epochs!, next.Epoch, next.Season));
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

    private void ApplySnapshot(int ticks, bool place = false)
    {
        var snapshot = _host.Snapshot();
        foreach (var inhabitant in snapshot.Inhabitants)
        {
            if (_figures.TryGetValue(inhabitant.Id, out var figure))
            {
                figure.Apply(inhabitant, snapshot.Time.Tick, ticks, place, snapshot.Inhabitants);
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
    private void ApplyPhase(string phase) => DaylightLook.Apply(_daylight, phase, _sun, _environment.Environment);
}
