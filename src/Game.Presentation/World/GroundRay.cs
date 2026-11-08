namespace GlassWorld.Presentation.World;

/// <summary>
/// Where a ray from the camera meets the island ground (gift placement, B6), without Godot: it steps
/// along the ray until the ray is below the ground, then bisects that step. The ground is the drawn mesh
/// height (<see cref="TerrainMesher.GroundHeight"/>), so the point lies on what the player sees.
/// </summary>
public static class GroundRay
{
    /// <summary>
    /// First crossing of the ray (<paramref name="origin"/>, <paramref name="direction"/>) with the ground,
    /// or null if the ray stays above it within <paramref name="maxDistance"/> metres. The returned point
    /// has the ground height at its (x, z).
    /// </summary>
    public static Point3? Hit(Point3 origin, Point3 direction, Func<double, double, double> ground, double step, double maxDistance, int refineSteps)
    {
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        var length = Math.Sqrt((direction.X * direction.X) + (direction.Y * direction.Y) + (direction.Z * direction.Z));
        if (length < 1e-12)
        {
            return null;
        }

        var d = new Point3(direction.X / length, direction.Y / length, direction.Z / length);
        Point3 At(double t) => new(origin.X + (d.X * t), origin.Y + (d.Y * t), origin.Z + (d.Z * t));
        double Above(double t)
        {
            var p = At(t);
            return p.Y - ground(p.X, p.Z);
        }

        if (Above(0) <= 0)
        {
            // The ray starts below the ground (camera inside a hill): the point right under it.
            return OnGround(origin.X, origin.Z, ground);
        }

        var previous = 0.0;
        for (var t = step; t <= maxDistance + (step / 2); t += step)
        {
            if (Above(t) > 0)
            {
                previous = t;
                continue;
            }

            var (low, high) = (previous, t);
            for (var i = 0; i < refineSteps; i++)
            {
                var mid = (low + high) / 2;
                (low, high) = Above(mid) > 0 ? (mid, high) : (low, mid);
            }

            var hit = At((low + high) / 2);
            return OnGround(hit.X, hit.Z, ground);
        }

        return null;
    }

    private static Point3 OnGround(double x, double z, Func<double, double, double> ground) => new(x, ground(x, z), z);
}
