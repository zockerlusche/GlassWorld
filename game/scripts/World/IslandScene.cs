using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Decisions;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// The generated island on its own (B2-3): terrain, water, light of a day phase and the island camera.
/// Arguments after <c>--</c>: <c>--seed=N</c>, <c>--preset=ID</c>; <c>--shots=DIR</c> runs the screenshot
/// tool (B2 R32: every point × projection × phase from <c>content/look/shots.json</c>, then quits);
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

        var ground = island.Map;
        var terrain = view.Look;

        // The camera target follows the ground, but never sinks below the sea surface.
        var seaLevel = host.Content.WorldGen.Presets[preset].Terrain.SeaLevel * terrain.UnitM;
        _rig = CameraRig.Create(Look<LookCamera>("camera.json"), _width, _depth, () => [], (x, z) => Math.Max(seaLevel, IslandView.GroundHeight(ground, terrain, x, z)));
        AddChild(_rig);

        if (_check)
        {
            GD.Print($"ISLAND_OK seed={seed} chunks={view.GroundChunks} ground_triangles={view.GroundTriangles} water_triangles={view.WaterTriangles}");
            GetTree().Quit(view.GroundChunks == island.Map.ChunksX * island.Map.ChunksZ && view.GroundTriangles > 0 && view.WaterTriangles > 0 ? 0 : 1);
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
            _rig.Model.Set(_width * next.Point.TargetXPermille / 1000.0, _depth * next.Point.TargetZPermille / 1000.0, next.Point.YawDeg, next.Point.DistanceM);
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

    /// <summary>Light, sky and ambient of a day phase from the daylight data (B2-5 blends them over time).</summary>
    private void ApplyPhase(string phase)
    {
        var data = _daylight.Phases.FirstOrDefault(p => p.Phase.ToString().Equals(phase, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Unknown day phase '{phase}' in shots.json.");
        _sun.LightColor = Color.FromHtml(data.LightColor);
        _sun.LightEnergy = (float)data.LightEnergy;
        _sun.RotationDegrees = new Vector3(-(float)data.SunElevationDeg, 35, 0);
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
