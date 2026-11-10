using GlassWorld.Simulation.Map;

namespace GlassWorld.Presentation.World;

/// <summary>One point of a stream's centre line: position in metres, the core's water depth (height units) and stream width (metres) there.</summary>
/// <param name="X">X in metres.</param>
/// <param name="Z">Z in metres.</param>
/// <param name="Depth">Water depth in height units.</param>
/// <param name="WidthM">Width of the stream in metres (the core's width by flow accumulation; the look's width for islands without it).</param>
public readonly record struct StreamPoint(double X, double Z, double Depth, double WidthM);

/// <summary>One stream as it is drawn: its smoothed centre line, whether it starts at a spring, and the cells it belongs to.</summary>
/// <param name="Points">The centre line from the head down the flow.</param>
/// <param name="Source">Whether the line starts at a source (a spring: nothing runs into its first cell and no lake lies behind it); such a line starts narrow (see the look's source taper).</param>
/// <param name="Cells">The cells of the stream: its running cells, the cell it ends in and, for an outflow, the lake cell it leaves.</param>
public sealed record StreamCourse(IReadOnlyList<StreamPoint> Points, bool Source, IReadOnlyList<(int X, int Z)> Cells);

/// <summary>The point where a stream band starts at a spring, and the unit direction it runs off in.</summary>
/// <param name="X">X in metres.</param>
/// <param name="Z">Z in metres.</param>
/// <param name="FlowX">X of the unit direction of the first stretch of the band.</param>
/// <param name="FlowZ">Z of the unit direction of the first stretch of the band.</param>
public readonly record struct StreamSource(double X, double Z, double FlowX, double FlowZ);

/// <summary>
/// Streams as smooth bands (A1456): instead of one square plate per running cell, each stream is a ribbon
/// along its cell-centre line, following the core's flow directions (<see cref="FlowDirections"/>). The
/// line is relaxed until it turns no more than the look allows per metre (W1724: no kinks in the middle of
/// a stream), never further from the stream's own cells than the look's tolerance, and then rounded by
/// Chaikin's corner cutting. Kept apart from the per-chunk surfaces so the detail levels of the ground can
/// change without touching it; lakes, swamp, banks and the sea stay as they are.
/// </summary>
public static partial class TerrainMesher
{
    /// <summary>The centre lines of all streams of the map (<see cref="StreamCourses"/>).</summary>
    public static IReadOnlyList<IReadOnlyList<StreamPoint>> StreamLines(WorldMap map, TerrainLook look) =>
        [.. StreamCourses(map, look).Select(c => c.Points)];

    /// <summary>
    /// Where water comes out of the ground into a stream band (for the spring look, W1724): the first point
    /// of every line that starts at a source, with the direction of its first metre; and for a spring of
    /// the core that a stream already runs past, the point of that band nearest to it with the band's
    /// direction there.
    /// </summary>
    public static IReadOnlyList<StreamSource> StreamSources(WorldMap map, TerrainLook look)
    {
        var courses = StreamCourses(map, look);
        var sources = new List<StreamSource>();
        void Add(IReadOnlyList<StreamPoint> line, int index)
        {
            // The direction over the next metre of the line (the last metre at its end).
            var from = index;
            var to = index;
            double Span() => Math.Sqrt(Math.Pow(line[to].X - line[from].X, 2) + Math.Pow(line[to].Z - line[from].Z, 2));
            while (to + 1 < line.Count && Span() < 1.0)
            {
                to++;
            }

            while (from > 0 && Span() < 1.0)
            {
                from--;
            }

            var length = Span();
            if (length > 1e-9)
            {
                sources.Add(new StreamSource(line[index].X, line[index].Z, (line[to].X - line[from].X) / length, (line[to].Z - line[from].Z) / length));
            }
        }

        foreach (var course in courses.Where(c => c.Source))
        {
            Add(course.Points, 0);
        }

        var near = 1.5 * look.CellM;
        foreach (var (sx, sz) in map.Hydrology?.Springs() ?? [])
        {
            double x = (sx + 0.5) * look.CellM, z = (sz + 0.5) * look.CellM;
            if (sources.Any(s => Math.Pow(s.X - x, 2) + Math.Pow(s.Z - z, 2) <= near * near))
            {
                continue;
            }

            (IReadOnlyList<StreamPoint> Line, int Index)? best = null;
            var least = near * near;
            foreach (var course in courses)
            {
                for (var i = 0; i < course.Points.Count; i++)
                {
                    var distance = Math.Pow(course.Points[i].X - x, 2) + Math.Pow(course.Points[i].Z - z, 2);
                    if (distance < least)
                    {
                        (best, least) = ((course.Points, i), distance);
                    }
                }
            }

            if (best is { } found)
            {
                Add(found.Line, found.Index);
            }
        }

        return sources;
    }

