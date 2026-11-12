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

    /// <summary>Size of the plain ground under the placeholder world (centred at the origin); the camera stays over it.</summary>
    private static readonly (float Width, float Depth) PlaceholderGroundM = (400, 120);

    private readonly Dictionary<string, Node3D> _islandNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FigureView> _islandFigures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _islandReported = new(StringComparer.Ordinal);
    private string _contentDirectory = string.Empty;
    private LookWorld? _look;
    private IslandView? _islandView;
    private ObjectsView? _objectsView;

    // The inventions drawn from their building plans (A1375): built things and building sites.
    private Build.InventionsView? _inventionsView;
    private GrassView? _grassView;
    private CloudCover? _cover;
    private NatureLook? _nature;
    private string _seasonSeen = string.Empty;
    private Simulation.Weather.WeatherReading? _weatherSeen;
    private string? _epochSeen;
    private Simulation.Map.TideReading? _tideSeen;
    private CameraRig? _rig;
    private FigureShared? _figureShared;
    private LookFigures? _figuresLook;
    private LookAppearance? _appearanceLook;
    private ObserverFocus? _observer;
    private Func<double, double, double> _ground = (_, _) => 0;
    private double _seaLevelM = double.NegativeInfinity;
    private float _cellM = 1;
    private readonly List<int> _groups = [];
    private readonly Dictionary<int, List<Node3D>> _groupMembers = [];
    private int? _observedGroup;
    private bool _wasObserving;
    private double _speedSeen = double.NaN;
    private readonly PlaybackFreeze _freeze = new();
    private readonly List<Vector3> _fadeFor = [];
    private List<Seat>? _seats;
    private (Godot.Environment? Environment, DirectionalLight3D? Sun, Color SunColor, float SunEnergy, Vector3 SunRotation, bool SunShadow, float SunShadowOpacity, DirectionalLight3D.ShadowMode SunShadowMode, float SunShadowDistance, List<Node3D> Hidden)? _restore;
    private QualityPlan? _quality;
    private (LookDaylight Data, DirectionalLight3D Sun, Godot.Environment Environment, DaylightMix? Fixed)? _daylight;
    private DaylightMix? _daylightShown;
    private WeatherSky? _skyShown;

    /// <summary>How this world is drawn (island when it has an island map).</summary>
    public WorldLookMode Mode { get; private set; } = WorldLookMode.Placeholder;

    /// <summary>The island camera in island mode, else null.</summary>
    public CameraRig? Rig => _rig;

    /// <summary>Island mode: objects drawn now (0 in placeholder mode).</summary>
    public int ObjectsDrawn => _objectsView?.Drawn ?? 0;

    /// <summary>
    /// Mesh and material of an object type as the world draws it (for the dream images of the night scene,
    /// B2579: "das vorhandene Symbol des Objekt-Typs"); null in the placeholder world or for a type without a shape.
    /// </summary>
    public (Mesh Mesh, Material Material)? ShapeOfType(string typeId) =>
        _objectsView is { } view ? view.LayerOf(view.Looks.For(typeId).VisualId) : null;

    /// <summary>Island mode: number of figures with the real figure model.</summary>
    public int FigureViews => _islandFigures.Count;

    /// <summary>Island mode: grass and small plants drawn in detail levels (0 in placeholder mode).</summary>
    public int GrassPlants => _grassView?.Plants ?? 0;

    /// <summary>Island mode: ground chunks of the island view.</summary>
    public int GroundChunks => _islandView?.GroundChunks ?? 0;

    /// <summary>Ground height in metres at (x, z): the drawn island ground, 0 on the placeholder.</summary>
    public float GroundHeight(double x, double z) => (float)_ground(x, z);

    /// <summary>
    /// Island mode: why a gift may not lie at a ground point (water, off the island) as a reason key, or
    /// null if it may (always null on the placeholder). The screen shows the reason and sends nothing.
    /// </summary>
    public string? GiftRefusal(Vector3 point) =>
        Mode == WorldLookMode.Island && _host?.Map is { } map
            ? GiftSpot.Refusal(map.Map, _cellM, point.X, point.Z, _ground(point.X, point.Z), _seaLevelM)
            : null;

    /// <summary>
    /// Island mode: where the sound mix listens (the point the camera looks at, on the ground), how far out the
    /// camera is zoomed (0..1) and, while it follows an inhabitant, how far the picture reaches around it - beyond
    /// that nothing is heard (B2605, B8 R7); null on the placeholder (the camera itself listens there).
    /// </summary>
    public (Simulation.Objects.Position Listener, double Zoom, double? LimitM)? Hearing()
    {
        if (_rig is null)
        {
            return null;
        }

        var size = GetViewport().GetVisibleRect().Size;
        double? limit = _rig.Model.Following && size.Y > 0 ? _rig.Model.SeenRadiusM(size.X / size.Y) : null;
        return (new Simulation.Objects.Position(_rig.Model.Target.X, _rig.Model.Target.Z), _rig.Model.ZoomShare, limit);
    }

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
        // The ray meets the sea surface where it lies above the ground, so a click on the sea lands where
        // the player sees it (the screen then refuses it, GiftRefusal).
        var hit = GroundRay.Hit(new Point3(from.X, from.Y, from.Z), new Point3(direction.X, direction.Y, direction.Z), GiftSpot.Surface(_ground, _seaLevelM), ray.StepM, ray.MaxDistanceM, ray.RefineSteps);
        return hit is { } p ? new Vector3((float)p.X, (float)p.Y, (float)p.Z) : null;
    }

    /// <summary>The player clicked a figure in the world: the camera follows it.</summary>
    public void ChooseFigure(string inhabitantId)
    {
        if (_rig is null || _observer is null || (!_islandNodes.ContainsKey(inhabitantId) && !_figures.ContainsKey(inhabitantId)))
        {
            return;
        }

        _observer.Choose(inhabitantId);
        _rig.FollowFigure(inhabitantId);
    }

    /// <summary>The player clicked into empty ground: the camera stops following and stays.</summary>
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

        var height = Math.Max(GroundHeight(ground.X, ground.Z), (float)_seaLevelM);
        return Math.Abs(ground.Y - height) > 0.01f
            ? $"ground point {ground} is not on the drawn ground ({height:0.00} m)"
            : string.Empty;
    }

    private static T Read<T>(string content, string folder, string file)
        where T : class => ContractSerializer.Deserialize<T>(System.IO.File.ReadAllText(System.IO.Path.Combine(content, folder, file)));

    /// <summary>The plan of the default quality level (content/look/quality.json).</summary>
    private static QualityPlan ReadQuality(string contentDirectory) => QualityPlan.From(Read<LookQuality>(contentDirectory, "look", "quality.json"));

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
        _seaLevelM = preset.Terrain.SeaLevel * terrain.UnitM;
        _cellM = terrain.CellM;

        // Living nature (B2-5 R21, R24): season colours, snow and wind as shader parameters; grass and
        // small plants in detail levels (density and distance from the quality level).
        var objectLooks = Read<LookObjects>(content, "look", "objects.json");
        var palette = Read<LookPalette>(content, "look", "palette.json");
        _nature = NatureLook.Create(content, palette);
        var (grass, otherDecor) = GrassPlan.Split(Decor.Plan(objectLooks.Decor, map.Island, terrain.CellM), _nature.Data.Grass);

        // The default quality level (B2-7 R30; which level a hardware tier uses is decided in B7).
        var quality = _quality = ReadQuality(content);
        _objectsView = ObjectsView.Create(objectLooks, palette, _ground, otherDecor, _nature.Season.Slots, TerrainMesher.StreamSources(ground, terrain), quality.HullOutline);
        AddChild(_objectsView);
        _inventionsView = Build.InventionsView.Create(Read<BuildKit>(content, "build", "kit.json"), _ground);
        AddChild(_inventionsView);
        _grassView = GrassView.Create(_objectsView, grass, _ground, _nature.Data.Grass, quality.Level);
        AddChild(_grassView);
        AddChild(_nature);

        // Cloud cover over what the player has not seen yet (B2-6 R25); the land a new or loaded world
        // knows is clear at once, only land seen from now on clears slowly.
        if (host.Exploration is { } exploration)
        {
            var seaLevelM = _seaLevelM;
            bool IsLand(int x, int z) => IslandView.GroundHeight(ground, terrain, (x + 0.5) * terrain.CellM, (z + 0.5) * terrain.CellM) > seaLevelM;
            _cover = CloudCover.Create(exploration, IsLand, _nature.Data.Cover, palette, terrain.CellM, preset.Exploration?.RevealRealSeconds ?? 0, _nature.Wind.DirectionX, _nature.Wind.DirectionZ);
            AddChild(_cover);
        }

        _figuresLook = Read<LookFigures>(content, "look", "figures.json");
        _objectsView.SetupCrownFade(_figuresLook.CrownFade);
        _appearanceLook = Read<LookAppearance>(content, "look", "appearance.json");
        var library = Read<AnimationLibrary>(content, "animation", "library.json");
        _figureShared = new FigureShared(library, _figuresLook, host.World.Calendar.Settings, Speed) { HullOutline = quality.HullOutline };
        _speedSeen = Speed;
        ApplyIsland(1, place: true);

        var width = map.Map.WidthCells * terrain.CellM;
        var depth = map.Map.DepthCells * terrain.CellM;
        var seaLevel = (float)_seaLevelM;
        var camera = Read<LookCamera>(content, "look", "camera.json");
        AttachRig(
            camera,
            CameraRig.Create(camera, width, depth, () => _islandNodes.Select(f => (f.Key, f.Value.Position)), (x, z) => Math.Max(seaLevel, IslandView.GroundHeight(ground, terrain, x, z))),
            id => _islandNodes.TryGetValue(id, out var node) ? node.Position : (Vector3?)null,
            _look.Observer);

        LightTheScene(content, _look, "IslandPlaceholder", "WaterPlaceholder");
        GD.Print($"ISLAND_WORLD preset={map.Island.PresetId} chunks={_islandView.GroundChunks} figures={_islandFigures.Count} objects_drawn={_objectsView.Drawn} layers={_objectsView.Layers}");
        GD.Print(_cover is null ? "ISLAND_COVER none" : $"ISLAND_COVER cells={_cover.Map.Width}x{_cover.Map.Depth} version={_cover.Map.Version} seen_permille={(int)(_cover.Map.SeenShare * 1000)}");
        GD.Print($"ISLAND_NATURE grass_plants={_grassView.Plants} grass_layers={_grassView.Layers} season={_seasonSeen} season_colors={_nature.Season.Slots.Ids.Count} weather_looks={_nature.WeatherWithLook.Count}");
        GD.Print($"ISLAND_QUALITY {quality.Describe()}");
    }

    /// <summary>
    /// The placeholder world gets the same camera as the island (B2567): the rig over the plain ground,
    /// the observer showing the groups until the player takes the camera.
    /// </summary>
    private void BeginPlaceholderCamera()
    {
        RebuildGroups();
        var camera = Read<LookCamera>(_contentDirectory, "look", "camera.json");
        var (w, d) = (PlaceholderGroundM.Width / 2, PlaceholderGroundM.Depth / 2);
        AttachRig(
            camera,
            CameraRig.Create(camera, -w, -d, w, d, () => _figures.Select(f => (f.Key, f.Value.Position))),
            id => _figures.TryGetValue(id, out var node) ? node.Position : (Vector3?)null,
            ReadLook(_contentDirectory).Observer);
    }

    /// <summary>Wires the rig to the observer and the game screen (the screen owns the left click) and starts it above the first group.</summary>
    private void AttachRig(LookCamera camera, CameraRig rig, Func<string, Vector3?> findFigure, WorldLookObserver observer)
    {
        _rig = rig;
        _rig.SelectOnClick = false;
        _rig.Held = CameraHeld;
        _rig.FindFigure = findFigure;
        _rig.Panned += () => _observer?.PlayerTookCamera(_clock);

        // The leave key (and the reset key) hand the camera back to the observer at once (GW3 #113, finding 4).
        _rig.Left += () => _observer?.HandBack(_clock);
        _rig.ResetPressed += () => _observer?.HandBack(_clock);
        AddChild(_rig);
        _observer = new ObserverFocus(observer);
        if (_observer.Update(_clock, _groups) is { } first && Centre(first) is { } start)
        {
            // Start right above the first group, then follow it smoothly.
            _rig.Model.Set(start.X, start.Z, camera.StartYawDeg, observer.DistanceM);
            _rig.Apply();
        }
    }

    /// <summary>Takes back a half-built island (nodes, scene light, state), so the placeholder world can start.</summary>
    private void UndoIsland()
    {
        RestoreScene();
        foreach (var child in GetChildren())
        {
            RemoveChild(child);
            child.QueueFree();
        }

        _host = null;
        Mode = WorldLookMode.Placeholder;
        _look = null;
        _islandView = null;
        _objectsView = null;
        _inventionsView = null;
        _grassView = null;
        _cover = null;
        _nature = null;
        _rig = null;
        _figureShared = null;
        _quality = null;
        _observer = null;
        _islandNodes.Clear();
        _islandFigures.Clear();
        _thoughts.Clear();
        _groups.Clear();
        _groupMembers.Clear();
        _ground = (_, _) => 0;
        _seaLevelM = double.NegativeInfinity;
    }

    /// <summary>
    /// The day light of the data on the scene's sun and environment (island and placeholder world); the named
    /// scene nodes are hidden (restored on exit). It follows the world clock unless <c>day_phase</c> fixes a phase.
    /// </summary>
    private void LightTheScene(string content, LookWorld look, params string[] hide)
    {
        if (GetParent() is not { } scene)
        {
            // Not in a scene (the smoke's load check): nothing to light.
            return;
        }

        var hidden = new List<Node3D>();
        foreach (var name in hide)
        {
            if (scene.GetNodeOrNull<Node3D>(name) is { Visible: true } node)
            {
                node.Visible = false;
                hidden.Add(node);
            }
        }

        var worldEnvironment = scene.GetChildren().OfType<WorldEnvironment>().FirstOrDefault();
        var sun = scene.GetChildren().OfType<DirectionalLight3D>().FirstOrDefault();
        _restore = (worldEnvironment?.Environment, sun, sun?.LightColor ?? Colors.White, sun?.LightEnergy ?? 1, sun?.RotationDegrees ?? Vector3.Zero, sun?.ShadowEnabled ?? false, sun?.ShadowOpacity ?? 1, sun?.DirectionalShadowMode ?? DirectionalLight3D.ShadowMode.Parallel4Splits, sun?.DirectionalShadowMaxDistance ?? 100, hidden);
        if (sun is null)
        {
            sun = new DirectionalLight3D { Name = "IslandSun" };
            AddChild(sun);
        }

        var daylight = Read<LookDaylight>(content, "look", "daylight.json");
        var environment = new Godot.Environment();
        if (worldEnvironment is null)
        {
            AddChild(new WorldEnvironment { Name = "IslandEnvironment", Environment = environment });
        }
        else
        {
            worldEnvironment.Environment = environment;
        }

        DaylightLook.ApplyRendering(daylight, sun, environment);

        // The sun's shadow as the quality level renders it (the island set the level; the placeholder world reads it here).
        _quality ??= ReadQuality(content);
        QualityLook.Apply(_quality, sun);
        _daylight = (daylight, sun, environment, look.DayPhase == DaylightClock.FollowClock ? null : DaylightMix.Of(daylight, look.DayPhase));
        FollowTheClock();
    }

    /// <summary>
    /// Each frame: the light of the current world time, with the fraction of the running tick (smooth at
    /// x16), or of the fixed day phase; on the island under the shown weather (B2-5 R23: dimmer or tinted
    /// light, clouds, fog, the soft storm glow). Applied only when something changed.
    /// </summary>
    private void FollowTheClock()
    {
        if (_daylight is not { } d || _host is null || !IsInstanceValid(d.Sun))
        {
            return;
        }

        var mix = d.Fixed ?? DaylightClock.At(d.Data, _host.World.Calendar, _host.World.Tick, _accumulator);
        WeatherSky? sky = null;
        if (_nature is not null)
        {
            (mix, sky) = _nature.Shade(mix, d.Data.NightMinBrightness);
        }

        // A quality level without shadows: no shadow in any phase.
        mix = _quality?.Light(mix) ?? mix;

        if (mix != _daylightShown || sky != _skyShown)
        {
            (_daylightShown, _skyShown) = (mix, sky);
            DaylightLook.Apply(d.Data, mix, d.Sun, d.Environment, sky, _nature?.WeatherData.Clouds);
            _nature?.Light(mix.WaterLight);
        }
    }

    /// <summary>
    /// Each frame of island mode: the season and the weather of the last snapshot move the season colours,
    /// the snow, the plants in the wind, the falling leaves, rain and snow and the weather blend (B2-5 R21,
    /// R23, R24); its look epoch blends the palette (R16) and its tide moves the sea surface (R15). Real
    /// time, so fast forward stays calm; pause and speed 0 hold everything still, like the figures (W1688).
    /// </summary>
    private void FollowNature(double delta)
    {
        if (_nature is null || _rig is null || _host is null || _seasonSeen.Length == 0)
        {
            return;
        }

        var (x, z) = _rig.Model.Target;
        var focus = new Vector3((float)x, Math.Max(GroundHeight(x, z), (float)_seaLevelM), (float)z);
        var calendar = _host!.World.Calendar;
        _islandView?.FollowTide(_tideSeen, _freeze.Frozen ? 0 : delta);
        _nature.Follow(_seasonSeen, _epochSeen, _weatherSeen, _freeze.Frozen ? 0 : delta, focus, Speed, calendar.RealSecondsPerTickAtNormalSpeed * calendar.Settings.TicksPerHour / 60.0);
        _cover?.Advance(_freeze.Frozen ? 0 : delta);
    }

    /// <summary>Puts back what <see cref="LightTheScene"/> changed in the scene (back to the menu).</summary>
    private void RestoreScene()
    {
        if (_restore is not { } r)
        {
            return;
        }

        _restore = null;
        _daylight = null;
        _daylightShown = null;
        _skyShown = null;
        _quality = null;

        // The shadow map size and filter are global renderer settings: back to the project's.
        QualityLook.RestoreRenderer();
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
            sun.ShadowOpacity = r.SunShadowOpacity;
            sun.DirectionalShadowMode = r.SunShadowMode;
            sun.DirectionalShadowMaxDistance = r.SunShadowDistance;
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

        if (_freeze.Update(Speed))
        {
            // Pause and speed 0 stop the figures' clips and procedural motion too (W1688).
            foreach (var figure in _islandFigures.Values)
            {
                figure.SetFrozen(_freeze.Frozen);
            }
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
        (_seasonSeen, _weatherSeen, _epochSeen, _tideSeen) = (snapshot.Time.SeasonId, snapshot.Weather, snapshot.LookEpoch, snapshot.Tide);
        if (_host.Exploration is { } exploration)
        {
            // The player's view of the core (R25), asked where the snapshot is taken.
            _cover?.Take(exploration, snapshot.ExploredVersion);
        }

        // Inventions with a building plan are drawn from it (A1375): their objects are not the object view's.
        // A world without a building plan (most of the time) costs nothing here.
        IReadOnlyList<Simulation.Objects.WorldObject> objects = snapshot.Objects;
        var builds = _host.Inventions.Builds.Where(b => b.PlanJson is not null).ToList();
        if (builds.Count > 0 || _inventionsView?.Drawn > 0)
        {
            _inventionsView?.Refresh(InventionLooks.Shown(builds, snapshot.Objects));
            var planned = InventionLooks.DrawnTypes(builds);
            objects = planned.Count == 0 ? snapshot.Objects : [.. snapshot.Objects.Where(o => !planned.Contains(o.TypeId))];
        }

        // Seats (W1688) are taken from the objects only when they changed.
        if (_objectsView?.Refresh(objects) != false || _seats is null)
        {
            _seats = SeatChoice.Seats(_figuresLook!.Sitting, snapshot.Objects);
        }

        HashSet<string>? fresh = null;
        if (!SameRoster(snapshot.Inhabitants))
        {
            // Births, deaths, new inhabitants: only then the roster is compared and the groups rebuilt.
            var (added, removed) = FigureRoster.Diff(_islandNodes.Keys, snapshot.Inhabitants.Select(i => i.Id));
            foreach (var id in removed)
            {
                RemoveFigure(id);
            }

            fresh = new HashSet<string>(added, StringComparer.Ordinal);
            foreach (var inhabitant in snapshot.Inhabitants.Where(i => fresh.Contains(i.Id)))
            {
                AddFigure(inhabitant);
            }

            RebuildGroups();
        }

        foreach (var inhabitant in snapshot.Inhabitants)
        {
            if (_islandFigures.TryGetValue(inhabitant.Id, out var figure))
            {
                figure.Apply(inhabitant, snapshot.Time.Tick, ticks, place || fresh?.Contains(inhabitant.Id) == true, snapshot.Inhabitants);
            }
            else if (_islandNodes.TryGetValue(inhabitant.Id, out var standIn))
            {
                var (x, z) = (inhabitant.Position.X, inhabitant.Position.Z);
                standIn.Position = new Vector3((float)x, GroundHeight(x, z), (float)z);
            }
        }
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
            var figure = FigureView.Create(inhabitant, spec, _figureShared!, (x, z) => (float)_ground(x, z), () => _seats ?? []);
            figure.SetFrozen(_freeze.Frozen);
            _islandFigures[inhabitant.Id] = figure;
            node = figure;
            head = figure.HeadAnchor;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A figure that cannot be built stands in as a calm capsule; the world runs on (logged once).
            if (_islandReported.Add("figure:" + inhabitant.Id))
            {
                GD.PushWarning($"FIGURE_STAND_IN {inhabitant.Id}: {e.Message}");
            }

            var height = (float)(_figuresLook?.HeadAnchorM ?? 1.9);
            var standIn = _look!.StandIn;
            node = new Node3D { Name = inhabitant.Id };
            node.AddChild(new MeshInstance3D { Name = "StandIn", Mesh = new CapsuleMesh { Radius = (float)standIn.RadiusM, Height = (float)standIn.HeightM }, Position = new Vector3(0, (float)standIn.HeightM / 2, 0) });
            head = new Node3D { Name = HeadAnchorName, Position = new Vector3(0, height, 0) };
            node.AddChild(head);
            var (x, z) = (inhabitant.Position.X, inhabitant.Position.Z);
            node.Position = new Vector3((float)x, GroundHeight(x, z), (float)z);
        }

        AddChild(node);
        _islandNodes[inhabitant.Id] = node;
        var labels = _look!.Labels;
        var label = new GlassWorld.Game.World.ScreenBubble
        {
            Name = "Thought",
            Position = new Vector3(0, (float)labels.ThoughtAboveHeadM, 0),
            Visible = false,
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
        _speechUntil.Remove(id);
        if (_observer?.Chosen == id)
        {
            _observer.Release(_clock);
            _rig?.Model.StopFollowing();
        }
    }

    /// <summary>The observer camera: follows the shown group while the player leaves the camera to it.</summary>
    private void ObserverCamera()
    {
        if (_rig is null || _observer is null)
        {
            return;
        }

        var group = _observer.Update(_clock, _groups);
        var observing = _observer.Observing(_clock);
        if (observing && group is { } g && (g != _observedGroup || !_wasObserving))
        {
            // Keep the player's zoom; the observer only moves the target.
            _rig.Model.Follow(() => Centre(g) is { } c ? (c.X, c.Z) : _rig.Model.Target, _rig.Model.GoalDistance);
            _observedGroup = g;
        }

        _wasObserving = observing;
    }

    /// <summary>
    /// A conversation event (W1688): the hearers of the player's sentence look and turn to the voice, the
    /// speaker shows its answer (wave, talking); the camera follows the one spoken to.
    /// </summary>
    private void React(Simulation.Inhabitants.WorldEvent e)
    {
        if (_figuresLook is null || GreetingReaction.For(_figuresLook.Greeting, e.Kind, e.Detail, e.Text is { Length: > 0 }) is not { } reaction)
        {
            return;
        }

        var ids = e.Kind == "conversation_started"
            ? GreetingReaction.Hearers(_figuresLook.Greeting, e.InhabitantId, _islandNodes.Select(n => (n.Key, (double)n.Value.Position.X, (double)n.Value.Position.Z)))
            : [e.InhabitantId];
        foreach (var id in ids)
        {
            if (_islandFigures.TryGetValue(id, out var figure))
            {
                figure.React(reaction);
            }
        }

        if (e.Kind == "conversation_started")
        {
            // The player spoke to it (maybe from the list, with the camera elsewhere): the camera follows
            // it, so its reaction and answer are seen.
            ChooseFigure(e.InhabitantId);
        }
    }

    /// <summary>Tree crowns between the camera and the chosen inhabitant (or every one, data) fade (W1689).</summary>
    private void KeepFiguresVisible()
    {
        if (_objectsView is null || _figuresLook is not { CrownFade.Enabled: true } look || _rig is null)
        {
            return;
        }

        _fadeFor.Clear();
        foreach (var (id, figure) in _islandFigures)
        {
            if (look.CrownFade.AllInhabitants || _observer?.Chosen == id)
            {
                _fadeFor.Add(figure.HeadAnchor.GlobalPosition);
            }
        }

        _objectsView.FadeCrowns(_rig.Camera.GlobalPosition, _fadeFor);
    }

    /// <summary>
    /// Raised when an inhabitant has a new thought: the screen shows a hint with a "look" button; the
    /// camera itself does not move (TL #19976).
    /// </summary>
    public event Action<string>? Noticed;

    /// <summary>
    /// The player let go of the camera (Esc): a followed figure is no longer followed. True if the
    /// camera was following.
    /// </summary>
    public bool StopFollowing()
    {
        if (_rig is null || !_rig.Model.Following)
        {
            return false;
        }

        _rig.Model.StopFollowing();
        _observer?.Release(_clock);
        return true;
    }

    /// <summary>A thought appeared: a hint for the screen; only an observer with auto on turns to that inhabitant's group.</summary>
    private void ObserveThought(string inhabitantId)
    {
        Noticed?.Invoke(inhabitantId);
        if (_observer is not null && _host is not null)
        {
            _observer.Thought(_host.Inhabitants.Get(inhabitantId).Group, _clock);
        }
    }

    /// <summary>Whether the snapshot has exactly the inhabitants that have figures (no allocation).</summary>
    private bool SameRoster(IReadOnlyList<InhabitantSnapshot> inhabitants)
    {
        if (inhabitants.Count != _islandNodes.Count)
        {
            return false;
        }

        for (var i = 0; i < inhabitants.Count; i++)
        {
            if (!_islandNodes.ContainsKey(inhabitants[i].Id))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Groups in order and their figures, rebuilt when the roster changes (not every frame).</summary>
    private void RebuildGroups()
    {
        _groups.Clear();
        _groupMembers.Clear();
        if (_host is null)
        {
            return;
        }

        foreach (var inhabitant in _host.Inhabitants.All)
        {
            // Island figures, or the stand-ins of the placeholder world.
            Node3D? node = _islandNodes.TryGetValue(inhabitant.Id, out var figure) ? figure : _figures.GetValueOrDefault(inhabitant.Id);
            if (node is null)
            {
                continue;
            }

            if (!_groupMembers.TryGetValue(inhabitant.Group, out var members))
            {
                _groupMembers[inhabitant.Group] = members = [];
                _groups.Add(inhabitant.Group);
            }

            members.Add(node);
        }

        _groups.Sort();
    }

    /// <summary>Middle of a group's figures on the ground, or null without figures (a plain loop, no allocation).</summary>
    private (double X, double Z)? Centre(int group)
    {
        if (!_groupMembers.TryGetValue(group, out var members) || members.Count == 0)
        {
            return null;
        }

        double x = 0, z = 0;
        foreach (var member in members)
        {
            x += member.Position.X;
            z += member.Position.Z;
        }

        return (x / members.Count, z / members.Count);
    }
}
