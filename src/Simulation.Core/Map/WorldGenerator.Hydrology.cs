using GlassWorld.Contracts;

namespace GlassWorld.Simulation.Map;

/// <summary>
/// Hydrology from the heightmap (B2 R4, TL #19722): nothing is laid by hand. The sea is the outlet;
/// depressions are filled from it inwards (priority flood, Barnes et al. 2014), every land cell drains to
/// its steepest lower neighbour on the filled surface (D8; on flats and in hollows along the flood path
/// towards the spill point), and the flow accumulation sums the rain of each cell and the springs
/// downstream. Springs rise where the groundwater table meets the ground at the foot of a slope or rock
/// edge. A stream runs where the accumulation reaches the data threshold and the water has visibly come
/// out of the ground - a spring, a pond's overflow or the foot of a slope (TL #19990); its width, depth and bank
/// height grow with the accumulation (data curve), tributaries merge where their paths meet. A fed hollow
/// becomes a pond or lake filled to its spill level; its outlet stream runs on to the sea. Pits too small
/// for a pond are filled up. The stream bed is cut into the ground, always falling downstream.
/// </summary>
public static partial class WorldGenerator
{
    /// <summary>Direction from a cell to its neighbour k is k; from the neighbour back it is (k + 4) % 8.</summary>
    private const int Opposite = 4;


