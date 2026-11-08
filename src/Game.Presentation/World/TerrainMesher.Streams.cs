using GlassWorld.Simulation.Map;

namespace GlassWorld.Presentation.World;

/// <summary>One point of a stream's centre line: position in metres and the core's water depth there (height units).</summary>
/// <param name="X">X in metres.</param>
/// <param name="Z">Z in metres.</param>
/// <param name="Depth">Water depth in height units.</param>
public readonly record struct StreamPoint(double X, double Z, double Depth);

/// <summary>
/// Streams as smooth bands (A1456): instead of one square plate per running cell, each stream is a ribbon
/// along its cell-centre line, following the core's flow directions (<see cref="FlowDirections"/>), with
/// corners rounded by Chaikin's corner cutting. Kept apart from the per-chunk surfaces so the detail
/// levels of the ground can change without touching it; lakes, swamp, banks and the sea stay as they are.
/// </summary>
public static partial class TerrainMesher
{
    /// <summary>
    /// The centre lines of all streams of the map, smoothed by the look's stream smoothing. A line starts at
    /// a running cell nothing runs into (longest first), follows the core's flow and ends where the water
    /// stops running (a lake, the sea: the line reaches into that cell) or meets a stream already drawn (it
    /// joins it there). The generator makes streams two cells wide: running cells beside a line belong to
    /// it, and the line runs between them. Deterministic.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<StreamPoint>> StreamLines(WorldMap map, TerrainLook look)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(look);
        int w = map.WidthCells, d = map.DepthCells;
        bool Running(int x, int z) => map.Contains(x, z) && map.Cell(x, z) is { Water: WaterKind.Fresh, Flow: >= 0 } c && c.Flow < FlowDirections.All.Count;
        (int X, int Z) Next(int x, int z)
        {
            var (dx, dz) = FlowDirections.All[map.Cell(x, z).Flow];
            return (x + dx, z + dz);
        }

        // Running cells something runs into.
        var fed = new bool[w * d];
        var running = new List<(int X, int Z)>();
        for (var z = 0; z < d; z++)
        {
            for (var x = 0; x < w; x++)
            {
                if (!Running(x, z))
                {
                    continue;
                }

                running.Add((x, z));
                var (nx, nz) = Next(x, z);
                if (Running(nx, nz))
                {
                    fed[(nz * w) + nx] = true;
                }
            }
        }

        int Length(int x, int z)
        {
            var seen = new HashSet<int>();
            while (Running(x, z) && seen.Add((z * w) + x))
            {
                (x, z) = Next(x, z);
            }

            return seen.Count;
        }

        // Heads first, longest first (the main stream before its side cells and tributaries), then
        // whatever is left (running cells in a loop).
        var starts = running.Where(c => !fed[(c.Z * w) + c.X]).Select(c => (Cell: c, Length: Length(c.X, c.Z)))
            .OrderByDescending(s => s.Length).ThenBy(s => s.Cell.Z).ThenBy(s => s.Cell.X).Select(s => s.Cell)
            .Concat(running).ToList();
        var taken = new bool[w * d];
        var lines = new List<IReadOnlyList<StreamPoint>>();
        var cell = look.CellM;
        StreamPoint Centre(int x, int z, double depth) => new((x + 0.5) * cell, (z + 0.5) * cell, depth);
        foreach (var (sx, sz) in starts)
        {
            if (taken[(sz * w) + sx])
            {
                continue;
            }

            var points = new List<StreamPoint>();
            int x = sx, z = sz;
            while (true)
            {
                var depth = map.Cell(x, z).WaterDepth;
                taken[(z * w) + x] = true;

                // The line runs between this cell and the running cells beside it that are not drawn yet.
                double px = x + 0.5, pz = z + 0.5;
                var count = 1;
                foreach (var (ox, oz) in (ReadOnlySpan<(int, int)>)[(1, 0), (-1, 0), (0, 1), (0, -1)])
                {
                    int bx = x + ox, bz = z + oz;
                    if (Running(bx, bz) && !taken[(bz * w) + bx] && Next(x, z) != (bx, bz))
                    {
                        taken[(bz * w) + bx] = true;
                        px += bx + 0.5;
                        pz += bz + 0.5;
                        count++;
                    }
                }

                points.Add(new StreamPoint(px / count * cell, pz / count * cell, depth));
                var (nx, nz) = Next(x, z);
                if (!map.Contains(nx, nz))
                {
                    break;
                }

                if (!Running(nx, nz) || taken[(nz * w) + nx])
                {
                    // Into still water, the sea or a stream drawn already: reach into that cell and end.
                    if (map.Cell(nx, nz).Water != WaterKind.None)
                    {
                        points.Add(Centre(nx, nz, depth));
                    }

                    break;
                }

                (x, z) = (nx, nz);
            }

            if (points.Count >= 2)
            {
                lines.Add(Chaikin(points, look.StreamSmoothing));
            }
        }

