using GlassWorld.Simulation;
using GlassWorld.Simulation.Decisions;
using Godot;

namespace GlassWorld.Game;

/// <summary>
/// Shows the core world (R15): builds it from the content folder, advances it with an accumulator at the
/// speed from content/world/time.json (normal: one game day in the configured real seconds, at most
/// max_ticks_per_frame ticks per frame) and draws placeholder figures and objects. No game logic here;
/// the island, look and animation come with B2.
/// </summary>
public partial class WorldPresenter : Node3D
{
    private readonly Dictionary<string, MeshInstance3D> _figures = new(StringComparer.Ordinal);
    private SimulationHost? _host;
    private MultiMeshInstance3D? _objects;
    private int _objectCount = -1;
    private double _accumulator;

    /// <summary>The running simulation (null until <see cref="Start"/>).</summary>
    public SimulationHost? Host => _host;

    /// <summary>Speed factor: 0 = pause, 1 = normal, 4/16 = fast forward (B6 adds the controls).</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Builds the placeholder start world for a seed and creates the visuals.</summary>
    public void Start(string contentDirectory, ulong seed)
    {
        _host = SimulationHost.CreatePlaceholderWorld(contentDirectory, seed, new MockDecisionPort(), "de");
        foreach (var inhabitant in _host.Inhabitants.All)
        {
            var figure = new MeshInstance3D
            {
                Name = inhabitant.Id,
                Mesh = new CapsuleMesh { Radius = 0.6f, Height = 1.8f },
            };
            AddChild(figure);
            _figures[inhabitant.Id] = figure;
        }

        _objects = new MultiMeshInstance3D { Name = "Objects" };
        AddChild(_objects);
        UpdateVisuals();
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (_host is null)
        {
            return;
        }

        var calendar = _host.World.Calendar;
        _accumulator += delta * Speed / calendar.RealSecondsPerTickAtNormalSpeed;
        var ticks = Math.Min((long)_accumulator, calendar.Settings.MaxTicksPerFrame);
        _accumulator -= ticks;
        _accumulator = Math.Min(_accumulator, calendar.Settings.MaxTicksPerFrame);
        if (ticks > 0)
        {
            _host.World.Run(ticks);
            UpdateVisuals();
        }
    }

    private void UpdateVisuals()
    {
        if (_host is null || _objects is null)
        {
            return;
        }

        foreach (var inhabitant in _host.Inhabitants.All)
        {
            _figures[inhabitant.Id].Position = new Vector3((float)inhabitant.Position.X, 0.9f, (float)inhabitant.Position.Z);
        }

        var objects = _host.Objects.All.ToList();
        if (objects.Count != _objectCount)
        {
            var multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new SphereMesh { Radius = 0.25f, Height = 0.5f },
                InstanceCount = objects.Count,
            };
            for (var i = 0; i < objects.Count; i++)
            {
                multimesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3((float)objects[i].Position.X, 0.25f, (float)objects[i].Position.Z)));
            }

            _objects.Multimesh = multimesh;
            _objectCount = objects.Count;
        }
    }
}
