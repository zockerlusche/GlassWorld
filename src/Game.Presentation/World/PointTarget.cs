using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.World;

/// <summary>
/// What the player points at with a click while writing (A1617, B2678): the nearest thing to the clicked ground
/// point within a radius from the data (UI token <c>window.point_radius_m</c>). Without Godot, so it can be tested.
/// </summary>
public static class PointTarget
{
    /// <summary>The nearest object within <paramref name="radiusM"/> of (<paramref name="x"/>, <paramref name="z"/>), ties by id; null if none.</summary>
    public static WorldObject? Nearest(IEnumerable<WorldObject> objects, double x, double z, double radiusM)
    {
        ArgumentNullException.ThrowIfNull(objects);
        WorldObject? best = null;
        var bestDistance = double.MaxValue;
        foreach (var o in objects.OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            var distance = Math.Sqrt(((o.Position.X - x) * (o.Position.X - x)) + ((o.Position.Z - z) * (o.Position.Z - z)));
            if (distance <= radiusM && distance < bestDistance)
            {
                (best, bestDistance) = (o, distance);
            }
        }

        return best;
    }
}