    /// <summary>
    /// All streams of the map as they are drawn. A line starts at a running cell nothing runs into (longest
    /// first), follows the core's flow and ends where the water stops running (a lake, the sea: the line
    /// reaches into that cell) or meets a stream already drawn (it joins it there). The generator makes
    /// streams two cells wide: running cells beside a line belong to it, and the line runs between them;
    /// where the flow steps into such a side cell the line goes on through it instead of breaking off. A
    /// head with a lake behind it is an outflow: its line starts in the lake at full width. Every other
    /// head is a source and starts narrow. The line is smoothed (<see cref="Relax"/>, then
    /// <see cref="Chaikin"/>, then <see cref="Settle"/>); its ends stay where they are. Deterministic.
    /// </summary>
    public static IReadOnlyList<StreamCourse> StreamCourses(WorldMap map, TerrainLook look)
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

        // The still fresh water a head runs out of: the lake cell that lies most squarely behind it.
        (int X, int Z)? LakeBehind(int x, int z)
        {
            var (fx, fz) = FlowDirections.All[map.Cell(x, z).Flow];
            (int X, int Z)? best = null;
            var most = 0;
            foreach (var (dx, dz) in FlowDirections.All)
            {
                int nx = x + dx, nz = z + dz;
                var behind = -((dx * fx) + (dz * fz));
                if (behind > most && map.Contains(nx, nz) && map.Cell(nx, nz) is { Water: WaterKind.Fresh, Flow: < 0 })
                {
                    (best, most) = ((nx, nz), behind);
                }
            }

            return best;
        }

        // Heads first, longest first (the main stream before its side cells and tributaries), then
        // whatever is left (running cells in a loop).
        var starts = running.Where(c => !fed[(c.Z * w) + c.X]).Select(c => (Cell: c, Length: Length(c.X, c.Z)))
            .OrderByDescending(s => s.Length).ThenBy(s => s.Cell.Z).ThenBy(s => s.Cell.X).Select(s => s.Cell)
            .Concat(running).ToList();
        var springs = map.Hydrology?.Springs().ToHashSet() ?? [];

