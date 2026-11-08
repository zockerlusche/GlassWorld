using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Game.World;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.View;
using Godot;
using AnimationLibrary = GlassWorld.Contracts.AnimationLibrary;

namespace GlassWorld.Game;

/// <summary>
/// Island mode of the main game (TL decision #19505): the generated island (IslandView), one FigureView
/// per inhabitant (births and deaths followed from the snapshots), the core's objects (ObjectsView) and
/// the island camera (CameraRig) driven by the observer (ObserverFocus) until the player takes it. The UI
/// anchors to the figures' head anchors; the gift ray meets the drawn ground. Presentation only: it reads
/// snapshots and never writes core state. The placeholder look stays the fallback (content/look/world.json).
/// </summary>
public partial class WorldPresenter
{
    private const string HeadAnchorName = "HeadAnchor";

    private readonly Dictionary<string, Node3D> _islandNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FigureView> _islandFigures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _islandReported = new(StringComparer.Ordinal);
    private string _contentDirectory = string.Empty;
    private LookWorld? _look;
    private IslandView? _islandView;
    private ObjectsView? _objectsView;
    private CameraRig? _rig;
    private FigureShared? _figureShared;
    private LookFigures? _figuresLook;
    private LookAppearance? _appearanceLook;
    private ObserverFocus? _observer;
    private Func<double, double, double> _ground = (_, _) => 0;
    private int? _observedGroup;
    private bool _wasObserving;
    private double _speedSeen = double.NaN;
    private (Godot.Environment? Environment, DirectionalLight3D? Sun, Color SunColor, float SunEnergy, Vector3 SunRotation, bool SunShadow, List<Node3D> Hidden)? _restore;

    /// <summary>How this world is drawn (island when it has an island map).</summary>
    public WorldLookMode Mode { get; private set; } = WorldLookMode.Placeholder;

    /// <summary>The island camera in island mode, else null.</summary>
    public CameraRig? Rig => _rig;

    /// <summary>Island mode: objects drawn now (0 in placeholder mode).</summary>
    public int ObjectsDrawn => _objectsView?.Drawn ?? 0;

    /// <summary>Island mode: number of figures with the real figure model.</summary>
    public int FigureViews => _islandFigures.Count;

    /// <summary>Island mode: ground chunks of the island view.</summary>
    public int GroundChunks => _islandView?.GroundChunks ?? 0;

    /// <summary>Ground height in metres at (x, z): the drawn island ground, 0 on the placeholder.</summary>
    public float GroundHeight(double x, double z) => (float)_ground(x, z);

    /// <summary>
    /// Where the question marks of an inhabitant hang: below the head anchor at the island's label height,
    /// or on the placeholder figure at <paramref name="placeholderHeightM"/>. Null without a figure.
    /// </summary>
    public (Node3D Parent, float HeightM, float? PixelSizeM)? MarkAnchor(string inhabitantId, float placeholderHeightM)
    {
        if (GetNodeOrNull<Node3D>(inhabitantId) is not { } figure)
        {
            return null;
        }

        if (Mode == WorldLookMode.Island && _look is not null && figure.GetNodeOrNull<Node3D>(HeadAnchorName) is { } head)
        {
            return (head, (float)_look.Labels.QuestionAboveHeadM, (float)_look.Labels.PixelSizeM);
        }

        return (figure, placeholderHeightM, null);
    }

    /// <summary>
    /// The point of a figure a click is measured against: on the island between feet and head anchor (data
    /// share), on the placeholder the figure's centre. Null without a figure.
    /// </summary>
    public Vector3? PickPoint(string inhabitantId)
    {
        if (GetNodeOrNull<Node3D>(inhabitantId) is not { } figure || !figure.IsInsideTree())
        {
            return null;
        }

        if (Mode == WorldLookMode.Island && _look is not null && figure.GetNodeOrNull<Node3D>(HeadAnchorName) is { } head)
        {
            return figure.GlobalPosition.Lerp(head.GlobalPosition, (float)_look.PickHeightShare);
        }

        return figure.GlobalPosition;
    }

    /// <summary>
    /// The ground point under a screen position: on the island the first crossing with the drawn ground
    /// (TerrainMesher.GroundHeight), on the placeholder the plane y = 0. Null if the camera looks away.
    /// </summary>
    public Vector3? GroundPoint(Camera3D camera, Vector2 screen)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var from = camera.ProjectRayOrigin(screen);
        var direction = camera.ProjectRayNormal(screen);
        if (Mode != WorldLookMode.Island || _look is null)
        {
            return Math.Abs(direction.Y) < 1e-6f ? null : from + (direction * (-from.Y / direction.Y));
        }

