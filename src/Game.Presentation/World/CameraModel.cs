using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>A point in metres on the ground plane and a height.</summary>
public readonly record struct Point3(double X, double Y, double Z);

/// <summary>
/// The island camera without Godot (B2 R26, mouse first B2567): it orbits a ground target freely (yaw
/// stepless, pitch from flat to top-down), pans by grabbing the ground, zooms towards the point under the
/// mouse cursor, moves and turns by keys with eased speed and can follow a figure. All limits, speeds and
/// the smoothing come from <c>content/look/camera.json</c>; mouse moves ease towards their goal, key moves
/// speed up and slow down without overshoot, the same at any frame rate; the height of the ground under
/// the target eases as well, so the view glides over slopes and cliffs instead of jumping. Screen points
/// are given in normalised device coordinates (x, y in -1..1, y up) with the aspect width / height.
/// Presentation only: the camera never changes the world.
/// </summary>
public sealed class CameraModel
{
    private readonly LookCamera _data;
    private readonly (double MinX, double MinZ, double MaxX, double MaxZ) _bounds;
    private readonly (double X, double Z) _centre;
    private double _yaw, _pitch, _distance, _targetX, _targetZ;
    private double _wantYaw, _wantPitch, _wantDistance, _wantX, _wantZ;
    private double _moveRight, _moveForward, _turn;
    private readonly Func<double, double, double> _ground;
    private double _height;
    private Func<(double X, double Z)>? _follow;

    /// <summary>
    /// Creates the camera over a ground area (metres); the target starts at its centre.
    /// <paramref name="groundHeight"/> gives the ground height in metres at (x, z) (none = flat at 0).
    /// </summary>
    public CameraModel(LookCamera data, double minX, double minZ, double maxX, double maxZ, Func<double, double, double>? groundHeight = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _ground = groundHeight ?? ((_, _) => 0);
        var margin = data.BoundsMarginM;
        _bounds = (minX - margin, minZ - margin, maxX + margin, maxZ + margin);
        _centre = ((minX + maxX) / 2, (minZ + maxZ) / 2);
        _yaw = _wantYaw = data.StartYawDeg;
        _pitch = _wantPitch = ClampPitch(data.PitchDeg);
        _distance = _wantDistance = data.DistanceM.Start;
        (_targetX, _targetZ) = (_wantX, _wantZ) = _centre;
        _height = _ground(_targetX, _targetZ);
    }

    /// <summary>The data values (thresholds of the click gate, limits).</summary>
    public LookCamera Data => _data;

    /// <summary>Orthographic or perspective (data value, picture question T1; the screenshot tool may override it).</summary>
    public bool Orthographic => OrthographicOverride ?? _data.Projection == LookCameraProjection.Orthographic;

    /// <summary>Projection set by the screenshot tool (null = data value).</summary>
    public bool? OrthographicOverride { get; set; }

    /// <summary>Field of view set by the screenshot tool (null = data value), e.g. the night shots that show the sky.</summary>
    public double? FovOverride { get; set; }

    /// <summary>Field of view in degrees (perspective).</summary>
    public double FovDeg => FovOverride ?? _data.FovDeg;

    /// <summary>Visible height in metres at the target (orthographic size; same framing as perspective).</summary>
    public double OrthoSize => 2 * _distance * Math.Tan(_data.FovDeg * Math.PI / 360);

    /// <summary>Current yaw in degrees.</summary>
    public double Yaw => _yaw;

    /// <summary>Current pitch (downward tilt) in degrees.</summary>
    public double Pitch => _pitch;

    /// <summary>Current distance in metres.</summary>
    public double Distance => _distance;

    /// <summary>The distance the camera eases towards (the player's zoom, kept when the observer takes over).</summary>
    public double GoalDistance => _wantDistance;

    /// <summary>Current ground target.</summary>
    public (double X, double Z) Target => (_targetX, _targetZ);

    /// <summary>Current (eased) height of the target in metres; it follows the ground under the target.</summary>
    public double TargetHeight => _height;