    /// <summary>The sea: cells below sea level that are connected to the map edge (inland pits are land).</summary>
    private static void Sea(int sea, int[] heights, WaterKind[] water, int[] depth, int w, int d)
    {
        var queue = new Queue<int>();
        void Try(int i)
        {
            if (water[i] == WaterKind.None && heights[i] < sea)
            {
                water[i] = WaterKind.Salt;
                depth[i] = sea - heights[i];
                queue.Enqueue(i);
            }
        }

        for (var x = 0; x < w; x++)
        {
            Try(x);
            Try(((d - 1) * w) + x);
        }

        for (var z = 0; z < d; z++)
        {
            Try(z * w);
            Try((z * w) + w - 1);
        }

        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            int cx = c % w, cz = c / w;
            foreach (var (dx, dz) in Neighbours4)
            {
                int nx = cx + dx, nz = cz + dz;
                if (nx >= 0 && nz >= 0 && nx < w && nz < d)
                {
                    Try((nz * w) + nx);
                }
            }
        }
    }

    /// <summary>The drainage of one island: per cell accumulation, stream width and feature.</summary>
    private sealed record Drainage(int[] Accumulation, int[] WidthCm, WaterFeature[] Features);

    private static Drainage Hydrology(WorldPreset preset, int[] heights, int[] groundwater, WaterKind[] water, int[] depth, sbyte[] flow, int w, int d, ulong seed)
    {
        var settings = preset.Water;
        var n = w * d;
        var features = new WaterFeature[n];

        // Closed lakes (more hollow than inflow) become outlets of their own like the sea, so the rest of
        // their hollow drains into them; the flood is repeated until no new closed lake appears.
        var sink = new bool[n];
        var sinkLevel = new int[n];
        List<int>? springs = null;
        var extra = new int[n];
        List<int> order;
        sbyte[] down;
        int[] accumulation;
        for (var round = 0; ; round++)
        {
            // 1. Priority flood from the sea: filled surface, flood parent and an order in which every
            // cell comes after the cell it drains to.
            var (filled, parent, flooded) = PriorityFlood(heights, water, sink, sinkLevel, w, d);
            order = flooded;
            down = Directions(filled, parent, water, sink, w, d);

            // 2. Rain only first, so springs rise beside the rain-fed streams instead of on them.
            if (springs is null)
            {
                var rain = Accumulate(settings, water, down, order, extra, w);
                springs = Springs(preset, heights, filled, groundwater, water, rain, w, d, seed);
                foreach (var s in springs)
                {
                    extra[s] = settings.Springs.Discharge;
                }
            }

            accumulation = Accumulate(settings, water, down, order, extra, w);

            // 3. Hollows a stream or spring feeds: too small or shallow for a pond, they are filled up;
            // as large as their inflow can fill (or the rounds are used up), they fill to the spill level
            // and overflow; larger, they hold a closed lake of the area the inflow fills.
            var inHollow = new bool[n];
            for (var i = 0; i < n; i++)
            {
                inHollow[i] = water[i] == WaterKind.None && !sink[i] && filled[i] > heights[i];
            }

            var plans = new List<(List<int> Cells, bool Lake)>();
            var closed = false;
            foreach (var hollow in Components(inHollow, w, d))
            {
                var inflow = hollow.Max(c => accumulation[c]);
                if (inflow < settings.StreamMinAccumulation)
                {
                    continue;
                }

                var deepest = hollow.Min(c => heights[c]);
                if (hollow.Count < settings.LakeMinCells || filled[hollow[0]] - deepest < settings.LakeMinDepth)
                {
                    plans.Add((hollow, false));
                    continue;
                }

                var area = Math.Max(settings.LakeMinCells, (int)Math.Min(int.MaxValue, (long)inflow * settings.LakeAreaPerInflowPermille / 1000));
                if (hollow.Count <= area || round >= settings.LakeRounds - 1)
                {
                    plans.Add((hollow, true));
                    continue;
                }

                var (cells, level) = ClosedLake(hollow, heights, area, w);
                foreach (var c in cells)
                {
                    sink[c] = true;
                    sinkLevel[c] = level;
                }

                closed = true;
            }

            if (closed)
            {
                continue;
            }

            foreach (var (cells, lake) in plans)
            {
                foreach (var c in cells)
                {
                    if (lake)
                    {
                        water[c] = WaterKind.Fresh;
                        depth[c] = Math.Max(settings.LakeWaterMinDepth, filled[c] - heights[c]);
                        flow[c] = -1;
                        features[c] = WaterFeature.Lake;
                    }
                    else
                    {
                        heights[c] = filled[c];
                    }
                }
            }

            break;
        }

        for (var c = 0; c < n; c++)
        {
            if (sink[c])
            {
                water[c] = WaterKind.Fresh;
                depth[c] = Math.Max(settings.LakeWaterMinDepth, sinkLevel[c] - heights[c]);
                flow[c] = -1;
                features[c] = WaterFeature.Lake;
            }
        }

        var original = (int[])heights.Clone();
        var running = Running(settings, heights, water, features, accumulation, down, order, springs, w, d);

        // 4. Streams where the accumulation is large enough and the water has visibly come out of the
        // ground, bed cut into the ground and falling downstream (cells come after the cell they drain to,
        // so that one is already cut).
        var widthCm = new int[n];
        var channelDepth = new int[n];
        var centre = new bool[n];
        foreach (var c in order)
        {
            if (!running[c])
            {
                continue;
            }

            var (width, waterDepth, bank) = Channel(settings, accumulation[c]);
            var t = Step(c, down[c], w);
            water[c] = WaterKind.Fresh;
            depth[c] = waterDepth;
            flow[c] = down[c];
            features[c] = WaterFeature.Stream;
            widthCm[c] = width;
            channelDepth[c] = waterDepth;
            var bed = heights[c] - waterDepth - bank;
            if ((features[t] & WaterFeature.Lake) != 0 && heights[t] > bed)
            {
                // Where a stream runs into a pond its bed stays below the banks: the pond's bed at the
                // inflow is scoured to the stream bed, its surface stays (TL #19722 polish).
                depth[t] += heights[t] - bed;
                heights[t] = bed;
            }

            heights[c] = Math.Max(bed, heights[t]);
            centre[c] = true;
        }

        foreach (var s in springs)
        {
            features[s] |= WaterFeature.Spring;
        }

        // 5. Wide streams take their side cells: the lowest dry neighbours that lie about as low as the
        // stream (valley floor, not the valley wall).
        var cell = preset.Grid.CellSizeCm;
        foreach (var c in order)
        {
            if (!centre[c] || widthCm[c] <= cell * 3 / 2)
            {
                continue;
            }

            var sides = widthCm[c] > cell * 5 / 2 ? 2 : 1;
            var t = Step(c, flow[c], w);
            var (_, _, bank) = Channel(settings, accumulation[c]);
            int cx = c % w, cz = c / w;
            var options = new List<(int Height, int Index)>();
            foreach (var (dx, dz) in Neighbours4)
            {
                int sx = cx + dx, sz = cz + dz;
                var s = (sz * w) + sx;
                if (sx >= 0 && sz >= 0 && sx < w && sz < d && s != t && water[s] == WaterKind.None && original[s] <= original[c] + bank)
                {
                    options.Add((original[s], s));
                }
            }

            foreach (var (_, s) in options.OrderBy(o => o.Height).ThenBy(o => o.Index).Take(sides))
            {
                water[s] = WaterKind.Fresh;
                depth[s] = channelDepth[c];
                heights[s] = Math.Min(heights[s], heights[c]);
                features[s] = WaterFeature.Stream;
                widthCm[s] = widthCm[c];
                flow[s] = SideDirection(s, c, t, heights, water, w);
            }
        }

        return new Drainage(accumulation, widthCm, features);
    }

    /// <summary>
    /// The cells that carry a stream (TL #19990: no stream out of nowhere). A cell whose accumulation
    /// reaches the threshold runs if water runs into it from a running cell above, or if it is a visible
    /// source: a spring, beside a pond or lake (its overflow), or - with the preset's stream heads - at the
    /// foot of a slope or rock where the ground within the radius rises by the head relief; such a head
    /// becomes a spring of its own (added to <paramref name="springs"/>). Above the first source the water
    /// seeps through the ground. Without stream heads every cell over the threshold runs. Upstream cells
    /// come first (the flood order backwards), so a cell knows whether a running cell feeds it.
    /// </summary>
    private static bool[] Running(WorldPresetWater settings, int[] heights, WaterKind[] water, WaterFeature[] features, int[] accumulation, sbyte[] down, List<int> order, List<int> springs, int w, int d)
    {
        var n = w * d;
        var running = new bool[n];
        var fed = new bool[n];
        var spring = new bool[n];
        foreach (var s in springs)
        {
            spring[s] = true;
        }

        var heads = settings.StreamHeads;
        for (var k = order.Count - 1; k >= 0; k--)
        {
            var c = order[k];
            if (water[c] != WaterKind.None || accumulation[c] < settings.StreamMinAccumulation || down[c] < 0)
            {
                continue;
            }

            if (heads is not null && !fed[c] && !spring[c] && !BesideLake(features, c, w, d))
            {
                if (Rise(heights, c, heads.RadiusCells, w, d) < heads.Relief)
                {
                    continue;
                }

                springs.Add(c);
                spring[c] = true;
            }

            running[c] = true;
            fed[Step(c, down[c], w)] = true;
        }

        return running;
    }

    /// <summary>Whether a pond or lake cell lies among the 8 neighbours.</summary>
    private static bool BesideLake(WaterFeature[] features, int c, int w, int d)
    {
        int cx = c % w, cz = c / w;
        foreach (var (dx, dz) in Neighbours8)
        {
            int nx = cx + dx, nz = cz + dz;
            if (nx >= 0 && nz >= 0 && nx < w && nz < d && (features[(nz * w) + nx] & WaterFeature.Lake) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>How far the highest ground within the square radius rises above the cell (height units).</summary>
    private static int Rise(int[] heights, int c, int radius, int w, int d)
    {
        int cx = c % w, cz = c / w;
        var top = heights[c];
        for (var z = Math.Max(0, cz - radius); z <= Math.Min(d - 1, cz + radius); z++)
        {
            for (var x = Math.Max(0, cx - radius); x <= Math.Min(w - 1, cx + radius); x++)
            {
                top = Math.Max(top, heights[(z * w) + x]);
            }
        }

        return top - heights[c];
    }

    /// <summary>
    /// Priority flood (8 neighbours): from the sea inwards, lowest first, ties first come first served.
    /// Every cell gets the filled height (its own or the spill level of the hollow it lies in) and the
    /// direction to the cell that reached it. Closed lakes are outlets like the sea, at their level. The order lists cells as they were taken, so a cell always
    /// comes after every cell with a lower filled height and after its flood parent.
    /// </summary>
    private static (int[] Filled, sbyte[] Parent, List<int> Order) PriorityFlood(int[] heights, WaterKind[] water, bool[] sink, int[] sinkLevel, int w, int d)
    {
        var n = w * d;
        var filled = (int[])heights.Clone();
        var parent = new sbyte[n];
        Array.Fill(parent, (sbyte)-1);
        var taken = new bool[n];
        var queue = new PriorityQueue<int, (int Height, int Sequence)>();
        var sequence = 0;
        for (var i = 0; i < n; i++)
        {
            int x = i % w, z = i / w;
            if (water[i] == WaterKind.Salt || sink[i] || x == 0 || z == 0 || x == w - 1 || z == d - 1)
            {
                taken[i] = true;
                filled[i] = sink[i] ? sinkLevel[i] : heights[i];
                queue.Enqueue(i, (filled[i], sequence++));
            }
        }

        var order = new List<int>(n);
        while (queue.TryDequeue(out var c, out _))
        {
            order.Add(c);
            int cx = c % w, cz = c / w;
            for (var k = 0; k < 8; k++)
            {
                int nx = cx + Neighbours8[k].Dx, nz = cz + Neighbours8[k].Dz;
                var nb = (nz * w) + nx;
                if (nx < 0 || nz < 0 || nx >= w || nz >= d || taken[nb])
                {
                    continue;
                }

                taken[nb] = true;
                filled[nb] = Math.Max(heights[nb], filled[c]);
                parent[nb] = (sbyte)((k + Opposite) % 8);
                queue.Enqueue(nb, (filled[nb], sequence++));
            }
        }

        return (filled, parent, order);
    }

    /// <summary>
    /// D8 on the filled surface: the neighbour with the steepest fall (height drop per distance, diagonal
    /// steps 1.414 long), ties by direction order; on flats and in hollows the flood parent. The sea,
    /// closed lakes and the map edge drain nowhere (-1).
    /// </summary>
    private static sbyte[] Directions(int[] filled, sbyte[] parent, WaterKind[] water, bool[] sink, int w, int d)
    {
        var down = new sbyte[w * d];
        Array.Fill(down, (sbyte)-1);
        for (var z = 1; z < d - 1; z++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                var c = (z * w) + x;
                if (water[c] == WaterKind.Salt || sink[c])
                {
                    continue;
                }

                int best = -1, bestFall = 0;
                for (var k = 0; k < 8; k++)
                {
                    var nb = ((z + Neighbours8[k].Dz) * w) + x + Neighbours8[k].Dx;
                    var fall = (filled[c] - filled[nb]) * (k % 2 == 0 ? 1000 : 707);
                    if (fall > bestFall)
                    {
                        best = k;
                        bestFall = fall;
                    }
                }

                down[c] = best >= 0 ? (sbyte)best : parent[c];
            }
        }

        return down;
    }

    /// <summary>Flow accumulation: rain of every land cell plus the extra water, passed downstream.</summary>
    private static int[] Accumulate(WorldPresetWater settings, WaterKind[] water, sbyte[] down, List<int> order, int[] extra, int w)
    {
        var accumulation = new int[water.Length];
        for (var i = 0; i < water.Length; i++)
        {
            accumulation[i] = water[i] == WaterKind.Salt ? 0 : settings.RunoffPerCell + extra[i];
        }

        for (var k = order.Count - 1; k >= 0; k--)
        {
            var c = order[k];
            if (water[c] != WaterKind.Salt && down[c] >= 0)
            {
                accumulation[Step(c, down[c], w)] += accumulation[c];
            }
        }

        return accumulation;
    }

    /// <summary>
    /// Springs: dry land high enough above and far enough from the sea where the groundwater table lies at most the data depth
    /// below the ground, at the foot of a slope or rock edge (the ground around rises by the foot relief),
    /// walkable, not in a hollow and not already on a rain-fed stream. The wettest candidates come first (then the higher
    /// relief, then an order from the seed); springs keep the minimum spacing, up to the most allowed.
    /// </summary>
    private static List<int> Springs(WorldPreset preset, int[] heights, int[] filled, int[] groundwater, WaterKind[] water, int[] rain, int w, int d, ulong seed)
    {
        var settings = preset.Water.Springs;
        var sea = preset.Terrain.SeaLevel;
        var r = settings.FootRadiusCells;

        // Distance from the sea (4 neighbours), for the springs' minimum distance from the coast.
        var seaDistance = new int[w * d];
        Array.Fill(seaDistance, int.MaxValue);
        var queue = new Queue<int>();
        for (var i = 0; i < w * d; i++)
        {
            if (water[i] == WaterKind.Salt)
            {
                seaDistance[i] = 0;
                queue.Enqueue(i);
            }
        }

        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            foreach (var (dx, dz) in Neighbours4)
            {
                int nx = (c % w) + dx, nz = (c / w) + dz;
                if (nx >= 0 && nz >= 0 && nx < w && nz < d && seaDistance[(nz * w) + nx] == int.MaxValue)
                {
                    seaDistance[(nz * w) + nx] = seaDistance[c] + 1;
                    queue.Enqueue((nz * w) + nx);
                }
            }
        }

        var candidates = new List<(int Wet, int Relief, ulong Tie, int Index)>();
        for (var z = r; z < d - r; z++)
        {
            for (var x = r; x < w - r; x++)
            {
                var i = (z * w) + x;
                if (water[i] != WaterKind.None || filled[i] > heights[i] || heights[i] - sea < settings.MinHeight || seaDistance[i] < settings.MinSeaDistanceCells || groundwater[i] > settings.MaxGroundwaterDepth
                    || rain[i] >= preset.Water.StreamMinAccumulation || heights[i] - LowestNeighbour(heights, w, d, x, z) > preset.Terrain.WalkableSlope)
                {
                    continue;
                }

                var top = heights[i];
                for (var dz = -r; dz <= r; dz++)
                {
                    for (var dx = -r; dx <= r; dx++)
                    {
                        top = Math.Max(top, heights[((z + dz) * w) + x + dx]);
                    }
                }

                if (top - heights[i] >= settings.FootRelief)
                {
                    candidates.Add((groundwater[i], top - heights[i], Mix(seed ^ (ulong)i), i));
                }
            }
        }

        var springs = new List<int>();
        var spacing2 = settings.MinSpacingCells * settings.MinSpacingCells;
        foreach (var (_, _, _, i) in candidates.OrderBy(c => c.Wet - (c.Relief / 3)).ThenBy(c => c.Tie))
        {
            if (springs.Count >= settings.MaxCount)
            {
                break;
            }

            int x = i % w, z = i / w;
            if (springs.All(s => Sq((s % w) - x) + Sq((s / w) - z) >= spacing2))
            {
                springs.Add(i);
            }
        }

        return springs;
    }

    /// <summary>Width (cm), water depth and bank height of a stream with this accumulation (data curve, linear).</summary>
    private static (int WidthCm, int Depth, int Bank) Channel(WorldPresetWater settings, int accumulation)
    {
        var points = settings.Channel.ToList();
        if (accumulation <= points[0].Accumulation)
        {
            return (points[0].WidthCm, points[0].Depth, points[0].Bank);
        }

        for (var k = 1; k < points.Count; k++)
        {
            var (a, b) = (points[k - 1], points[k]);
            if (accumulation <= b.Accumulation)
            {
                var span = Math.Max(1, b.Accumulation - a.Accumulation);
                var at = accumulation - a.Accumulation;
                int Lerp(int from, int to) => from + (int)((long)(to - from) * at / span);
                return (Lerp(a.WidthCm, b.WidthCm), Lerp(a.Depth, b.Depth), Lerp(a.Bank, b.Bank));
            }
        }

        return (points[^1].WidthCm, points[^1].Depth, points[^1].Bank);
    }

    /// <summary>
    /// A side cell runs with its stream: into the stream's next cell if that is its neighbour and not
    /// higher, else into its centre cell if that is not higher; otherwise it lies still.
    /// </summary>
    private static sbyte SideDirection(int side, int centre, int next, int[] heights, WaterKind[] water, int w)
    {
        int sx = side % w, sz = side / w;
        foreach (var target in new[] { next, centre })
        {
            int dx = (target % w) - sx, dz = (target / w) - sz;
            if (Math.Abs(dx) <= 1 && Math.Abs(dz) <= 1 && (dx, dz) != (0, 0) && water[target] != WaterKind.None && heights[target] <= heights[side])
            {
                for (var k = 0; k < 8; k++)
                {
                    if (Neighbours8[k] == (dx, dz))
                    {
                        return (sbyte)k;
                    }
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// A closed lake in a hollow: it grows from the deepest cell, lowest neighbour first, until it has the
    /// area its inflow fills; cells lower than its level by then join too. Returns the cells and the level
    /// (highest ground under the water).
    /// </summary>
    private static (List<int> Cells, int Level) ClosedLake(List<int> hollow, int[] heights, int area, int w)
    {
        var inside = hollow.ToHashSet();
        var start = hollow.OrderBy(c => heights[c]).ThenBy(c => c).First();
        var queue = new PriorityQueue<int, (int Height, int Index)>();
        var seen = new HashSet<int> { start };
        queue.Enqueue(start, (heights[start], start));
        var cells = new List<int>();
        var level = heights[start];
        while (queue.TryPeek(out var c, out var key) && (cells.Count < area || key.Height < level))
        {
            queue.Dequeue();
            level = Math.Max(level, key.Height);
            cells.Add(c);
            foreach (var (dx, dz) in Neighbours8)
            {
                var nb = c + (dz * w) + dx;
                if (inside.Contains(nb) && seen.Add(nb))
                {
                    queue.Enqueue(nb, (heights[nb], nb));
                }
            }
        }

        return (cells, level);
    }

    /// <summary>Connected groups (8 neighbours) of marked cells, each in the order found.</summary>
    private static List<List<int>> Components(bool[] marked, int w, int d)
    {
        var seen = new bool[marked.Length];
        var groups = new List<List<int>>();
        for (var i = 0; i < marked.Length; i++)
        {
            if (!marked[i] || seen[i])
            {
                continue;
            }

            var group = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(i);
            seen[i] = true;
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                group.Add(c);
                int cx = c % w, cz = c / w;
                foreach (var (dx, dz) in Neighbours8)
                {
                    int nx = cx + dx, nz = cz + dz;
                    var nb = (nz * w) + nx;
                    if (nx >= 0 && nz >= 0 && nx < w && nz < d && marked[nb] && !seen[nb])
                    {
                        seen[nb] = true;
                        queue.Enqueue(nb);
                    }
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    private static int Step(int cell, int direction, int w) => cell + (Neighbours8[direction].Dz * w) + Neighbours8[direction].Dx;

    private static int Sq(int v) => v * v;
}