        var ray = _look.GroundRay;
        var hit = GroundRay.Hit(new Point3(from.X, from.Y, from.Z), new Point3(direction.X, direction.Y, direction.Z), _ground, ray.StepM, ray.MaxDistanceM, ray.RefineSteps);
        return hit is { } p ? new Vector3((float)p.X, (float)p.Y, (float)p.Z) : null;
    }

    /// <summary>The player clicked a figure in the world: on the island the camera follows it.</summary>
    public void ChooseFigure(string inhabitantId)
    {
        if (_rig is null || _observer is null || !_islandNodes.ContainsKey(inhabitantId))
        {
            return;
        }

        _observer.Choose(inhabitantId);
        _rig.FollowFigure(inhabitantId);
    }

    /// <summary>The player clicked into empty ground: on the island the camera stops following and stays.</summary>
    public void ClickedGround()
    {
        if (_rig is null || _observer is null)
        {
            return;
        }

        _observer.PlayerTookCamera(_clock);
        _rig.Model.StopFollowing();
    }

    /// <summary>Screenshot runs: a close view of one inhabitant (island: the rig follows it near). False on the placeholder.</summary>
    public bool CloseLook(string inhabitantId, double distanceM)
    {
        if (_rig is null || _observer is null || !_islandNodes.TryGetValue(inhabitantId, out var figure))
        {
            return false;
        }

        _observer.Choose(inhabitantId);
        _rig.Model.Set(figure.Position.X, figure.Position.Z, _rig.Model.Yaw, distanceM);
        _rig.FollowFigure(inhabitantId, distanceM);
        _rig.Apply();
        return true;
    }

    /// <summary>
    /// Smoke run only (island mode): every inhabitant has a figure with a head anchor and a thought bubble
    /// on it, the objects are drawn, the ground ray from the screen centre lands on the drawn ground.
    /// Empty if fine, otherwise the first problem.
    /// </summary>
    public string IslandSelfCheck()
    {
        if (Mode != WorldLookMode.Island || _host is null || _rig is null)
        {
            return "not in island mode";
        }

        foreach (var inhabitant in _host.Inhabitants.All)
        {
            if (GetNodeOrNull<FigureView>(inhabitant.Id) is not { } figure)
            {
                return $"no figure for {inhabitant.Id}";
            }

            if (_thoughts.TryGetValue(inhabitant.Id, out var bubble) && bubble.Label.GetParent() != figure.HeadAnchor)
            {
                return $"thought bubble of {inhabitant.Id} is not on its head anchor";
            }
        }

        if (_objectsView is null || _objectsView.Drawn == 0)
        {
            return "no objects drawn";
        }

        if (GetViewport()?.GetCamera3D() != _rig.Camera)
        {
            return "the island camera is not the current camera";
        }

        var centre = GetViewport()!.GetVisibleRect().Size / 2;
        if (GroundPoint(_rig.Camera, centre) is not { } ground)
        {
            return "the ground ray from the screen centre finds no ground";
        }

        var height = GroundHeight(ground.X, ground.Z);
        return Math.Abs(ground.Y - height) > 0.01f
            ? $"ground point {ground} is not on the drawn ground ({height:0.00} m)"
            : string.Empty;
    }

    private static T Read<T>(string content, string folder, string file)
        where T : class => ContractSerializer.Deserialize<T>(System.IO.File.ReadAllText(System.IO.Path.Combine(content, folder, file)));

    /// <summary>Reads the world-look switch (content/look/world.json).</summary>
    private static LookWorld ReadLook(string contentDirectory) => Read<LookWorld>(contentDirectory, "look", "world.json");

    private void BeginIsland()
    {
        var host = _host!;
        var content = _contentDirectory;
        var map = host.Map ?? throw new InvalidOperationException("island mode needs a world with an island map");
        Mode = WorldLookMode.Island;
        _look = ReadLook(content);
        var preset = host.Content.WorldGen.Presets[map.Island.PresetId];
        _islandView = IslandView.Create(map, preset, content);
        AddChild(_islandView);
        var terrain = _islandView.Look!;
        var ground = map.Map;
        _ground = (x, z) => IslandView.GroundHeight(ground, terrain, x, z);

        _objectsView = ObjectsView.Create(Read<LookObjects>(content, "look", "objects.json"), Read<LookPalette>(content, "look", "palette.json"), _ground);
        AddChild(_objectsView);

        _figuresLook = Read<LookFigures>(content, "look", "figures.json");
        _appearanceLook = Read<LookAppearance>(content, "look", "appearance.json");
        var library = Read<AnimationLibrary>(content, "animation", "library.json");
        _figureShared = new FigureShared(library, _figuresLook, host.World.Calendar.Settings, Speed);
        _speedSeen = Speed;
        ApplyIsland(1, place: true);

        var width = map.Map.WidthCells * terrain.CellM;
        var depth = map.Map.DepthCells * terrain.CellM;
        var seaLevel = preset.Terrain.SeaLevel * terrain.UnitM;
        var camera = Read<LookCamera>(content, "look", "camera.json");
        _rig = CameraRig.Create(camera, width, depth, () => _islandNodes.Select(f => (f.Key, f.Value.Position)), (x, z) => Math.Max(seaLevel, IslandView.GroundHeight(ground, terrain, x, z)));
        _rig.SelectOnClick = false;
        _rig.Panned += () => _observer?.PlayerTookCamera(_clock);
        AddChild(_rig);
        _observer = new ObserverFocus(_look.Observer);
        if (_observer.Update(_clock, Groups()) is { } first && Centre(first) is { } start)
        {
            // Start right above the first group, then follow it smoothly.
            _rig.Model.Set(start.X, start.Z, camera.StartYawDeg, _look.Observer.DistanceM);
            _rig.Apply();
        }

        LightTheIsland(content);
        GD.Print($"ISLAND_WORLD preset={map.Island.PresetId} chunks={_islandView.GroundChunks} figures={_islandFigures.Count} objects_drawn={_objectsView.Drawn} layers={_objectsView.Layers}");
    }

    /// <summary>The day light of the data on the scene's sun and environment; the placeholder island and sea are hidden (restored on exit).</summary>
    private void LightTheIsland(string content)
    {
        if (GetParent() is not { } scene || _look is null)
        {
            // Not in a scene (the smoke's load check): nothing to light.
            return;
        }

        var hidden = new List<Node3D>();
        foreach (var name in new[] { "IslandPlaceholder", "WaterPlaceholder" })
        {
            if (scene.GetNodeOrNull<Node3D>(name) is { Visible: true } node)
            {
                node.Visible = false;
                hidden.Add(node);
            }
        }

        var worldEnvironment = scene.GetChildren().OfType<WorldEnvironment>().FirstOrDefault();
        var sun = scene.GetChildren().OfType<DirectionalLight3D>().FirstOrDefault();
        _restore = (worldEnvironment?.Environment, sun, sun?.LightColor ?? Colors.White, sun?.LightEnergy ?? 1, sun?.RotationDegrees ?? Vector3.Zero, sun?.ShadowEnabled ?? false, hidden);
        if (sun is null)
        {
            sun = new DirectionalLight3D { Name = "IslandSun" };
            AddChild(sun);
        }

        var environment = new Godot.Environment { TonemapMode = Godot.Environment.ToneMapper.Linear };
        if (worldEnvironment is null)
        {
            AddChild(new WorldEnvironment { Name = "IslandEnvironment", Environment = environment });
        }
        else
        {
            worldEnvironment.Environment = environment;
        }

        sun.ShadowEnabled = true;
        sun.ShadowBlur = 2f;
        DaylightLook.Apply(Read<LookDaylight>(content, "look", "daylight.json"), _look.DayPhase, sun, environment);
    }

    /// <summary>Puts back what <see cref="LightTheIsland"/> changed in the scene (back to the menu).</summary>
    private void RestoreScene()
    {
        if (_restore is not { } r)
        {
            return;
        }

        _restore = null;
        foreach (var node in r.Hidden.Where(IsInstanceValid))
        {
            node.Visible = true;
        }

        if (GetParent()?.GetChildren().OfType<WorldEnvironment>().FirstOrDefault() is { } worldEnvironment && r.Environment is not null)
        {
            worldEnvironment.Environment = r.Environment;
        }

        if (r.Sun is { } sun && IsInstanceValid(sun))
        {
            sun.LightColor = r.SunColor;
            sun.LightEnergy = r.SunEnergy;
            sun.RotationDegrees = r.SunRotation;
            sun.ShadowEnabled = r.SunShadow;
        }
    }

    /// <summary>One frame of island mode after the ticks: figures and objects from the snapshot, then the camera.</summary>
    private void ProcessIsland(long ticks)
    {
        if (_figureShared is not null && Speed != _speedSeen)
        {
            _figureShared.UseSpeed(Speed);
            _speedSeen = Speed;
        }

        if (ticks > 0)
        {
            ApplyIsland((int)Math.Min(ticks, int.MaxValue), place: false);
        }
    }

    /// <summary>Takes the core's state: adds and removes figures, moves them, redraws changed objects.</summary>
    private void ApplyIsland(int ticks, bool place)
    {
        if (_host is null || _figureShared is null)
        {
            return;
        }

        var snapshot = _host.Snapshot();
        var (added, removed) = FigureRoster.Diff(_islandNodes.Keys, snapshot.Inhabitants.Select(i => i.Id));
        foreach (var id in removed)
        {
            RemoveFigure(id);
        }

        var fresh = new HashSet<string>(StringComparer.Ordinal);
        foreach (var inhabitant in snapshot.Inhabitants.Where(i => added.Contains(i.Id)))
        {
            AddFigure(inhabitant);
            fresh.Add(inhabitant.Id);
        }

        foreach (var inhabitant in snapshot.Inhabitants)
        {
            if (_islandFigures.TryGetValue(inhabitant.Id, out var figure))
            {
                figure.Apply(inhabitant, snapshot.Time.Tick, ticks, place || fresh.Contains(inhabitant.Id));
            }
            else if (_islandNodes.TryGetValue(inhabitant.Id, out var standIn))
            {
                var (x, z) = (inhabitant.Position.X, inhabitant.Position.Z);
                standIn.Position = new Vector3((float)x, GroundHeight(x, z), (float)z);
            }
        }

        _objectsView?.Refresh(snapshot.Objects);
    }

    private void AddFigure(InhabitantSnapshot inhabitant)
    {
        Node3D node;
        Node3D head;
        try
        {
            if (inhabitant.Appearance is not { } appearance)
            {
                throw new InvalidDataException("no appearance record");
            }

            var spec = FigureSpec.From(appearance, inhabitant.Sex, _figuresLook!, _appearanceLook!);
            var figure = FigureView.Create(inhabitant, spec, _figureShared!, (x, z) => (float)_ground(x, z));
            _islandFigures[inhabitant.Id] = figure;
            node = figure;
            head = figure.HeadAnchor;
        }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException)
        {
            // A figure that cannot be built stands in as a calm capsule; the world runs on (logged once).
            if (_islandReported.Add("figure:" + inhabitant.Id))
            {
                GD.PushWarning($"FIGURE_STAND_IN {inhabitant.Id}: {e.Message}");
            }

            var height = (float)(_figuresLook?.HeadAnchorM ?? 1.9);
            node = new Node3D { Name = inhabitant.Id };
            node.AddChild(new MeshInstance3D { Name = "StandIn", Mesh = new CapsuleMesh { Radius = 0.35f, Height = 1.7f }, Position = new Vector3(0, 0.85f, 0) });
            head = new Node3D { Name = HeadAnchorName, Position = new Vector3(0, height, 0) };
            node.AddChild(head);
            var (x, z) = (inhabitant.Position.X, inhabitant.Position.Z);
            node.Position = new Vector3((float)x, GroundHeight(x, z), (float)z);
        }

        AddChild(node);
        _islandNodes[inhabitant.Id] = node;
        var labels = _look!.Labels;
        var label = new Label3D
        {
            Name = "Thought",
            Position = new Vector3(0, (float)labels.ThoughtAboveHeadM, 0),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            PixelSize = (float)labels.PixelSizeM,
            NoDepthTest = true,
            Visible = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Width = 900,
        };
        head.AddChild(label);
        _thoughts[inhabitant.Id] = (label, 0);
    }

    private void RemoveFigure(string id)
    {
        if (_islandNodes.Remove(id, out var node))
        {
            node.QueueFree();
        }

        _islandFigures.Remove(id);
        _thoughts.Remove(id);
        if (_observer?.Chosen == id)
        {
            _observer.Release(_clock);
            _rig?.Model.StopFollowing();
        }
    }

    /// <summary>The observer camera on the island: follows the shown group while the player leaves the camera to it.</summary>
    private void IslandCamera()
    {
        if (_rig is null || _observer is null)
        {
            return;
        }

        var group = _observer.Update(_clock, Groups());
        var observing = _observer.Observing(_clock);
        if (observing && group is { } g && (g != _observedGroup || !_wasObserving))
        {
            // Keep the player's zoom; the observer only moves the target.
            _rig.Model.Follow(() => Centre(g) is { } c ? (c.X, c.Z) : _rig.Model.Target, _rig.Model.Distance);
            _observedGroup = g;
        }

        _wasObserving = observing;
    }

    /// <summary>A thought appeared: the observer turns to that inhabitant's group.</summary>
    private void ObserveThought(string inhabitantId)
    {
        if (_observer is not null && _host is not null)
        {
            _observer.Thought(_host.Inhabitants.Get(inhabitantId).Group, _clock);
        }
    }

    private List<int> Groups() => _host is null ? [] : [.. _host.Inhabitants.All.Select(i => i.Group).Distinct().Order()];

    /// <summary>Middle of a group's figures on the ground, or null without figures.</summary>
    private (double X, double Z)? Centre(int group)
    {
        if (_host is null)
        {
            return null;
        }

        var members = _host.Inhabitants.All.Where(i => i.Group == group).Select(i => _islandNodes.GetValueOrDefault(i.Id)).OfType<Node3D>().ToList();
        return members.Count == 0 ? null : (members.Average(m => (double)m.Position.X), members.Average(m => (double)m.Position.Z));
    }
}
