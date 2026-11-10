using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>Which clip a figure plays while it lies down, lies or gets up.</summary>
public enum LieClip
{
    /// <summary>The transition from standing to sitting.</summary>
    Sit,

    /// <summary>The lying clip (a seated loop laid on its side).</summary>
    Lie,

    /// <summary>The transition from sitting to standing.</summary>
    Rise,
}

/// <summary>Where a figure is between standing and lying.</summary>
/// <param name="Sit">How far the hips have sunk to the ground (0 standing, 1 seated on the ground).</param>
/// <param name="Roll">How far it has rolled from sitting onto its side (0 seated, 1 lying), eased.</param>
/// <param name="Clip">What it plays.</param>
public readonly record struct LieStage(double Sit, double Roll, LieClip Clip);

/// <summary>
/// A sampled point of a lying body: its offset from the hips in metres in world axes, in the lying pose on
/// level ground, and how thick the body is around it.
/// </summary>
/// <param name="X">Offset x.</param>
/// <param name="Y">Offset up.</param>
/// <param name="Z">Offset z.</param>
/// <param name="RadiusM">The point stays at least this far above the ground.</param>
public readonly record struct BodyPoint(double X, double Y, double Z, double RadiusM);

/// <summary>How a lying body meets the ground.</summary>
/// <param name="Up">The body's up axis (unit length): the ground normal under it, within the data's tilt.</param>
/// <param name="HipY">Height of the hips in metres.</param>
public readonly record struct LyingFit(Point3 Up, double HipY);

/// <summary>
/// Lying down to sleep (W1695, W1729) without Godot, a placeholder until the figures have a lying clip: the
/// figure sits down with the library's sit transition, rolls from sitting onto its side about its hips (eased)
/// while the hips slide to the lying spot, and gets up the same way in reverse. The lying body is fitted to the
/// ground: tilted to the slope under its sampled points within the data's limit and lifted so that none of
/// them is below the ground. A figure on the ground never turns its body. Data:
/// <c>content/look/figures.json</c> (lying). The figure faces +Z at yaw 0, its left is +X.
/// </summary>
public static class LyingPose
{
    /// <summary>Whether the library clip <paramref name="clipId"/> is played lying.</summary>
    public static bool IsLying(FigureLying lying, string clipId)
    {
        ArgumentNullException.ThrowIfNull(lying);
        return lying.Clips.Contains(clipId, StringComparer.Ordinal);
    }

    /// <summary>Real seconds from standing to lying: sitting down, then the roll.</summary>
    public static double TotalS(FigureLying lying)
    {
        ArgumentNullException.ThrowIfNull(lying);
        return lying.SitS + lying.RollS;
    }

    /// <summary>The seconds lain down after <paramref name="delta"/> more: towards lying or back to standing.</summary>
    public static double Advance(FigureLying lying, double seconds, bool down, double delta) =>
        Math.Clamp(seconds + (down ? Math.Max(0, delta) : -Math.Max(0, delta)), 0, TotalS(lying));

    /// <summary>
    /// The stage <paramref name="seconds"/> after standing. Lying down (<paramref name="down"/>) it plays the
    /// sit transition, then the lying clip while it rolls; getting up it rolls back in the lying clip (a seated
    /// loop), then plays the rise transition. The hips sink within the data's share of the sitting time.
    /// </summary>
    public static LieStage Stage(FigureLying lying, double seconds, bool down)
    {
        ArgumentNullException.ThrowIfNull(lying);
        var t = Math.Clamp(seconds, 0, TotalS(lying));
        var window = Math.Max(1e-6, lying.SinkTo - lying.SinkFrom);
        var sit = Smooth(((t / lying.SitS) - lying.SinkFrom) / window);
        var roll = Smooth((t - lying.SitS) / lying.RollS);
        var clip = down
            ? (t < lying.SitS ? LieClip.Sit : LieClip.Lie)
            : (t > lying.SitS ? LieClip.Lie : LieClip.Rise);
        return new LieStage(sit, roll, clip);
    }

    /// <summary>The playback speed that fits a transition clip of <paramref name="clipLengthS"/> into the sitting time.</summary>
    public static double ClipSpeed(FigureLying lying, double clipLengthS)
    {
        ArgumentNullException.ThrowIfNull(lying);
        return clipLengthS > 0 ? clipLengthS / lying.SitS : 1;
    }