    /// <summary>
    /// How far out the camera is zoomed, 0 (closest) .. 1 (farthest) over the distance range of the data:
    /// the sound mix widens its ranges with it (WorldAudio.Zoom).
    /// </summary>
    public double ZoomShare => ZoomShareOf(_distance, _data);

    /// <summary>The zoom share of a distance over the data's distance range (0..1, clamped).</summary>
    public static double ZoomShareOf(double distance, LookCamera data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var range = data.DistanceM.Max - data.DistanceM.Min;
        return range > 0 ? Math.Clamp((distance - data.DistanceM.Min) / range, 0, 1) : 0;
    }

    /// <summary>Whether the camera follows a figure.</summary>
    public bool Following => _follow is not null;

    /// <summary>Whether a key move or turn is still running (speeding up, at speed or gliding out).</summary>
    public bool KeyMoving => _moveRight != 0 || _moveForward != 0 || _turn != 0;

    /// <summary>Eye position for the current state.</summary>
    public Point3 Eye
    {
        get
        {
            var pitch = _pitch * Math.PI / 180;
            var yaw = _yaw * Math.PI / 180;
            var flat = _distance * Math.Cos(pitch);
            return new Point3(_targetX + (flat * Math.Sin(yaw)), _height + (_distance * Math.Sin(pitch)), _targetZ + (flat * Math.Cos(yaw)));
        }
    }

    /// <summary>Orbits by a drag in pixels: horizontal turns (stepless, all around), vertical tilts within the pitch range.</summary>
    public void Drag(double pixelsX, double pixelsY = 0)
    {
        _wantYaw += pixelsX * _data.RotateDegPerPx;
        _wantPitch = ClampPitch(_wantPitch + (pixelsY * _data.PitchDegPerPx));
    }

    /// <summary>Zooms by mouse wheel steps (positive = closer) towards the screen centre, within the distance range.</summary>
    public void Zoom(double steps) => _wantDistance = ZoomedDistance(steps);

    /// <summary>
    /// Zooms by mouse wheel steps (positive = closer) towards the screen point (<paramref name="ndcX"/>,
    /// <paramref name="ndcY"/>): the ground point under it stays under it. While following a figure the
    /// camera zooms towards the centre, so the figure stays in view.
    /// </summary>
    public void ZoomAt(double steps, double ndcX, double ndcY, double aspect)
    {
        var distance = ZoomedDistance(steps);
        if (_follow is not null || GroundAt(ndcX, ndcY, aspect) is not { } point)
        {
            _wantDistance = distance;
            return;
        }

        // Scaling eye and target about the ground point keeps every ray direction, so the point stays
        // on its pixel; target and distance ease together, so it also stays there while gliding.
        var factor = distance / _wantDistance;
        _wantX = point.X + ((_wantX - point.X) * factor);
        _wantZ = point.Z + ((_wantZ - point.Z) * factor);
        _wantDistance = distance;
        Clamp();
    }

    /// <summary>
    /// Grabs the ground: the ground point under screen point <paramref name="from"/> moves under
    /// <paramref name="to"/> at once (no lag behind the mouse). Leaves follow mode. False if one of the
    /// points looks above the horizon.
    /// </summary>
    public bool Grab((double X, double Y) from, (double X, double Y) to, double aspect)
    {
        if (GroundAt(from.X, from.Y, aspect) is not { } a || GroundAt(to.X, to.Y, aspect) is not { } b)
        {
            return false;
        }

        _follow = null;
        Shift(a.X - b.X, a.Z - b.Z);
        return true;
    }