        // Per cell the number of the line that drew it (0 = none yet).
        var owner = new int[w * d];
        var courses = new List<StreamCourse>();
        var cell = look.CellM;
        var number = 0;
        var beside = new (int X, int Z)[5];
        double Width(int x, int z) => map.Hydrology?.WidthCm(x, z) is > 0 and var cm ? cm / 100.0 : look.StreamWidthM;
        StreamPoint Centre(int x, int z, double depth, double width) => new((x + 0.5) * cell, (z + 0.5) * cell, depth, width);
        foreach (var (sx, sz) in starts)
        {
            if (owner[(sz * w) + sx] != 0)
            {
                continue;
            }

            number++;
            var points = new List<StreamPoint>();
            var cells = new List<(int X, int Z)>();
            var walked = new HashSet<int>();

            // Per point how far the relaxing may move it: less where the cells it stands for lie far from it.
            var shifts = new List<double>();
            var source = !fed[(sz * w) + sx];
            if (source && !springs.Contains((sx, sz)) && LakeBehind(sx, sz) is { } lake)
            {
                // An outflow: the band starts in the lake, at full width.
                points.Add(Centre(lake.X, lake.Z, map.Cell(sx, sz).WaterDepth, Width(sx, sz)));
                shifts.Add(look.StreamMaxShiftM);
                cells.Add(lake);
                source = false;
            }

            int x = sx, z = sz;
            while (true)
            {
                var depth = map.Cell(x, z).WaterDepth;
                var width = Width(x, z);
                if (owner[(z * w) + x] == 0)
                {
                    cells.Add((x, z));
                }

                owner[(z * w) + x] = number;
                walked.Add((z * w) + x);

                // The line runs between this cell and the running cells beside it that are not drawn yet.
                double px = x + 0.5, pz = z + 0.5;
                var count = 1;
                beside[0] = (x, z);
                foreach (var (ox, oz) in (ReadOnlySpan<(int, int)>)[(1, 0), (-1, 0), (0, 1), (0, -1)])
                {
                    int bx = x + ox, bz = z + oz;
                    if (Running(bx, bz) && owner[(bz * w) + bx] == 0 && Next(x, z) != (bx, bz))
                    {
                        owner[(bz * w) + bx] = number;
                        cells.Add((bx, bz));
                        beside[count] = (bx, bz);
                        px += bx + 0.5;
                        pz += bz + 0.5;
                        count++;
                    }
                }

                (px, pz) = (px / count, pz / count);
                var far = 0.0;
                foreach (var (bx, bz) in beside.AsSpan(0, count))
                {
                    far = Math.Max(far, Math.Sqrt(Math.Pow(bx + 0.5 - px, 2) + Math.Pow(bz + 0.5 - pz, 2)) * cell);
                }

                points.Add(new StreamPoint(px * cell, pz * cell, depth, width));
                shifts.Add(Math.Clamp(look.StreamCoverM - far, 0, look.StreamMaxShiftM));
                var (nx, nz) = Next(x, z);
                if (!map.Contains(nx, nz))
                {
                    break;
                }

                // The flow may step into a cell beside this very line (the stream is two cells wide and its
                // flow wanders between them): the line goes on through it. Only a cell it walked already
                // (a loop) or a cell of another line ends it.
                var taken = owner[(nz * w) + nx];
                if (!Running(nx, nz) || (taken != 0 && (taken != number || walked.Contains((nz * w) + nx))))
                {
                    // Into still water, the sea or a stream drawn already: reach into that cell and end.
                    if (map.Cell(nx, nz).Water != WaterKind.None)
                    {
                        points.Add(Centre(nx, nz, depth, width));
                        shifts.Add(look.StreamMaxShiftM);
                        cells.Add((nx, nz));
                    }

                    break;
                }

                (x, z) = (nx, nz);
            }

            if (points.Count >= 2)
            {
                var smooth = Settle(Chaikin(Relax(points, cells, look, shifts), look.StreamSmoothing), cells, look);
                courses.Add(new StreamCourse(Taper(smooth, source ? look.StreamSourceTaperM : 0, look.StreamSourceWidthShare), source, cells));
            }
        }