    /// <summary>
    /// Whether a figure may turn its whole body to a voice or to another inhabitant: only upright, never while
    /// it lies, lies down, gets up (<paramref name="lieSeconds"/> above 0) or sits (W1729).
    /// </summary>
    public static bool MayTurn(bool lying, double lieSeconds, bool seated, double seatShare) =>
        !lying && lieSeconds <= 0 && !seated && seatShare <= 0;

    /// <summary>The roll about the facing axis in radians at <paramref name="roll"/> (0 seated, 1 lying).</summary>
    public static double RollRad(FigureLying lying, double roll)
    {
        ArgumentNullException.ThrowIfNull(lying);
        return lying.RollDeg * Math.PI / 180 * Math.Clamp(roll, 0, 1);
    }

    /// <summary>
    /// The hips of the seated figure in its own frame (the pivot of the roll): up by the seated clip's seat
    /// height, back by <c>hip_back_m</c>, both × scale.
    /// </summary>
    public static (double Y, double Z) Pivot(FigureLying lying, FigureSitting sitting, double scale)
    {
        ArgumentNullException.ThrowIfNull(lying);
        ArgumentNullException.ThrowIfNull(sitting);
        return (sitting.ClipSeatHeightM * scale, -lying.HipBackM * scale);
    }

    /// <summary>
    /// How far the hips have slid along the figure's left (+X) at <paramref name="roll"/>: away from the side
    /// the head goes down to, <c>hip_slide_m</c> × scale when lying.
    /// </summary>
    public static double Slide(FigureLying lying, double scale, double roll)
    {
        ArgumentNullException.ThrowIfNull(lying);
        return Math.Sign(lying.RollDeg) * lying.HipSlideM * scale * Math.Clamp(roll, 0, 1);
    }

    /// <summary>
    /// How far the legs have left the seated pose at <paramref name="roll"/> (0 as seated, 1 as lying: thighs
    /// opened, knees bent): only after <c>legs_from</c> of the roll, while the body is nearly down, so they
    /// do not dip into the ground on the way.
    /// </summary>
    public static double Legs(FigureLying lying, double roll)
    {
        ArgumentNullException.ThrowIfNull(lying);
        return Smooth((Math.Clamp(roll, 0, 1) - lying.LegsFrom) / Math.Max(1e-6, 1 - lying.LegsFrom));
    }

    /// <summary>
    /// How far a body must be lifted so that none of its points is below the ground: sitting down, rolling
    /// and getting up never sink into it. The points are offsets from (<paramref name="x"/>,
    /// <paramref name="y"/>, <paramref name="z"/>) in world axes as the body is now; 0 when all are clear.
    /// </summary>
    public static double Lift(IReadOnlyList<BodyPoint> points, double x, double y, double z, Func<double, double, double> ground)
    {
        ArgumentNullException.ThrowIfNull(points);
        return points.Count == 0 ? 0 : Math.Max(0, -Clearance(new LyingFit(new Point3(0, 1, 0), y), points, x, z, ground));
    }

    /// <summary>
    /// Fits a lying body to the ground: the slope under its points (least squares over the ground heights
    /// there) tilts it, at most by <c>max_tilt_deg</c>; then the hips are as high as needed so that every
    /// tilted point is its radius above the ground under it. Without points (a figure not drawn yet) the hips
    /// lie <c>lift_m</c> × <paramref name="scale"/> above level ground.
    /// </summary>
    /// <param name="lying">The data.</param>
    /// <param name="points">The body's points about the hips, lying on level ground.</param>
    /// <param name="hipX">Where the hips lie (x).</param>
    /// <param name="hipZ">Where the hips lie (z).</param>
    /// <param name="scale">The figure's size factor.</param>
    /// <param name="ground">Ground height at (x, z).</param>
    public static LyingFit Fit(FigureLying lying, IReadOnlyList<BodyPoint> points, double hipX, double hipZ, double scale, Func<double, double, double> ground)
    {
        ArgumentNullException.ThrowIfNull(lying);
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(ground);
        var level = new Point3(0, 1, 0);
        if (points.Count == 0)
        {
            return new LyingFit(level, ground(hipX, hipZ) + (lying.LiftM * scale));
        }

        // The slope of the ground under the body: height = a·x + b·z + c over the points.
        double mx = 0, mz = 0, mg = 0;
        Span<double> heights = points.Count <= 32 ? stackalloc double[points.Count] : new double[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            heights[i] = ground(hipX + points[i].X, hipZ + points[i].Z);
            mx += points[i].X;
            mz += points[i].Z;
            mg += heights[i];
        }

        (mx, mz, mg) = (mx / points.Count, mz / points.Count, mg / points.Count);
        double sxx = 0, szz = 0, sxz = 0, sxg = 0, szg = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var (dx, dz, dg) = (points[i].X - mx, points[i].Z - mz, heights[i] - mg);
            sxx += dx * dx;
            szz += dz * dz;
            sxz += dx * dz;
            sxg += dx * dg;
            szg += dz * dg;
        }