    /// <summary>
    /// Key moves for one frame of <paramref name="seconds"/>: <paramref name="right"/> and
    /// <paramref name="forward"/> pan in the camera's own frame, <paramref name="turn"/> turns (positive =
    /// yaw up), each -1..1 as held now. The speed eases towards the held value (data time constants) and
    /// is integrated exactly, so it never overshoots and is the same at any frame rate. Panning leaves
    /// follow mode; it is faster when zoomed out, so the screen speed stays about the same.
    /// </summary>
    public void Move(double right, double forward, double turn, double seconds)
    {
        var dt = Math.Max(0, seconds);
        if (right != 0 || forward != 0)
        {
            _follow = null;
        }

        var panRight = Ease(ref _moveRight, Math.Clamp(right, -1, 1), dt);
        var panForward = Ease(ref _moveForward, Math.Clamp(forward, -1, 1), dt);
        var turned = Ease(ref _turn, Math.Clamp(turn, -1, 1), dt) * _data.KeyTurnDegS;
        _yaw += turned;
        _wantYaw += turned;
        if (panRight == 0 && panForward == 0)
        {
            return;
        }

        // The direction at the middle of the turn of this frame, so turning while panning bends the same at any frame rate.
        var yaw = (_yaw - (turned / 2)) * Math.PI / 180;
        var scale = _data.PanSpeedMS * (_distance / _data.DistanceM.Start);
        Shift(((panRight * Math.Cos(yaw)) - (panForward * Math.Sin(yaw))) * scale, ((-panRight * Math.Sin(yaw)) - (panForward * Math.Cos(yaw))) * scale);
    }

    /// <summary>
    /// Follows a figure (its position is read every update) at the follow distance, or at
    /// <paramref name="distanceM"/> when given (the screenshot tool keeps the distance of its shot point).
    /// </summary>
    public void Follow(Func<(double X, double Z)> position, double? distanceM = null)
    {
        ArgumentNullException.ThrowIfNull(position);
        _follow = position;
        _moveRight = _moveForward = 0;
        _wantDistance = Math.Clamp(distanceM ?? _data.FollowDistanceM, _data.DistanceM.Min, _data.DistanceM.Max);
    }

    /// <summary>Leaves follow mode; the camera stays where it is.</summary>
    public void StopFollowing() => _follow = null;

    /// <summary>
    /// Back to the start view (the reset key): start turn (the short way round), start pitch and distance
    /// over the centre of the area, eased; follow mode and key moves end.
    /// </summary>
    public void Reset()
    {
        _follow = null;
        _moveRight = _moveForward = _turn = 0;
        var turn = (_data.StartYawDeg - _yaw) % 360;
        turn = turn > 180 ? turn - 360 : turn < -180 ? turn + 360 : turn;
        _wantYaw = _yaw + turn;
        _wantPitch = ClampPitch(_data.PitchDeg);
        _wantDistance = _data.DistanceM.Start;
        (_wantX, _wantZ) = _centre;
        Clamp();
    }

    /// <summary>
    /// Jumps to a fixed view without easing (screenshot tool, R32); the pitch goes back to its start value
    /// unless the shot names one (outside the player's range, so a shot can show the sky).
    /// </summary>
    public void Set(double targetX, double targetZ, double yawDeg, double distance, double? pitchDeg = null)
    {
        _follow = null;
        _moveRight = _moveForward = _turn = 0;
        _targetX = _wantX = targetX;
        _targetZ = _wantZ = targetZ;
        _yaw = _wantYaw = yawDeg;
        _pitch = _wantPitch = pitchDeg ?? ClampPitch(_data.PitchDeg);
        _distance = _wantDistance = Math.Clamp(distance, _data.DistanceM.Min, _data.DistanceM.Max);
        Clamp();
        (_targetX, _targetZ) = (_wantX, _wantZ);
        _height = _ground(_targetX, _targetZ);
    }

    /// <summary>Eases towards the goals; <paramref name="seconds"/> is the real frame time.</summary>
    public void Update(double seconds)
    {
        if (_follow is not null)
        {
            (_wantX, _wantZ) = _follow();
            Clamp();
        }

        // Smoothing is given per 60 Hz frame; the same feel at any frame rate.
        var t = 1 - Math.Pow(1 - _data.Smoothing, Math.Max(0, seconds) * 60);
        _yaw += (_wantYaw - _yaw) * t;
        _pitch += (_wantPitch - _pitch) * t;
        _distance += (_wantDistance - _distance) * t;
        _targetX += (_wantX - _targetX) * t;
        _targetZ += (_wantZ - _targetZ) * t;

        // The height eases towards the ground under the target, so a step in the ground (a cell edge, a
        // cliff) becomes a glide.
        _height += (_ground(_targetX, _targetZ) - _height) * t;
    }

