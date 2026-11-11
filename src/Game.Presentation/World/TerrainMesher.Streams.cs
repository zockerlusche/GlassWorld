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
/// <param name="Cells">The cells of the stream: its running cells, the cell it ends in (at the sea also the sea cells it runs on over, see <see cref="StreamMouth"/>) and, for an outflow, the lake cell it leaves.</param>
/// <param name="Mouth">Where the stream ends in standing water, or null: it joins another stream, leaves the map, or the look has no mouth data.</param>
public sealed record StreamCourse(IReadOnlyList<StreamPoint> Points, bool Source, IReadOnlyList<(int X, int Z)> Cells, StreamMouth? Mouth = null);

/// <summary>One cross-section of a stream band: its two edges and its centre, and how far along the line it lies.</summary>
/// <param name="Left">The edge at band position 1 across.</param>
/// <param name="Centre">The point on the centre line.</param>
/// <param name="Right">The edge at band position -1 across.</param>
/// <param name="AlongM">Metres along the centre line from its head.</param>
public readonly record struct StreamSection((float X, float Y, float Z) Left, (float X, float Y, float Z) Centre, (float X, float Y, float Z) Right, float AlongM);

/// <summary>What a stream runs into at its end.</summary>
public enum StreamMouthKind
{
    /// <summary>The sea, whose surface follows the tide.</summary>
    Sea,

    /// <summary>Still fresh water: a lake, a pond, a swamp.</summary>
    Lake,
}

