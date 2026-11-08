using GlassWorld.Contracts;
using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// The island camera (B2 R26) as a Godot node around <see cref="CameraModel"/>: drag with the rotate
/// button, wheel to zoom, keys to pan, click a figure to follow it, the leave key or a click into empty
/// ground to stop. Controls are InputMap actions (rebindable); defaults are added only where the project
/// does not define the action. A click on a figure reports its id (for the inhabitant card, B6).
/// </summary>
public partial class CameraRig : Node3D
{
    /// <summary>Rotate while held.</summary>
    public const string RotateAction = "gw_camera_rotate";

    /// <summary>Zoom in one step.</summary>
    public const string ZoomInAction = "gw_camera_zoom_in";

    /// <summary>Zoom out one step.</summary>
    public const string ZoomOutAction = "gw_camera_zoom_out";

    /// <summary>Pan actions (left, right, forward, back).</summary>
    public static readonly string[] PanActions = ["gw_camera_pan_left", "gw_camera_pan_right", "gw_camera_pan_forward", "gw_camera_pan_back"];

    /// <summary>Leave follow mode.</summary>
    public const string LeaveAction = "gw_camera_leave";

    /// <summary>Select (click).</summary>
    public const string SelectAction = "gw_select";

    private Camera3D _camera = null!;
    private float _pickRadiusM;
    private Func<IEnumerable<(string Id, Vector3 Position)>> _figures = () => [];
    private bool _rotating;

    /// <summary>Raised when a figure is clicked (its inhabitant id).</summary>
    public event Action<string>? FigureClicked;

    /// <summary>Raised in a frame in which the player pans the camera (panning leaves follow mode).</summary>
    public event Action? Panned;

    /// <summary>Raised when the player presses the leave key (follow mode ends).</summary>
    public event Action? Left;

    /// <summary>
    /// Optional lookup of one figure's position by id, used by <see cref="FollowFigure"/> every frame
    /// instead of searching all figures (no allocation per frame); null = search the figure list.
    /// </summary>
    public Func<string, Vector3?>? FindFigure { get; set; }

    /// <summary>
    /// Whether the rig itself picks figures on a left click (island scene). In the main game the game
    /// screen owns the left click (gift placement or figure selection) and tells the world, so one click
    /// never does both; there this is false.
    /// </summary>
    public bool SelectOnClick { get; set; } = true;

    /// <summary>The camera logic (tested in Game.Presentation).</summary>
    public CameraModel Model { get; private set; } = null!;

    /// <summary>Creates the rig over the island area.</summary>
    public static CameraRig Create(LookCamera data, float width, float depth, Func<IEnumerable<(string Id, Vector3 Position)>> figures, Func<double, double, float>? groundHeight = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(figures);
        EnsureDefaultInput();
        Func<double, double, double>? ground = groundHeight is null ? null : (x, z) => groundHeight(x, z);
        var rig = new CameraRig { Name = "CameraRig", Model = new CameraModel(data, 0, 0, width, depth, ground), _figures = figures, _pickRadiusM = (float)data.PickRadiusM };
        rig._camera = new Camera3D { Name = "Camera", Current = true, Near = (float)data.NearM, Far = (float)data.FarM };
        rig.AddChild(rig._camera);
        rig.Apply();
        return rig;
    }

    /// <summary>Godot camera of the rig.</summary>
    public Camera3D Camera => _camera;

    /// <summary>Follows the figure with this id (its position is read every frame), at the follow distance or <paramref name="distanceM"/>.</summary>
    public void FollowFigure(string id, double? distanceM = null)
    {
        if (FindFigure is { } find)
        {
            Model.Follow(() => find(id) is { } p ? (p.X, p.Z) : Model.Target, distanceM);
            return;
        }

        Model.Follow(() => _figures().Where(f => f.Id == id).Select(f => ((double)f.Position.X, (double)f.Position.Z)).DefaultIfEmpty(Model.Target).First(), distanceM);
    }

    /// <inheritdoc/>
    public override void _Notification(int what)
    {
        // A rotate button released while the window had no focus (or swallowed by a GUI) never reaches
        // the rig, so rotating stops whenever focus is lost.
        if (what is (int)NotificationApplicationFocusOut or (int)NotificationWMWindowFocusOut)
        {
            _rotating = false;
        }
    }