    /// <summary>
    /// The ray through a screen point for the current view (perspective from the eye, orthographic
    /// parallel to the view direction); the direction has length 1.
    /// </summary>
    public (Point3 Origin, Point3 Direction) Ray(double ndcX, double ndcY, double aspect)
    {
        var eye = Eye;
        var forward = Normal(_targetX - eye.X, _height - eye.Y, _targetZ - eye.Z);

        // Right = forward x up, up' = right x forward (as a look-at with the world up).
        var right = Normal(-forward.Z, 0, forward.X);
        var up = new Point3((right.Y * forward.Z) - (right.Z * forward.Y), (right.Z * forward.X) - (right.X * forward.Z), (right.X * forward.Y) - (right.Y * forward.X));
        if (Orthographic)
        {
            var half = OrthoSize / 2;
            var sx = ndcX * half * aspect;
            var sy = ndcY * half;
            return (new Point3(eye.X + (right.X * sx) + (up.X * sy), eye.Y + (right.Y * sx) + (up.Y * sy), eye.Z + (right.Z * sx) + (up.Z * sy)), forward);
        }

        var tan = Math.Tan(_data.FovDeg * Math.PI / 360);
        var px = ndcX * tan * aspect;
        var py = ndcY * tan;
        return (eye, Normal(forward.X + (right.X * px) + (up.X * py), forward.Y + (right.Y * px) + (up.Y * py), forward.Z + (right.Z * px) + (up.Z * py)));
    }

    /// <summary>
    /// The point under a screen point on the ground plane at the target's height (the plane pan and zoom
    /// hold on to), or null if the ray looks above the horizon or meets the plane beyond the far distance.
    /// </summary>
    public Point3? GroundAt(double ndcX, double ndcY, double aspect)
    {
        var (origin, direction) = Ray(ndcX, ndcY, aspect);
        if (direction.Y > -1e-6)
        {
            return null;
        }

        var t = (_height - origin.Y) / direction.Y;
        return t < 0 || t > _data.FarM ? null : new Point3(origin.X + (direction.X * t), _height, origin.Z + (direction.Z * t));
    }

    /// <summary>
    /// Eases a key speed (share of the top speed) towards <paramref name="held"/> over <paramref name="dt"/>
    /// and returns the way travelled (share x seconds), both exact for an exponential approach.
    /// </summary>
    private double Ease(ref double speed, double held, double dt)
    {
        if (speed == 0 && held == 0)
        {
            return 0;
        }

        var tau = held == 0 ? _data.KeyEaseOutS : _data.KeyEaseInS;
        var keep = Math.Exp(-dt / tau);
        var way = (held * dt) + ((speed - held) * tau * (1 - keep));
        speed = held + ((speed - held) * keep);

        // Below a thousandth of the top speed the glide has ended.
        if (held == 0 && Math.Abs(speed) < 1e-3)
        {
            speed = 0;
        }

        return way;
    }

    private void Shift(double dx, double dz)
    {
        _wantX += dx;
        _wantZ += dz;
        _targetX += dx;
        _targetZ += dz;
        Clamp();
        _targetX = Math.Clamp(_targetX, _bounds.MinX, _bounds.MaxX);
        _targetZ = Math.Clamp(_targetZ, _bounds.MinZ, _bounds.MaxZ);
    }

    private double ZoomedDistance(double steps) =>
        Math.Clamp(_wantDistance * Math.Pow(1 - _data.ZoomStep, steps), _data.DistanceM.Min, _data.DistanceM.Max);

    private double ClampPitch(double pitch) => Math.Clamp(pitch, _data.PitchRangeDeg.Min, _data.PitchRangeDeg.Max);

    private static Point3 Normal(double x, double y, double z)
    {
        var length = Math.Sqrt((x * x) + (y * y) + (z * z));
        return length > 0 ? new Point3(x / length, y / length, z / length) : new Point3(0, -1, 0);
    }

    private void Clamp()
    {
        _wantX = Math.Clamp(_wantX, _bounds.MinX, _bounds.MaxX);
        _wantZ = Math.Clamp(_wantZ, _bounds.MinZ, _bounds.MaxZ);
    }
}
