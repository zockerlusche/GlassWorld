using GlassWorld.Contracts;
using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// The island camera (B2 R26, mouse first B2567) as a Godot node around <see cref="CameraModel"/>: drag
/// with the orbit button (right or middle) to orbit and tilt, drag the ground with the select button (or
/// the orbit button with Shift) to pan, wheel to zoom towards the cursor, keys to pan and turn smoothly,
/// the reset key for the start view; click a figure to follow it, the leave key or a click into empty
/// ground to stop. A short press is a click, a longer move a drag (<see cref="ClickGate"/>), so a drag
/// never selects. Controls are InputMap actions (rebindable); defaults are added only where the project
/// does not define the action. A click on a figure reports its id (for the inhabitant card, B6).
/// </summary>
public partial class CameraRig : Node3D
{
    /// <summary>Orbit (turn and tilt) while held.</summary>
    public const string RotateAction = "gw_camera_rotate";

    /// <summary>Zoom in one step.</summary>
    public const string ZoomInAction = "gw_camera_zoom_in";

    /// <summary>Zoom out one step.</summary>
    public const string ZoomOutAction = "gw_camera_zoom_out";

    /// <summary>Pan actions (left, right, forward, back).</summary>
    public static readonly string[] PanActions = ["gw_camera_pan_left", "gw_camera_pan_right", "gw_camera_pan_forward", "gw_camera_pan_back"];

    /// <summary>Turn actions (left, right).</summary>
    public static readonly string[] TurnActions = ["gw_camera_turn_left", "gw_camera_turn_right"];

    /// <summary>Back to the start view.</summary>
    public const string ResetAction = "gw_camera_reset";

    /// <summary>Leave follow mode.</summary>
    public const string LeaveAction = "gw_camera_leave";

    /// <summary>Select (click); dragging with it pans.</summary>
    public const string SelectAction = "gw_select";

    private Camera3D _camera = null!;
    private float _pickRadiusM;
    private Func<IEnumerable<(string Id, Vector3 Position)>> _figures = () => [];
    private ClickGate _select = null!;
    private bool _orbiting;
    private Vector2 _pointer;

    /// <summary>Raised when a figure is clicked (its inhabitant id).</summary>
    public event Action<string>? FigureClicked;

    /// <summary>Raised in a frame in which the player pans the camera (panning leaves follow mode).</summary>
    public event Action? Panned;

    /// <summary>Raised when the player presses the leave key (follow mode ends).</summary>
    public event Action? Left;

    /// <summary>Raised when the player presses the reset key (the camera glides back to the start view).</summary>
    public event Action? ResetPressed;

    /// <summary>
    /// Optional lookup of one figure's position by id, used by <see cref="FollowFigure"/> every frame
    /// instead of searching all figures (no allocation per frame); null = search the figure list.
    /// </summary>
    public Func<string, Vector3?>? FindFigure { get; set; }

    /// <summary>
    /// Whether the rig itself picks figures on a left click (island scene). In the main game the game
    /// screen owns the left click (gift placement or figure selection) and tells the world, so one click
    /// never does both; there this is false. Dragging with the left button pans in both cases.
    /// </summary>
    public bool SelectOnClick { get; set; } = true;

    /// <summary>Picture runs: the rig neither reads input nor moves the camera (the run places it).</summary>
    public bool Held { get; set; }

    /// <summary>The camera logic (tested in Game.Presentation).</summary>
    public CameraModel Model { get; private set; } = null!;

    /// <summary>Creates the rig over the island area (0..width, 0..depth).</summary>
    public static CameraRig Create(LookCamera data, float width, float depth, Func<IEnumerable<(string Id, Vector3 Position)>> figures, Func<double, double, float>? groundHeight = null) =>
        Create(data, 0, 0, width, depth, figures, groundHeight);

