namespace GlassWorld.Presentation.World;

/// <summary>
/// The directions the ink outline pushes the corners of a flat-shaded mesh out along (TL #20750). The
/// outline is an inverted hull: a second copy of the mesh, drawn inside out and a little larger. Pushed
/// out along its own normals, a flat-shaded mesh falls apart there - every face has its own corners with
/// its own normal, so each face moves off by itself and the outline shows as loose strokes that float
/// above the body (the rock niche and the small stones of the survey D704). Here all corners that share
/// a place get one common direction, so the hull stays one closed skin that sits on the body.
/// </summary>
public static class OutlineHull
{
    /// <summary>Corners closer than this (metres, per axis) count as the same place.</summary>
    public const double WeldM = 1e-4;

    /// <summary>
    /// One direction per corner: the mean of the different face normals that meet at its place, made
    /// longer where they spread (a mitre), so every face of the hull lies about one outline width off its
    /// face of the body, at most <paramref name="mitreLimit"/> long. A corner whose normals cancel (the
    /// two sides of a leaf) keeps its own normal.
    /// </summary>
    /// <param name="positions">Corner positions, one per mesh vertex.</param>
    /// <param name="normals">The face normal of each corner (unit length).</param>
    /// <param name="mitreLimit">Longest direction in outline widths (<c>outline_mitre_limit</c> of <c>content/look/objects.json</c>): a sharp spike is not pushed out further; at least 1.</param>
    public static Point3[] Directions(IReadOnlyList<Point3> positions, IReadOnlyList<Point3> normals, double mitreLimit)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(normals);
        ArgumentOutOfRangeException.ThrowIfLessThan(mitreLimit, 1);
        if (positions.Count != normals.Count)
        {
            throw new ArgumentException("one normal per position expected", nameof(normals));
        }

        static (long X, long Y, long Z) Key(Point3 p, double step) => ((long)Math.Round(p.X / step), (long)Math.Round(p.Y / step), (long)Math.Round(p.Z / step));
        var meeting = new Dictionary<(long X, long Y, long Z), List<Point3>>();
        for (var i = 0; i < positions.Count; i++)
        {
            var key = Key(positions[i], WeldM);
            if (!meeting.TryGetValue(key, out var list))
            {
                meeting[key] = list = [];
            }

            // The two triangles of a flat facet bring the same normal twice: it counts once.
            var normal = Key(normals[i], 1e-3);
            if (!list.Exists(n => Key(n, 1e-3) == normal))
            {
                list.Add(normals[i]);
            }
        }

        var result = new Point3[positions.Count];
        for (var i = 0; i < positions.Count; i++)
        {
            var list = meeting[Key(positions[i], WeldM)];
            double x = 0, y = 0, z = 0;
            foreach (var n in list)
            {
                (x, y, z) = (x + n.X, y + n.Y, z + n.Z);
            }

            var length = Math.Sqrt((x * x) + (y * y) + (z * z));
            if (length < 1e-6)
            {
                result[i] = normals[i];
                continue;
            }

            (x, y, z) = (x / length, y / length, z / length);

            // The mitre: the face that looks furthest away from the mean still moves one width.
            var least = 1.0;
            foreach (var n in list)
            {
                least = Math.Min(least, (x * n.X) + (y * n.Y) + (z * n.Z));
            }

            var scale = Math.Min(mitreLimit, 1 / Math.Max(least, 1e-6));
            result[i] = new Point3(x * scale, y * scale, z * scale);
        }

        return result;
    }
}