/// <summary>The end of a stream in standing water (TL #20750).</summary>
/// <param name="Kind">The sea or still fresh water.</param>
/// <param name="LevelM">Height of the receiving surface in metres; for the sea its mean level.</param>
/// <param name="LengthM">How much of the end of the centre line lies in the receiving water: from the edge of its first cell there to the end of the line, in metres along the cell centres.</param>
public sealed record StreamMouth(StreamMouthKind Kind, float LevelM, double LengthM);

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
    /// head is a source and starts narrow. With the look's mouth data (TL #20750) a line that reaches the
    /// sea runs on down the sea floor, cell by cell to the lowest neighbour, until the ground lies under
    /// the lowest tide (at most the look's number of cells), so at low water the stream crosses the beach
    /// the sea left dry; the course then says where it ends (<see cref="StreamCourse.Mouth"/>). The line is
    /// smoothed (<see cref="Relax"/>, then <see cref="Chaikin"/>, then <see cref="Settle"/>); its ends stay
    /// where they are. Deterministic.
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
            StreamMouth? mouth = null;
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
                    var into = map.Cell(nx, nz);
                    if (into.Water != WaterKind.None)
                    {
                        points.Add(Centre(nx, nz, depth, width));
                        shifts.Add(look.StreamMaxShiftM);
                        cells.Add((nx, nz));
                    }

                    if (into.Water != WaterKind.None && !Running(nx, nz) && look.Mouth is { } data)
                    {
                        // Standing water: half a cell of the line lies in it, from the cell's edge to its centre.
                        var inWater = cell / 2.0;
                        if (into.Water == WaterKind.Salt)
                        {
                            foreach (var (bx, bz) in DownTheSeaFloor(map, look, data, (nx, nz), (nx - x, nz - z)))
                            {
                                inWater += Math.Sqrt(Math.Pow(bx - cells[^1].X, 2) + Math.Pow(bz - cells[^1].Z, 2)) * cell;
                                points.Add(Centre(bx, bz, depth, width));
                                shifts.Add(look.StreamMaxShiftM);
                                cells.Add((bx, bz));
                            }
                        }

                        mouth = into.Water == WaterKind.Salt
                            ? new StreamMouth(StreamMouthKind.Sea, data.SeaLevelM(look.UnitM), inWater)
                            : new StreamMouth(StreamMouthKind.Lake, (into.Height + into.WaterDepth) * look.UnitM, inWater);
                    }

                    break;
                }

                (x, z) = (nx, nz);
            }

            if (points.Count >= 2)
            {
                var smooth = Settle(Chaikin(Relax(points, cells, look, shifts), look.StreamSmoothing), cells, look);
                courses.Add(new StreamCourse(Taper(smooth, source ? look.StreamSourceTaperM : 0, look.StreamSourceWidthShare), source, cells, mouth));
            }
        }

        return courses;
    }

    /// <summary>
    /// The sea cells a stream runs on over after its first one (TL #20750): from cell to the lowest
    /// neighbouring sea cell (among equals the one most ahead), as long as the drawn ground in the middle
    /// of the cell still lies above the lowest tide less the look's margin and the film of the band, and at
    /// most the look's number of cells. Over a flat it goes straight on (a cell as high as this one, ahead
    /// of it), never back and never twice over a cell. So the band ends on ground that every tide covers;
    /// where the sea floor neither falls nor runs on level ahead it ends there.
    /// </summary>
    private static List<(int X, int Z)> DownTheSeaFloor(WorldMap map, TerrainLook look, StreamMouthLook data, (int X, int Z) from, (int X, int Z) heading)
    {
        var path = new List<(int X, int Z)>();
        var under = data.SeaLevelM(look.UnitM, -1000) - data.UnderM - look.ShoreLiftM;
        var (x, z) = from;
        while (path.Count < data.ReachCells && GroundHeight(map, look, (x + 0.5) * look.CellM, (z + 0.5) * look.CellM) > under)
        {
            ((int X, int Z) Cell, (int X, int Z) Step)? best = null;
            var (lowest, mostAhead) = (int.MaxValue, int.MinValue);
            foreach (var (dx, dz) in FlowDirections.All)
            {
                int bx = x + dx, bz = z + dz;
                if (!map.Contains(bx, bz) || map.Cell(bx, bz) is not { Water: WaterKind.Salt } below || (bx, bz) == from || path.Contains((bx, bz)))
                {
                    continue;
                }

                // Down the floor, or level and ahead.
                var ahead = (dx * heading.X) + (dz * heading.Z);
                var here = map.Cell(x, z).Height;
                if ((below.Height < here || (below.Height == here && ahead > 0)) && (below.Height < lowest || (below.Height == lowest && ahead > mostAhead)))
                {
                    (best, lowest, mostAhead) = (((bx, bz), (dx, dz)), below.Height, ahead);
                }
            }

            if (best is not { } next)
            {
                break;
            }

            path.Add(next.Cell);
            ((x, z), heading) = next;
        }

        return path;
    }

    /// <summary>
    /// The stream bands of the whole map as one mesh: per line point a cross-section left, centre, right
    /// (the core's stream width there, TL #19755). The centre lies at the drawn ground plus the core's water
    /// depth - in the trough of the cut bed, below the banks; an edge lies there too, but never higher than
    /// the ground under it plus the shore lift, so the band closes against its banks. Each face carries the
    /// direction the water runs; each vertex carries its place on the band (<see cref="MeshData.Band"/>:
    /// metres along the line, -1..1 across), so the shader can draw flow lines that run on without a seam
    /// from face to face. Where a stream ends in standing water the band comes down to it
    /// (<see cref="AtMouth"/>, TL #20750) instead of ending as a block above it.
    /// </summary>
    public static MeshData Streams(WorldMap map, TerrainLook look)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(look);
        var mesh = new MeshData();
        foreach (var course in StreamCourses(map, look))
        {
            var line = course.Points;
            var sections = StreamSections(map, look, course);
            var color = line[0].Depth <= look.SwampMaxUnits ? look.Swamp : look.Fresh;
            for (var i = 0; i + 1 < line.Count; i++)
            {
                double fx = line[i + 1].X - line[i].X, fz = line[i + 1].Z - line[i].Z;
                var length = Math.Sqrt((fx * fx) + (fz * fz));
                var flow = length > 1e-9 ? ((float)(fx / length), (float)(fz / length)) : default;
                (float X, float Y, float Z)[] a = [sections[i].Left, sections[i].Centre, sections[i].Right];
                (float X, float Y, float Z)[] b = [sections[i + 1].Left, sections[i + 1].Centre, sections[i + 1].Right];
                var (from, to) = (sections[i].AlongM, sections[i + 1].AlongM);
                for (var k = 0; k < 2; k++)
                {
                    // Across the band: 1 at the first edge, 0 on the centre line, -1 at the other edge.
                    float near = 1 - k, far = -k;
                    Up(mesh, (a[k], (from, near)), (a[k + 1], (from, far)), (b[k], (to, near)), color, flow);
                    Up(mesh, (a[k + 1], (from, far)), (b[k + 1], (to, far)), (b[k], (to, near)), color, flow);
                }
            }
        }

        return mesh;
    }

    /// <summary>
    /// The cross-sections of one stream band, one per point of its centre line (see <see cref="Streams"/>):
    /// the centre at the height <see cref="AtMouth"/> gives over the drawn ground - at the sea settled so
    /// that the water never rises on its way (<see cref="RunDownToTheSea"/>) -, the edges half the width to
    /// either side, as high as the centre but never higher than the ground under them plus the shore lift.
    /// </summary>
    public static IReadOnlyList<StreamSection> StreamSections(WorldMap map, TerrainLook look, StreamCourse course)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(look);
        ArgumentNullException.ThrowIfNull(course);
        var line = course.Points;
        var along = new float[line.Count];
        for (var i = 1; i < line.Count; i++)
        {
            along[i] = along[i - 1] + (float)Math.Sqrt(Math.Pow(line[i].X - line[i - 1].X, 2) + Math.Pow(line[i].Z - line[i - 1].Z, 2));
        }

        var surfaces = new float[line.Count];
        var widens = new float[line.Count];
        var deepest = new float[line.Count];
        for (var i = 0; i < line.Count; i++)
        {
            var ground = GroundHeight(map, look, line[i].X, line[i].Z);
            var depth = (float)(line[i].Depth * look.UnitM);
            (surfaces[i], widens[i]) = AtMouth(look, course.Mouth, ground, depth, along[^1] - along[i]);
            deepest[i] = ground + depth;
        }

        if (course.Mouth is { Kind: StreamMouthKind.Sea } mouth && look.Mouth is { } data)
        {
            RunDownToTheSea(surfaces, deepest, along, mouth.LengthM, data.SeaLevelM(look.UnitM, 1000));
        }

        var sections = new StreamSection[line.Count];
        for (var i = 0; i < line.Count; i++)
        {
            // Across the line: perpendicular to the mean direction of the neighbouring segments.
            var before = line[Math.Max(0, i - 1)];
            var after = line[Math.Min(line.Count - 1, i + 1)];
            double tx = after.X - before.X, tz = after.Z - before.Z;
            var length = Math.Sqrt((tx * tx) + (tz * tz));
            (tx, tz) = length > 1e-9 ? (tx / length, tz / length) : (1, 0);
            var p = line[i];
            var surface = surfaces[i];
            var half = p.WidthM * widens[i] / 2;
            (float X, float Y, float Z) Edge(double side)
            {
                double ex = p.X - (tz * half * side), ez = p.Z + (tx * half * side);
                return ((float)ex, MathF.Min(surface, GroundHeight(map, look, ex, ez) + look.ShoreLiftM), (float)ez);
            }

            sections[i] = new StreamSection(Edge(1), ((float)p.X, surface, (float)p.Z), Edge(-1), along[i]);
        }

        return sections;
    }

    /// <summary>
    /// Settles the heights of a band's centre line towards the sea so that the water never rises on its way
    /// (review of #222, seed 42 of generator version 5: the ground along a line does not only fall - the
    /// cut bed of a stream lies lower than the beach it runs out onto, and the drawn ground has small
    /// rises between cells. A film that follows such ground dips under the sea and comes up again beyond
    /// its own waterline). Two passes over the heights <see cref="AtMouth"/> gave:
    /// <list type="number">
    /// <item>Before the coast (the last <paramref name="inWaterM"/> metres of the line lie beyond it) the
    /// water stands behind a rise of the ground further down as water does: level with the water there,
    /// but nowhere above <paramref name="deepest"/> (the ground plus the stream's depth, where it lay
    /// before). So a cut bed is filled up to the beach before it and the stream runs on without a break.</item>
    /// <item>From the first point the highest tide can reach (its water lies under
    /// <paramref name="highestSeaM"/>) no point lies higher than the one before it. Where the ground rises
    /// above the water that came down to it, the band passes under it instead of climbing it.</item>
    /// </list>
    /// So for every tide the centre line crosses the sea level once: what the sea has covered stays
    /// covered. Heights are only ever lowered below what <see cref="AtMouth"/> gave beyond the coast, so the
    /// film there still never lies above the ground plus the shore lift.
    /// </summary>
    /// <param name="surfaces">Heights of the centre line in metres from the head to the end; settled in place.</param>
    /// <param name="deepest">Per point the ground plus the stream's full depth.</param>
    /// <param name="along">Per point the metres along the line from its head.</param>
    /// <param name="inWaterM">How much of the end of the line lies in the sea (<see cref="StreamMouth.LengthM"/>).</param>
    /// <param name="highestSeaM">Height of the sea surface at the highest tide.</param>
    public static void RunDownToTheSea(float[] surfaces, IReadOnlyList<float> deepest, IReadOnlyList<float> along, double inWaterM, float highestSeaM)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(deepest);
        ArgumentNullException.ThrowIfNull(along);
        if (deepest.Count != surfaces.Length || along.Count != surfaces.Length)
        {
            throw new ArgumentException("one value per point expected", nameof(surfaces));
        }

        if (surfaces.Length == 0)
        {
            return;
        }

        // The first point beyond the coast; the last point of the line always is.
        var total = along[^1];
        var coast = surfaces.Length - 1;
        while (coast > 0 && total - along[coast - 1] <= inWaterM)
        {
            coast--;
        }

        // Far up the stream this changes nothing: there the water lies at its full depth already.
        for (var i = coast - 1; i >= 0; i--)
        {
            surfaces[i] = MathF.Min(deepest[i], MathF.Max(surfaces[i], surfaces[i + 1]));
        }

        var reached = Array.FindIndex(surfaces, y => y < highestSeaM);
        for (var i = reached + 1; reached >= 0 && i < surfaces.Length; i++)
        {
            surfaces[i] = MathF.Min(surfaces[i], surfaces[i - 1]);
        }
    }

    /// <summary>
    /// Height of a band's centre and the factor of its width at a point <paramref name="toEndM"/> metres
    /// before the end of its line, over the drawn <paramref name="ground"/> there (TL #20750, W1738).
    /// Away from a mouth, and without mouth data, the centre lies at the ground plus the water depth.
    /// <para>At the sea the water thins out over the look's fade length before the coast to a film on the
    /// ground (the shore lift), and stays a film from the coast to the end of the line - which lies on
    /// ground under the lowest tide (<see cref="DownTheSeaFloor"/>). The band is built once: the sea
    /// surface follows the tide and covers whatever lies below it, so at high water the lower part of the
    /// stream is simply under the sea, and at low water the film runs on over the wet beach to the
    /// low-tide line. Nothing stands above the ground it runs over. The band widens from the coast to its
    /// end by the look's share.</para>
    /// <para>In still fresh water the band never lies above the surface less the look's margin (nor under
    /// the ground plus the shore lift where the bank is still dry).</para>
    /// </summary>
    public static (float Surface, float Widen) AtMouth(TerrainLook look, StreamMouth? mouth, float ground, float depthM, double toEndM)
    {
        ArgumentNullException.ThrowIfNull(look);
        if (mouth is null || look.Mouth is not { } data)
        {
            return (ground + depthM, 1);
        }

        if (mouth.Kind == StreamMouthKind.Lake)
        {
            return toEndM <= mouth.LengthM
                ? (MathF.Min(ground + depthM, MathF.Max(mouth.LevelM - data.UnderM, ground + look.ShoreLiftM)), 1)
                : (ground + depthM, 1);
        }

        // Metres before the coast (negative: beyond it, on the beach and the sea floor).
        var before = toEndM - mouth.LengthM;
        if (before >= data.FadeM)
        {
            return (ground + depthM, 1);
        }

        var film = MathF.Min(depthM, look.ShoreLiftM);
        var t = before <= 0 ? 0f : (float)(before / data.FadeM);
        var share = t * t * (3 - (2 * t));
        var widen = before >= 0 || mouth.LengthM <= 0 ? 1f : 1 + (data.WidenShare * (float)Math.Min(1, -before / mouth.LengthM));
        return (ground + film + ((depthM - film) * share), widen);
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