        return lines;
    }

    /// <summary>
    /// The stream bands of the whole map as one mesh: per line point a cross-section left, centre, right
    /// (stream width from the look). The centre lies at the drawn ground plus the core's water depth; an
    /// edge lies there too, but never higher than the ground under it plus the shore lift, so the band
    /// closes against its banks. Each face carries the direction the water runs.
    /// </summary>
    public static MeshData Streams(WorldMap map, TerrainLook look)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(look);
        var mesh = new MeshData();
        var half = look.StreamWidthM / 2;
        foreach (var line in StreamLines(map, look))
        {
            var sections = new (float X, float Y, float Z)[line.Count][];
            for (var i = 0; i < line.Count; i++)
            {
                // Across the line: perpendicular to the mean direction of the neighbouring segments.
                var before = line[Math.Max(0, i - 1)];
                var after = line[Math.Min(line.Count - 1, i + 1)];
                double tx = after.X - before.X, tz = after.Z - before.Z;
                var length = Math.Sqrt((tx * tx) + (tz * tz));
                (tx, tz) = length > 1e-9 ? (tx / length, tz / length) : (1, 0);
                var p = line[i];
                var surface = GroundHeight(map, look, p.X, p.Z) + (float)(p.Depth * look.UnitM);
                (float X, float Y, float Z) Edge(double side)
                {
                    double ex = p.X - (tz * half * side), ez = p.Z + (tx * half * side);
                    return ((float)ex, MathF.Min(surface, GroundHeight(map, look, ex, ez) + look.ShoreLiftM), (float)ez);
                }

                sections[i] = [Edge(1), ((float)p.X, surface, (float)p.Z), Edge(-1)];
            }

            var color = line[0].Depth <= look.SwampMaxUnits ? look.Swamp : look.Fresh;
            for (var i = 0; i + 1 < line.Count; i++)
            {
                double fx = line[i + 1].X - line[i].X, fz = line[i + 1].Z - line[i].Z;
                var length = Math.Sqrt((fx * fx) + (fz * fz));
                var flow = length > 1e-9 ? ((float)(fx / length), (float)(fz / length)) : default;
                var (a, b) = (sections[i], sections[i + 1]);
                for (var k = 0; k < 2; k++)
                {
                    Up(mesh, a[k], a[k + 1], b[k], color, flow);
                    Up(mesh, a[k + 1], b[k + 1], b[k], color, flow);
                }
            }
        }

        return mesh;
    }

    /// <summary>Chaikin's corner cutting, <paramref name="rounds"/> times; the ends stay where they are.</summary>
    public static IReadOnlyList<StreamPoint> Chaikin(IReadOnlyList<StreamPoint> points, int rounds)
    {
        ArgumentNullException.ThrowIfNull(points);
        var current = points.ToList();
        for (var r = 0; r < rounds && current.Count >= 3; r++)
        {
            var next = new List<StreamPoint>((current.Count * 2) + 1) { current[0] };
            for (var i = 0; i + 1 < current.Count; i++)
            {
                var (a, b) = (current[i], current[i + 1]);
                if (i > 0)
                {
                    next.Add(Mix(a, b, 0.25));
                }

                if (i + 2 < current.Count)
                {
                    next.Add(Mix(a, b, 0.75));
                }
            }

            next.Add(current[^1]);
            current = next;
        }

        return current;
    }

    private static StreamPoint Mix(StreamPoint a, StreamPoint b, double t) =>
        new(a.X + ((b.X - a.X) * t), a.Z + ((b.Z - a.Z) * t), a.Depth + ((b.Depth - a.Depth) * t));

    /// <summary>A triangle whose face looks up (+y), whatever order its corners come in.</summary>
    private static void Up(MeshData mesh, (float X, float Y, float Z) a, (float X, float Y, float Z) b, (float X, float Y, float Z) c, Rgb color, (float X, float Z) flow)
    {
        var upward = ((c.X - a.X) * (b.Z - a.Z)) - ((c.Z - a.Z) * (b.X - a.X));

        // Normal y of (b - a) x (c - a) is (b.z - a.z)(c.x - a.x) - (b.x - a.x)(c.z - a.z).
        if (upward >= 0)
        {
            mesh.Triangle(a, b, c, color, flow);
        }
        else
        {
            mesh.Triangle(a, c, b, color, flow);
        }
    }
}
