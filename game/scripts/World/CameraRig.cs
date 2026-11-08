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

    private const float PickRadiusM = 1.5f;

    private Camera3D _camera = null!;
    private Func<IEnumerable<(string Id, Vector3 Position)>> _figures = () => [];
    private bool _rotating;

    /// <summary>Raised when a figure is clicked (its inhabitant id).</summary>
    public event Action<string>? FigureClicked;

    /// <summary>The camera logic (tested in Game.Presentation).</summary>
    public CameraModel Model { get; private set; } = null!;

    /// <summary>Creates the rig over the island area.</summary>
    public static CameraRig Create(LookCamera data, float width, float depth, Func<IEnumerable<(string Id, Vector3 Position)>> figures, Func<double, double, float>? groundHeight = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(figures);
        EnsureDefaultInput();
        Func<double, double, double>? ground = groundHeight is null ? null : (x, z) => groundHeight(x, z);
        var rig = new CameraRig { Name = "CameraRig", Model = new CameraModel(data, 0, 0, width, depth, ground), _figures = figures };
        rig._camera = new Camera3D { Name = "Camera", Current = true, Near = 0.3f, Far = 4000f };
        rig.AddChild(rig._camera);
        rig.Apply();
        return rig;
    }

    /// <summary>Godot camera of the rig.</summary>
    public Camera3D Camera => _camera;

    /// <summary>Follows the figure with this id (its position is read every frame).</summary>
    public void FollowFigure(string id) =>
        Model.Follow(() => _figures().Where(f => f.Id == id).Select(f => ((double)f.Position.X, (double)f.Position.Z)).DefaultIfEmpty(Model.Target).First());

    /// <inheritdoc/>
    public override void _UnhandledInput(InputEvent @event)
    {
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
        }
        else if (@event.IsActionPressed(SelectAction) && @event is InputEventMouseButton click)
        {
            Select(click.Position);
        }
        else if (_rotating && @event is InputEventMouseMotion motion)
        {
            Model.Drag(motion.Relative.X);
        }
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        var right = Input.GetAxis(PanActions[0], PanActions[1]);
        var forward = Input.GetAxis(PanActions[3], PanActions[2]);
        Model.Pan(right, forward, delta);
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
        var bestDistance = PickRadiusM;
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
