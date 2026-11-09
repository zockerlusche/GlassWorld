using GlassWorld.Contracts;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Presentation.World;

/// <summary>A spot the scene finder picked: camera target in metres and, if asked, the yaw that puts the hill behind it.</summary>
/// <param name="X">Target x in metres.</param>
/// <param name="Z">Target z in metres.</param>
/// <param name="YawDeg">Camera yaw in degrees, or null to keep the shot point's.</param>
/// <param name="StreamCells">Running stream cells within the radius.</param>
/// <param name="Relief">Height difference within the radius (height units).</param>
public readonly record struct SceneSpot(double X, double Z, double? YawDeg, int StreamCells, int Relief);

/// <summary>
/// Finds per seed the spot for comparison shots (TL #19722: the same scene type in every picture): near a
/// start position, on a running stream, with more running water, a spring and a hill within the view radius, as the shot point's
/// <see cref="ShotScene"/> asks. Of all fitting cells it takes the one with the most relief plus stream,
/// then the spring closest to the target; ties by position. Deterministic, Godot-free.
/// </summary>
public static class SceneFinder
{
    /// <summary>The best spot, or null if no cell near a start fits.</summary>
    public static SceneSpot? Find(ShotScene scene, GeneratedIsland island, double cellM)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(island);
        var map = island.Map;
        var springs = map.Hydrology?.Springs().ToList() ?? [];
        var r = scene.RadiusCells;
        var near = scene.MaxStartDistanceCells;
        (double Score, int Spring, int X, int Z, int Streams, int Relief, (int X, int Z) Top)? best = null;
        for (var z = r; z < map.DepthCells - r; z++)
        {
            for (var x = r; x < map.WidthCells - r; x++)
            {
                // The target is a stream cell, so the creek lies in the middle of the picture.
                if (map.Cell(x, z) is not { Water: WaterKind.Fresh, Flow: >= 0 } || !island.Starts.Any(s => Sq(s.X - x) + Sq(s.Z - z) <= near * near))
                {
                    continue;
                }

                var spring = springs.Select(s => Sq(s.X - x) + Sq(s.Z - z)).Where(d => d <= r * r).DefaultIfEmpty(-1).Min();
                if (scene.Spring && spring < 0)
                {
                    continue;
                }

                int streams = 0, low = int.MaxValue, high = int.MinValue;
                var top = (x, z);
                for (var dz = -r; dz <= r; dz++)
                {
                    for (var dx = -r; dx <= r; dx++)
                    {
                        if (Sq(dx) + Sq(dz) > r * r)
                        {
                            continue;
                        }

                        var c = map.Cell(x + dx, z + dz);
                        streams += c.Water == WaterKind.Fresh && c.Flow >= 0 ? 1 : 0;
                        if (c.Water == WaterKind.Salt)
                        {
                            continue;
                        }

                        low = Math.Min(low, c.Height);
                        if (c.Height > high)
                        {
                            (high, top) = (c.Height, (x + dx, z + dz));
                        }
                    }
                }

                var relief = high - low;
                if (streams < scene.MinStreamCells || relief < scene.MinRelief)
                {
                    continue;
                }

                var score = relief + (streams / 2.0);
                if (best is not { } b || score > b.Score || (score == b.Score && spring < b.Spring))
                {
                    best = (score, spring, x, z, streams, relief, top);
                }
            }
        }

        if (best is not { } found)
        {
            return null;
        }

        double? yaw = null;
        if (scene.FaceHill && found.Top != (found.X, found.Z))
        {
            // The camera stands at target + (sin yaw, cos yaw) * distance; the hill shall lie beyond the target.
            double hx = found.Top.X - found.X, hz = found.Top.Z - found.Z;
            yaw = Math.Atan2(-hx, -hz) * 180 / Math.PI;
        }

        return new SceneSpot((found.X + 0.5) * cellM, (found.Z + 0.5) * cellM, yaw, found.Streams, found.Relief);
    }

    private static int Sq(int v) => v * v;
}