    /// <summary>Creates the rig over a ground area given by its corners in metres.</summary>
    public static CameraRig Create(LookCamera data, float minX, float minZ, float maxX, float maxZ, Func<IEnumerable<(string Id, Vector3 Position)>> figures, Func<double, double, float>? groundHeight = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(figures);
        EnsureDefaultInput();
        Func<double, double, double>? ground = groundHeight is null ? null : (x, z) => groundHeight(x, z);
        var rig = new CameraRig
        {
            Name = "CameraRig",
            Model = new CameraModel(data, minX, minZ, maxX, maxZ, ground),
            _figures = figures,
            _pickRadiusM = (float)data.PickRadiusM,
            _select = new ClickGate(data.ClickMaxPx, data.ClickMaxMs),
        };
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
        // A button released while the window had no focus (or swallowed by a GUI) never reaches the rig,
        // so orbiting and dragging stop whenever focus is lost.
        if (what is (int)NotificationApplicationFocusOut or (int)NotificationWMWindowFocusOut)
        {
            _orbiting = false;
            _select?.Cancel();
        }
    }

    /// <inheritdoc/>
    public override void _UnhandledInput(InputEvent @event)
    {
        // Presses start here, so a press on a window or button never moves the camera; the moves and
        // releases of a started press are read in _Input, so a drag across a panel keeps going.
        // A left click is also the gift placement of the game screen: in the main game the screen owns
        // the left click (SelectOnClick = false) and decides between gift placement and figure selection;
        // it uses the same click gate, so a drag never selects or places.
        // A press into the world (not on a control) takes the focus off the GUI, so the keys move the
        // camera again.
        if (@event is InputEventMouseButton { Pressed: true })
        {
            GetViewport()?.GuiReleaseFocus();
        }

        if (Held)
        {
            return;
        }

        // The wheel over a window, menu or list belongs to it, never to the map (TL #19986), also where the
        // control itself does not scroll.
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown or MouseButton.WheelLeft or MouseButton.WheelRight }
            && GetViewport()?.GuiGetHoveredControl() is not null)
        {
            return;
        }

        if (@event.IsActionPressed(ZoomInAction))
        {
            ZoomBy(@event, 1);
        }
        else if (@event.IsActionPressed(ZoomOutAction))
        {
            ZoomBy(@event, -1);
        }
        else if (@event.IsActionPressed(RotateAction) && @event is InputEventMouse orbit)
        {
            _orbiting = true;
            _pointer = orbit.Position;
        }
        else if (@event.IsActionPressed(LeaveAction))
        {
            Model.StopFollowing();
            Left?.Invoke();
        }
        else if (@event.IsActionPressed(ResetAction))
        {
            Model.Reset();
            ResetPressed?.Invoke();
        }
        else if (@event.IsActionPressed(SelectAction) && @event is InputEventMouseButton press)
        {
            _select.Press(press.Position.X, press.Position.Y, Time.GetTicksMsec());
            _pointer = press.Position;
        }
    }

    /// <inheritdoc/>
    public override void _Input(InputEvent @event)
    {
        if (Held)
        {
            return;
        }

        if (@event is InputEventMouseMotion motion)
        {
            if (_orbiting)
            {
                if (motion.ShiftPressed)
                {
                    GrabTo(motion.Position);
                }
                else
                {
                    Model.Drag(motion.Relative.X, motion.Relative.Y);
                    _pointer = motion.Position;
                }
            }
            else if (_select.Pressed && _select.Move(motion.Position.X, motion.Position.Y))
            {
                GrabTo(motion.Position);
            }
        }
        else if (_orbiting && @event.IsActionReleased(RotateAction))
        {
            _orbiting = false;
        }
        else if (_select.Pressed && @event.IsActionReleased(SelectAction) && @event is InputEventMouseButton release)
        {
            if (_select.Release(release.Position.X, release.Position.Y, Time.GetTicksMsec()) && SelectOnClick)
            {
                Select(release.Position);
            }
        }
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (Held)
        {
            return;
        }

        // A GUI control can swallow a button's release, so the rig never sees it; the Input state still
        // knows the button is up, so orbiting or dragging stops on the next frame.
        if (_orbiting && !Input.IsActionPressed(RotateAction))
        {
            _orbiting = false;
        }

        if (_select.Pressed && !Input.IsActionPressed(SelectAction))
        {
            _select.Cancel();
        }

        // While a control that uses these keys has the focus (a text field, slider, list, option button)
        // they belong to it and never move the camera; a plain button that took the focus on a click does
        // not block them (GW3 review #144).
        var keys = !BlocksCameraKeys(GetViewport()?.GuiGetFocusOwner());
        var right = keys ? Input.GetAxis(PanActions[0], PanActions[1]) : 0;
        var forward = keys ? Input.GetAxis(PanActions[3], PanActions[2]) : 0;
        var turn = keys ? Input.GetAxis(TurnActions[0], TurnActions[1]) : 0;
        Model.Move(right, forward, turn, delta);
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

    /// <summary>Whether a focused control uses the pan and turn keys itself (typing, sliding, list navigation).</summary>
    public static bool BlocksCameraKeys(Control? focus) => focus is LineEdit or TextEdit or Godot.Range or ItemList or Tree or OptionButton;

    /// <summary>A screen point in normalised device coordinates (y up) and the aspect of the viewport.</summary>
    private (double X, double Y, double Aspect) Ndc(Vector2 screen)
    {
        var size = GetViewport()?.GetVisibleRect().Size ?? Vector2.One;
        var w = Math.Max(1, size.X);
        var h = Math.Max(1, size.Y);
        return ((2 * screen.X / w) - 1, 1 - (2 * screen.Y / h), w / h);
    }

    private void ZoomBy(InputEvent @event, double sign)
    {
        // The wheel zooms towards the cursor (smooth wheels report a factor); the keys towards the centre.
        if (@event is InputEventMouseButton wheel)
        {
            var (x, y, aspect) = Ndc(wheel.Position);
            Model.ZoomAt(sign * (wheel.Factor > 0 ? wheel.Factor : 1), x, y, aspect);
            return;
        }

        Model.Zoom(sign);
    }

    private void GrabTo(Vector2 screen)
    {
        var (fx, fy, aspect) = Ndc(_pointer);
        var (tx, ty, _) = Ndc(screen);
        _pointer = screen;
        if (Model.Grab((fx, fy), (tx, ty), aspect))
        {
            Panned?.Invoke();
        }
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

        Add(RotateAction, new InputEventMouseButton { ButtonIndex = MouseButton.Right }, new InputEventMouseButton { ButtonIndex = MouseButton.Middle });
        Add(ZoomInAction, new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp }, new InputEventKey { PhysicalKeycode = Key.Equal });
        Add(ZoomOutAction, new InputEventMouseButton { ButtonIndex = MouseButton.WheelDown }, new InputEventKey { PhysicalKeycode = Key.Minus });
        Add(PanActions[0], new InputEventKey { PhysicalKeycode = Key.A }, new InputEventKey { PhysicalKeycode = Key.Left });
        Add(PanActions[1], new InputEventKey { PhysicalKeycode = Key.D }, new InputEventKey { PhysicalKeycode = Key.Right });
        Add(PanActions[2], new InputEventKey { PhysicalKeycode = Key.W }, new InputEventKey { PhysicalKeycode = Key.Up });
        Add(PanActions[3], new InputEventKey { PhysicalKeycode = Key.S }, new InputEventKey { PhysicalKeycode = Key.Down });
        Add(TurnActions[0], new InputEventKey { PhysicalKeycode = Key.Q });
        Add(TurnActions[1], new InputEventKey { PhysicalKeycode = Key.E });
        Add(ResetAction, new InputEventKey { PhysicalKeycode = Key.Home });
        Add(LeaveAction, new InputEventKey { PhysicalKeycode = Key.Backspace });
        Add(SelectAction, new InputEventMouseButton { ButtonIndex = MouseButton.Left });
    }
}
