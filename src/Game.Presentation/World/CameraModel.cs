using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>A point in metres on the ground plane and a height.</summary>
public readonly record struct Point3(double X, double Y, double Z);

/// <summary>
/// The island camera without Godot (B2 R26): an iso view that orbits a ground target, rotates by dragging,
/// zooms in steps, pans in its own frame and can follow a figure. All limits, speeds and the smoothing
/// come from <c>content/look/camera.json</c>; every move eases towards its goal, also the height of the
/// ground under the target, so the view glides over slopes and cliffs instead of jumping. Presentation
/// only: the camera never changes the world.
/// </summary>
public sealed class CameraModel
{
    private readonly LookCamera _data;
    private readonly (double MinX, double MinZ, double MaxX, double MaxZ) _bounds;
    private double _yaw, _distance, _targetX, _targetZ;
    private double _wantYaw, _wantDistance, _wantX, _wantZ;
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
        _yaw = _wantYaw = data.StartYawDeg;
        _distance = _wantDistance = data.DistanceM.Start;
        _targetX = _wantX = (minX + maxX) / 2;
        _targetZ = _wantZ = (minZ + maxZ) / 2;
        _height = _ground(_targetX, _targetZ);
    }

    /// <summary>Orthographic or perspective (data value, picture question T1; the screenshot tool may override it).</summary>
    public bool Orthographic => OrthographicOverride ?? _data.Projection == LookCameraProjection.Orthographic;

    /// <summary>Projection set by the screenshot tool (null = data value).</summary>
    public bool? OrthographicOverride { get; set; }

    /// <summary>Field of view in degrees (perspective).</summary>
    public double FovDeg => _data.FovDeg;

    /// <summary>Visible height in metres at the target (orthographic size; same framing as perspective).</summary>
    public double OrthoSize => 2 * _distance * Math.Tan(_data.FovDeg * Math.PI / 360);

    /// <summary>Current yaw in degrees.</summary>
    public double Yaw => _yaw;

    /// <summary>Current distance in metres.</summary>
    public double Distance => _distance;

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

    /// <summary>Eye position for the current state.</summary>
    public Point3 Eye
    {
        get
        {
            var pitch = _data.PitchDeg * Math.PI / 180;
            var yaw = _yaw * Math.PI / 180;
            var flat = _distance * Math.Cos(pitch);
            return new Point3(_targetX + (flat * Math.Sin(yaw)), _height + (_distance * Math.Sin(pitch)), _targetZ + (flat * Math.Cos(yaw)));
        }
    }

    /// <summary>Rotates by a horizontal drag in pixels.</summary>
    public void Drag(double pixels) => _wantYaw += pixels * _data.RotateDegPerPx;

    /// <summary>Zooms by mouse wheel steps (positive = closer), within the distance range.</summary>
    public void Zoom(double steps) =>
        _wantDistance = Math.Clamp(_wantDistance * Math.Pow(1 - _data.ZoomStep, steps), _data.DistanceM.Min, _data.DistanceM.Max);

    /// <summary>
    /// Pans in the camera's own frame (right, forward in -1..1) for <paramref name="seconds"/>; panning
    /// leaves follow mode. Faster when zoomed out, so the screen speed stays about the same.
    /// </summary>
    public void Pan(double right, double forward, double seconds)
    {
        if (right == 0 && forward == 0)
        {
            return;
        }

        _follow = null;
        var yaw = _wantYaw * Math.PI / 180;
        var scale = _data.PanSpeedMS * seconds * (_wantDistance / _data.DistanceM.Start);
        _wantX += ((right * Math.Cos(yaw)) - (forward * Math.Sin(yaw))) * scale;
        _wantZ += ((-right * Math.Sin(yaw)) - (forward * Math.Cos(yaw))) * scale;
        Clamp();
    }

    /// <summary>
    /// Follows a figure (its position is read every update) at the follow distance, or at
    /// <paramref name="distanceM"/> when given (the screenshot tool keeps the distance of its shot point).
    /// </summary>
    public void Follow(Func<(double X, double Z)> position, double? distanceM = null)
    {
        ArgumentNullException.ThrowIfNull(position);
        _follow = position;
        _wantDistance = Math.Clamp(distanceM ?? _data.FollowDistanceM, _data.DistanceM.Min, _data.DistanceM.Max);
    }

    /// <summary>Leaves follow mode; the camera stays where it is.</summary>
    public void StopFollowing() => _follow = null;

    /// <summary>Jumps to a fixed view without easing (screenshot tool, R32).</summary>
    public void Set(double targetX, double targetZ, double yawDeg, double distance)
    {
        _follow = null;
        _targetX = _wantX = targetX;
        _targetZ = _wantZ = targetZ;
        _yaw = _wantYaw = yawDeg;
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
        _distance += (_wantDistance - _distance) * t;
        _targetX += (_wantX - _targetX) * t;
        _targetZ += (_wantZ - _targetZ) * t;

        // The height eases towards the ground under the target, so a step in the ground (a cell edge, a
        // cliff) becomes a glide.
        _height += (_ground(_targetX, _targetZ) - _height) * t;
    }

    private void Clamp()
    {
        _wantX = Math.Clamp(_wantX, _bounds.MinX, _bounds.MaxX);
        _wantZ = Math.Clamp(_wantZ, _bounds.MinZ, _bounds.MaxZ);
    }
}