        double a = 0, b = 0;
        var det = (sxx * szz) - (sxz * sxz);
        if (det > 1e-9 * Math.Max(1e-12, sxx * szz))
        {
            a = ((sxg * szz) - (szg * sxz)) / det;
            b = ((szg * sxx) - (sxg * sxz)) / det;
        }
        else if (sxx + szz > 1e-12)
        {
            // All points on one line: the slope along it.
            a = sxg / (sxx + szz);
            b = szg / (sxx + szz);
        }

        var steep = Math.Sqrt((a * a) + (b * b));
        var limit = Math.Tan(Math.Clamp(lying.MaxTiltDeg, 0, 89) * Math.PI / 180);
        if (steep > limit)
        {
            (a, b) = (a * limit / steep, b * limit / steep);
        }

        var length = Math.Sqrt((a * a) + 1 + (b * b));
        var up = new Point3(-a / length, 1 / length, -b / length);
        var hipY = double.NegativeInfinity;
        foreach (var point in points)
        {
            var p = Tilt(up, new Point3(point.X, point.Y, point.Z));
            hipY = Math.Max(hipY, ground(hipX + p.X, hipZ + p.Z) + point.RadiusM - p.Y);
        }

        return new LyingFit(up, hipY);
    }

    /// <summary>
    /// The smallest gap in metres between the fitted body and the ground: over all points, the point's
    /// underside (its radius below it) minus the ground under it. Never negative for a fit of the same points.
    /// </summary>
    public static double Clearance(LyingFit fit, IReadOnlyList<BodyPoint> points, double hipX, double hipZ, Func<double, double, double> ground)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(ground);
        var least = double.PositiveInfinity;
        foreach (var point in points)
        {
            var p = Tilt(fit.Up, new Point3(point.X, point.Y, point.Z));
            least = Math.Min(least, fit.HipY + p.Y - point.RadiusM - ground(hipX + p.X, hipZ + p.Z));
        }

        return least;
    }

    /// <summary>
    /// Turns <paramref name="v"/> by the shortest turn that brings straight up onto <paramref name="up"/>
    /// (unit length), as Godot's <c>Quaternion(Vector3.Up, up)</c> does.
    /// </summary>
    public static Point3 Tilt(Point3 up, Point3 v)
    {
        // Rodrigues about the axis up × n, with cos = n.Y; the axis has the length of the sine.
        var (kx, kz) = (up.Z, -up.X);
        var sin = Math.Sqrt((kx * kx) + (kz * kz));
        if (sin < 1e-12)
        {
            return v;
        }

        (kx, kz) = (kx / sin, kz / sin);
        var cos = up.Y;
        var dot = (kx * v.X) + (kz * v.Z);
        var (cx, cy, cz) = (-kz * v.Y, (kz * v.X) - (kx * v.Z), kx * v.Y);
        return new Point3(
            (v.X * cos) + (cx * sin) + (kx * dot * (1 - cos)),
            (v.Y * cos) + (cy * sin),
            (v.Z * cos) + (cz * sin) + (kz * dot * (1 - cos)));
    }

    private static double Smooth(double share)
    {
        var s = Math.Clamp(share, 0, 1);
        return s * s * (3 - (2 * s));
    }
}