    /// <inheritdoc/>
    public override void _UnhandledInput(InputEvent @event)
    {
        // A left click is also the gift placement of the game screen: in the main game the screen owns
        // the left click (SelectOnClick = false) and decides between gift placement and figure selection.
        if (@event.IsActionPressed(ZoomInAction))
        {
            Model.Zoom(1);
        }
        else if (@event.IsActionPressed(ZoomOutAction))
        {
            Model.Zoom(-1);
        }
        else if (@event.IsActionPressed(RotateAction))
        {
            _rotating = true;
        }
        else if (@event.IsActionReleased(RotateAction))
        {
            _rotating = false;
        }
        else if (@event.IsActionPressed(LeaveAction))
        {
            Model.StopFollowing();
            Left?.Invoke();
        }
        else if (SelectOnClick && @event.IsActionPressed(SelectAction) && @event is InputEventMouseButton click)
        {
            Select(click.Position);
        }
        else if (_rotating && @event is InputEventMouseMotion motion && Input.IsActionPressed(RotateAction))
        {
            Model.Drag(motion.Relative.X);
        }
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        // A GUI control can swallow the rotate button's release, so the rig never sees it; the Input
        // state still knows the button is up, so rotating stops on the next frame.
        if (_rotating && !Input.IsActionPressed(RotateAction))
        {
            _rotating = false;
        }

        // Keys typed into a text field (a message to an inhabitant) never move the camera.
        var typing = GetViewport()?.GuiGetFocusOwner() is LineEdit or TextEdit;
        var right = typing ? 0 : Input.GetAxis(PanActions[0], PanActions[1]);
        var forward = typing ? 0 : Input.GetAxis(PanActions[3], PanActions[2]);
        Model.Pan(right, forward, delta);
        if (right != 0 || forward != 0)
        {
            Panned?.Invoke();
        }

        Model.Update(delta);
        Apply();
    }

    /// <summary>Puts the Godot camera where the model says.</summary>
    public void Apply()
    {
        var eye = Model.Eye;
        var (tx, tz) = Model.Target;
        _camera.Projection = Model.Orthographic ? Camera3D.ProjectionType.Orthogonal : Camera3D.ProjectionType.Perspective;
        _camera.Fov = (float)Model.FovDeg;
        _camera.Size = (float)Model.OrthoSize;
        // The model orbits a point on the ground; its eased height lifts eye and target alike.
        _camera.LookAtFromPosition(new Vector3((float)eye.X, (float)eye.Y, (float)eye.Z), new Vector3((float)tx, (float)Model.TargetHeight, (float)tz), Vector3.Up);
    }

    private void Select(Vector2 screen)
    {
        // Ray against the ground plane at each figure's height; the nearest figure within the radius wins.
        var origin = _camera.ProjectRayOrigin(screen);
        var direction = _camera.ProjectRayNormal(screen);
        string? best = null;
        var bestDistance = _pickRadiusM;
        foreach (var (id, position) in _figures())
        {
            if (Mathf.IsZeroApprox(direction.Y))
            {
                break;
            }

            var t = (position.Y - origin.Y) / direction.Y;
            var hit = origin + (direction * t);
            var distance = new Vector2(hit.X - position.X, hit.Z - position.Z).Length();
            if (t > 0 && distance < bestDistance)
            {
                best = id;
                bestDistance = distance;
            }
        }

        if (best is null)
        {
            Model.StopFollowing();
            return;
        }

        FollowFigure(best);
        FigureClicked?.Invoke(best);
    }

    private static void EnsureDefaultInput()
    {
        void Add(string action, params InputEvent[] events)
        {
            if (InputMap.HasAction(action))
            {
                return;
            }

            InputMap.AddAction(action);
            foreach (var e in events)
            {
                InputMap.ActionAddEvent(action, e);
            }
        }

        Add(RotateAction, new InputEventMouseButton { ButtonIndex = MouseButton.Right });
        Add(ZoomInAction, new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp }, new InputEventKey { PhysicalKeycode = Key.Equal });
        Add(ZoomOutAction, new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown }, new InputEventKey { PhysicalKeycode = Key.Minus });
        Add(PanActions[0], new InputEventKey { PhysicalKeycode = Key.A }, new InputEventKey { PhysicalKeycode = Key.Left });
        Add(PanActions[1], new InputEventKey { PhysicalKeycode = Key.D }, new InputEventKey { PhysicalKeycode = Key.Right });
        Add(PanActions[2], new InputEventKey { PhysicalKeycode = Key.W }, new InputEventKey { PhysicalKeycode = Key.Up });
        Add(PanActions[3], new InputEventKey { PhysicalKeycode = Key.S }, new InputEventKey { PhysicalKeycode = Key.Down });
        Add(LeaveAction, new InputEventKey { PhysicalKeycode = Key.Backspace });
        Add(SelectAction, new InputEventMouseButton { ButtonIndex = MouseButton.Left });
    }
}