        return courses;
    }

    /// <summary>
    /// The stream bands of the whole map as one mesh: per line point a cross-section left, centre, right
    /// (the core's stream width there, TL #19755). The centre lies at the drawn ground plus the core's water
    /// depth - in the trough of the cut bed, below the banks; an edge lies there too, but never higher than
    /// the ground under it plus the shore lift, so the band closes against its banks. Each face carries the
    /// direction the water runs; each vertex carries its place on the band (<see cref="MeshData.Band"/>:
    /// metres along the line, -1..1 across), so the shader can draw flow lines that run on without a seam
    /// from face to face.
    /// </summary>
    public static MeshData Streams(WorldMap map, TerrainLook look)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(look);
        var mesh = new MeshData();
        foreach (var line in StreamLines(map, look))
        {
            var sections = new (float X, float Y, float Z)[line.Count][];
            var along = new float[line.Count];
            for (var i = 0; i < line.Count; i++)
            {
                if (i > 0)
                {
                    along[i] = along[i - 1] + (float)Math.Sqrt(Math.Pow(line[i].X - line[i - 1].X, 2) + Math.Pow(line[i].Z - line[i - 1].Z, 2));
                }

                // Across the line: perpendicular to the mean direction of the neighbouring segments.
                var before = line[Math.Max(0, i - 1)];
                var after = line[Math.Min(line.Count - 1, i + 1)];
                double tx = after.X - before.X, tz = after.Z - before.Z;
                var length = Math.Sqrt((tx * tx) + (tz * tz));
                (tx, tz) = length > 1e-9 ? (tx / length, tz / length) : (1, 0);
                var p = line[i];
                var half = p.WidthM / 2;
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
                    // Across the band: 1 at the first edge, 0 on the centre line, -1 at the other edge.
                    float near = 1 - k, far = -k;
                    Up(mesh, (a[k], (along[i], near)), (a[k + 1], (along[i], far)), (b[k], (along[i + 1], near)), color, flow);
                    Up(mesh, (a[k + 1], (along[i], far)), (b[k + 1], (along[i + 1], far)), (b[k], (along[i + 1], near)), color, flow);
                }
            }
        }

        return mesh;
    }

    /// <summary>
    /// A stream that starts at its source (nothing runs into its first cell) widens from the look's share
    /// of its width to the full width over <paramref name="lengthM"/> along the line, so it seeps out of the
    /// ground instead of starting with a blunt edge.
    /// </summary>
    private static IReadOnlyList<StreamPoint> Taper(IReadOnlyList<StreamPoint> line, double lengthM, double startShare)
    {
        if (lengthM <= 0)
        {
            return line;
        }

        var result = new List<StreamPoint>(line.Count);
        var along = 0.0;
        for (var i = 0; i < line.Count; i++)
        {
            if (i > 0)
            {
                along += Math.Sqrt(Math.Pow(line[i].X - line[i - 1].X, 2) + Math.Pow(line[i].Z - line[i - 1].Z, 2));
            }

            var share = startShare + ((1 - startShare) * Math.Min(1, along / lengthM));
            result.Add(line[i] with { WidthM = line[i].WidthM * share });
        }

        return result;
    }

    /// <summary>
    /// Smooths the cell-centre polyline of a stream (W1724): in each pass every point but the ends moves
    /// halfway to the middle of its neighbours (width and depth with it); a point that would leave the
    /// stream's <paramref name="cells"/> by more than the look's tolerance is held back at that distance,
    /// and no point moves further from where the cells put it than its share of <paramref name="shifts"/>
    /// (the look's largest shift without them; the streams give less where a point stands for cells far
    /// from it, so every cell of the stream keeps its band). The passes stop as soon as the line turns no more than the
    /// look's goal per metre (<see cref="MaxTurnDegPerM"/>), at the latest after the look's number of
    /// passes; if the goal is out of reach (a hairpin the cells do not let go of) the smoothest pass is
    /// kept, since ever more passes only pull the line taut around the corners of its cells. The ends
    /// never move.
    /// </summary>
    public static IReadOnlyList<StreamPoint> Relax(IReadOnlyList<StreamPoint> points, IReadOnlyCollection<(int X, int Z)> cells, TerrainLook look, IReadOnlyList<double>? shifts = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(look);
        if (look.StreamRelaxPasses <= 0 || points.Count < 3)
        {
            return points;
        }

        var own = cells as IReadOnlySet<(int X, int Z)> ?? cells.ToHashSet();
        var limit = look.StreamTurnGoalDegPerM;
        var anchors = points.ToArray();
        var current = anchors;
        var (best, least) = (current, limit > 0 ? MaxTurnDegPerM(current) : double.MaxValue);
        for (var pass = 0; pass < look.StreamRelaxPasses && (limit <= 0 || least > limit); pass++)
        {
            current = Pass(current, own, look, anchors, shifts);
            if (limit <= 0)
            {
                best = current;
            }
            else if (MaxTurnDegPerM(current) is var turn && turn < least)
            {
                (best, least) = (current, turn);
            }
        }

        return best;
    }

    /// <summary>
    /// After the corner cutting: a few more passes over the dense line (eight per round of cutting), each
    /// holding every point within the look's tolerance of the stream's cells - cutting a corner may carry
    /// the line across the notch between two cells. So every point of the drawn line keeps the tolerance,
    /// and the places where it was held back are smooth. Nothing happens without relaxing passes.
    /// </summary>
    public static IReadOnlyList<StreamPoint> Settle(IReadOnlyList<StreamPoint> points, IReadOnlyCollection<(int X, int Z)> cells, TerrainLook look)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(look);
        if (look.StreamRelaxPasses <= 0 || points.Count < 3)
        {
            return points;
        }

        var own = cells as IReadOnlySet<(int X, int Z)> ?? cells.ToHashSet();
        var current = points.ToArray();
        for (var pass = 0; pass < 8 * Math.Max(1, look.StreamSmoothing); pass++)
        {
            current = Pass(current, own, look);
        }

        return current;
    }

    /// <summary>
    /// One smoothing pass: every point but the ends halfway to the middle of its neighbours, held within the
    /// tolerance of the cells and, with <paramref name="anchors"/>, within its shift of its anchor.
    /// </summary>
    private static StreamPoint[] Pass(StreamPoint[] current, IReadOnlySet<(int X, int Z)> cells, TerrainLook look, StreamPoint[]? anchors = null, IReadOnlyList<double>? shifts = null)
    {
        var next = new StreamPoint[current.Length];
        next[0] = current[0];
        next[^1] = current[^1];
        for (var i = 1; i + 1 < current.Length; i++)
        {
            var moved = Mix(current[i], Mix(current[i - 1], current[i + 1], 0.5), 0.5);
            var (x, z) = Held(moved.X, moved.Z, cells, look.CellM, look.StreamCellToleranceM) ?? (current[i].X, current[i].Z);
            if (anchors is not null)
            {
                double dx = x - anchors[i].X, dz = z - anchors[i].Z;
                var (shift, most) = (Math.Sqrt((dx * dx) + (dz * dz)), shifts?[i] ?? look.StreamMaxShiftM);
                if (shift > most)
                {
                    (x, z) = (anchors[i].X + (dx * most / shift), anchors[i].Z + (dz * most / shift));
                }
            }

            next[i] = moved with { X = x, Z = z };
        }

        return next;
    }

    /// <summary>
    /// How far a point lies outside the cells (metres; 0 inside one), looking at the cells around it up to
    /// <paramref name="reachCells"/> away; infinity if none of them is that near.
    /// </summary>
    public static double DistanceToCells(double x, double z, IReadOnlySet<(int X, int Z)> cells, double cellM, int reachCells = 2) =>
        Nearest(x, z, cells, cellM, reachCells) is { } n ? Math.Sqrt(Math.Pow(x - n.X, 2) + Math.Pow(z - n.Z, 2)) : double.PositiveInfinity;

    /// <summary>
    /// The largest turn of a line in degrees per metre: at every eighth of a metre along the line that has
    /// a metre of line on either side, the angle between the chord over the metre before it and the chord
    /// over the metre after it. A straight line gives 0, a bend of radius r about 57 / r, a sharp corner
    /// its full angle. A line shorter than two metres has no such place and gives 0.
    /// </summary>
    public static double MaxTurnDegPerM(IReadOnlyList<StreamPoint> line)
    {
        ArgumentNullException.ThrowIfNull(line);
        const double Chord = 1.0, Step = 0.125;
        var along = new double[line.Count];
        for (var i = 1; i < line.Count; i++)
        {
            along[i] = along[i - 1] + Math.Sqrt(Math.Pow(line[i].X - line[i - 1].X, 2) + Math.Pow(line[i].Z - line[i - 1].Z, 2));
        }

        var most = 0.0;
        var total = line.Count > 0 ? along[^1] : 0;
        for (var k = 0; Chord + (k * Step) <= total - Chord; k++)
        {
            var at = Chord + (k * Step);
            var (before, here, after) = (PointAt(line, along, at - Chord), PointAt(line, along, at), PointAt(line, along, at + Chord));
            double ax = here.X - before.X, az = here.Z - before.Z, bx = after.X - here.X, bz = after.Z - here.Z;
            var turn = Math.Abs(Math.Atan2((ax * bz) - (az * bx), (ax * bx) + (az * bz))) * 180 / Math.PI;
            most = Math.Max(most, turn / Chord);
        }

        return most;
    }

    /// <summary>The point of a line at a distance along it from its start (clamped to its ends); <paramref name="along"/> holds the length of the line up to each of its points.</summary>
    private static StreamPoint PointAt(IReadOnlyList<StreamPoint> line, double[] along, double distance)
    {
        if (distance <= 0)
        {
            return line[0];
        }

        // The first point at or beyond the distance (the lengths rise along the line).
        int low = 0, high = line.Count - 1;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (along[middle] < distance)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (along[low] < distance || low == 0)
        {
            return line[low];
        }

        var step = along[low] - along[low - 1];
        return step > 1e-12 ? Mix(line[low - 1], line[low], (distance - along[low - 1]) / step) : line[low];
    }

    /// <summary>The point of the cells nearest to (x, z), among the cells up to <paramref name="reach"/> cells around it.</summary>
    private static (double X, double Z)? Nearest(double x, double z, IReadOnlySet<(int X, int Z)> cells, double cellM, int reach)
    {
        int cx = (int)Math.Floor(x / cellM), cz = (int)Math.Floor(z / cellM);
        if (cells.Contains((cx, cz)))
        {
            return (x, z);
        }

        (double X, double Z)? best = null;
        var least = double.MaxValue;
        for (var dz = -reach; dz <= reach; dz++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                if (!cells.Contains((cx + dx, cz + dz)))
                {
                    continue;
                }

                double nx = Math.Clamp(x, (cx + dx) * cellM, (cx + dx + 1) * cellM), nz = Math.Clamp(z, (cz + dz) * cellM, (cz + dz + 1) * cellM);
                var distance = ((x - nx) * (x - nx)) + ((z - nz) * (z - nz));
                if (distance < least)
                {
                    (best, least) = ((nx, nz), distance);
                }
            }
        }

        return best;
    }

    /// <summary>The point itself if it lies within the tolerance of the cells, else the point at that distance from them on the way to it; null if no cell is near.</summary>
    private static (double X, double Z)? Held(double x, double z, IReadOnlySet<(int X, int Z)> cells, double cellM, double toleranceM)
    {
        if (Nearest(x, z, cells, cellM, 2) is not { } near)
        {
            return null;
        }

        double dx = x - near.X, dz = z - near.Z;
        var distance = Math.Sqrt((dx * dx) + (dz * dz));
        return distance <= toleranceM ? (x, z) : (near.X + (dx * toleranceM / distance), near.Z + (dz * toleranceM / distance));
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
        new(a.X + ((b.X - a.X) * t), a.Z + ((b.Z - a.Z) * t), a.Depth + ((b.Depth - a.Depth) * t), a.WidthM + ((b.WidthM - a.WidthM) * t));

    /// <summary>A triangle of a stream band whose face looks up (+y), whatever order its corners come in; each corner with its place on the band (metres along, -1..1 across).</summary>
    private static void Up(
        MeshData mesh,
        ((float X, float Y, float Z) P, (float Along, float Across) Band) a,
        ((float X, float Y, float Z) P, (float Along, float Across) Band) b,
        ((float X, float Y, float Z) P, (float Along, float Across) Band) c,
        Rgb color,
        (float X, float Z) flow)
    {
        var upward = ((c.P.X - a.P.X) * (b.P.Z - a.P.Z)) - ((c.P.Z - a.P.Z) * (b.P.X - a.P.X));

        // Normal y of (b - a) x (c - a) is (b.z - a.z)(c.x - a.x) - (b.x - a.x)(c.z - a.z).
        if (upward < 0)
        {
            (b, c) = (c, b);
        }

        mesh.Triangle(a.P, b.P, c.P, color, flow);
        mesh.Band.AddRange([a.Band.Along, a.Band.Across, b.Band.Along, b.Band.Across, c.Band.Along, c.Band.Across]);
    }
}
