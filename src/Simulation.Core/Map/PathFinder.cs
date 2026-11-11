namespace GlassWorld.Simulation.Map;

/// <summary>Outcome of a path search.</summary>
public enum PathStatus
{
    /// <summary>A path was found.</summary>
    Found,

    /// <summary>No path exists (blocked cells, deep water).</summary>
    Unreachable,

    /// <summary>The search used up its node budget before reaching the goal.</summary>
    BudgetExceeded,
}

/// <summary>A path of cells from (excluding) the start to (including) the goal.</summary>
/// <param name="Status">Whether a path was found.</param>
/// <param name="Cells">The cells to walk, in order; empty unless found (or if start equals goal).</param>
/// <param name="Expanded">Nodes the search expanded.</param>
/// <param name="Cost">Total cost of the path (walk cost × step length in thousandths; 0 unless found).</param>
public sealed record PathResult(PathStatus Status, IReadOnlyList<(int X, int Z)> Cells, int Expanded, long Cost = 0);

/// <summary>
/// A* over the walk costs of the base grid (B2 R10): eight directions, diagonals only when both
/// orthogonal neighbours are walkable, step cost = walk cost × 1000 (straight) or × 1414 (diagonal), all
/// in integers without rounding per step; the estimate is the octile distance × the lowest walk cost in
/// the same units, so it never exceeds the true cost (admissible, review GW2 #94).
/// Ties are broken by a fixed rule (lower total, then lower remaining estimate, then lower cell index),
/// so the same map and endpoints always give the same path. A node budget from the data bounds every
/// search. Integers only (ADR-0002); no Godot navigation.
/// </summary>
public static class PathFinder
{
    /// <summary>
    /// Reusable search arrays for one map size: a stamp per search marks valid entries, so nothing is
    /// cleared or allocated between searches (results never depend on the scratch's history).
    /// </summary>
    public sealed class Scratch(int size)
    {
        internal Node[] Nodes { get; } = new Node[size];

        internal int Stamp { get; set; }

        internal int Size { get; } = size;

        /// <summary>The open list of the search that is running: a binary heap, smallest first.</summary>
        internal Open[] Heap { get; set; } = new Open[256];
    }

    /// <summary>
    /// What a search knows about a cell, kept together so that looking at a neighbour touches one place in
    /// memory: the cost so far, the cell it was reached from, and a mark - twice the stamp of the search that
    /// saw it, plus one once that search has closed it. Any other mark is an older search's and counts as unseen.
    /// </summary>
    internal struct Node
    {
        public long G;
        public int Came;
        public int Mark;
    }

    /// <summary>
    /// An entry of the open list: a cell with its total (cost so far + estimate) and its estimate. The order
    /// is the search's fixed rule - lower total, then lower estimate, then lower cell index - and it is
    /// strict: two entries are never equal in it unless they are the same cell at the same cost, so every
    /// heap that keeps this order hands the cells out in the same sequence.
    /// </summary>
    internal readonly record struct Open(long F, long H, int Index)
    {
        public bool Before(in Open other) =>
            F != other.F ? F < other.F : H != other.H ? H < other.H : Index < other.Index;
    }

    private const long Straight = 1000;
    private const long Diagonal = 1414;

    private static readonly (int Dx, int Dz, long Step)[] Steps =
        [(1, 0, Straight), (0, 1, Straight), (-1, 0, Straight), (0, -1, Straight), (1, 1, Diagonal), (-1, 1, Diagonal), (-1, -1, Diagonal), (1, -1, Diagonal)];

    /// <summary>
    /// Walkable areas: flood fill with the path finder's step rule; one area number per cell (index
    /// z × width + x), 0 = blocked. Two cells with the same number are connected by a walk.
    /// </summary>
    public static int[] WalkableAreas(WorldMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        int w = map.WidthCells, d = map.DepthCells;
        var areas = new int[w * d];
        var next = 0;
        var queue = new Queue<int>();
        for (var i = 0; i < w * d; i++)
        {
            if (areas[i] != 0 || map.Cell(i % w, i / w).WalkCost == MapCell.Blocked)
            {
                continue;
            }

            areas[i] = ++next;
            queue.Enqueue(i);
            while (queue.TryDequeue(out var c))
            {
                int cx = c % w, cz = c / w;
                for (var dz = -1; dz <= 1; dz++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx, nz = cz + dz;
                        if ((dx == 0 && dz == 0) || !map.Contains(nx, nz) || areas[(nz * w) + nx] != 0 || map.Cell(nx, nz).WalkCost == MapCell.Blocked)
                        {
                            continue;
                        }

                        if (dx != 0 && dz != 0 && (map.Cell(cx + dx, cz).WalkCost == MapCell.Blocked || map.Cell(cx, cz + dz).WalkCost == MapCell.Blocked))
                        {
                            continue;
                        }

                        areas[(nz * w) + nx] = next;
                        queue.Enqueue((nz * w) + nx);
                    }
                }
            }
        }

        return areas;
    }

    /// <summary>Finds the cheapest path from <paramref name="from"/> to <paramref name="to"/>.</summary>
    /// <param name="map">The base grid.</param>
    /// <param name="from">Start cell (may itself be costly; it is never entered).</param>
    /// <param name="to">Goal cell.</param>
    /// <param name="nodeBudget">Most nodes to expand.</param>
    /// <param name="minWalkCost">Lowest walk cost on the map (keeps the estimate admissible).</param>
    /// <param name="scratch">Reusable arrays (optional; must match the map size).</param>
    /// <param name="costs">
    /// Walk costs of a run-time layer over the map (index z × width + x, e.g. the tides, B2-5 R15), read
    /// instead of the cells' base costs; null = the base costs. <paramref name="minWalkCost"/> must then be
    /// the lowest of these costs.
    /// </param>
    public static PathResult Find(WorldMap map, (int X, int Z) from, (int X, int Z) to, int nodeBudget, int minWalkCost, Scratch? scratch = null, int[]? costs = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        var w = map.WidthCells;
        var d = map.DepthCells;
        var size = w * d;
        if (costs is not null && costs.Length != size)
        {
            throw new ArgumentException($"The walk costs have {costs.Length} entries, the map has {size} cells.", nameof(costs));
        }

        // One flat array of walk costs, whoever gives them: the run-time layer or the map itself.
        var cost = costs ?? map.WalkCosts;
        if (!map.Contains(from.X, from.Z) || !map.Contains(to.X, to.Z) || cost[(to.Z * w) + to.X] == MapCell.Blocked)
        {
            return new PathResult(PathStatus.Unreachable, [], 0);
        }

        if (from == to)
        {
            return new PathResult(PathStatus.Found, [], 0);
        }

        var s = scratch is { } given && given.Size == size ? given : new Scratch(size);
        if (++s.Stamp >= int.MaxValue / 2)
        {
            Array.Clear(s.Nodes);
            s.Stamp = 1;
        }

        var seenMark = s.Stamp * 2;
        var closedMark = seenMark + 1;
        var nodes = s.Nodes;
        var heap = s.Heap;
        var count = 0;
        var start = (from.Z * w) + from.X;
        var goal = (to.Z * w) + to.X;
        long unit = Math.Max(0, minWalkCost);
        int toX = to.X, toZ = to.Z;

        nodes[start] = new Node { G = 0, Came = -1, Mark = seenMark };
        var first = Estimate(from.X, from.Z, toX, toZ) * unit;
        heap[count++] = new Open(first, first, start);
        var expanded = 0;
        var steps = Steps;
        while (count > 0)
        {
            // Take the smallest entry out: the last one moves to the top and sinks to its place.
            var current = heap[0].Index;
            var last = heap[--count];
            var hole = 0;
            while (true)
            {
                var child = (2 * hole) + 1;
                if (child >= count)
                {
                    break;
                }

                if (child + 1 < count && heap[child + 1].Before(heap[child]))
                {
                    child++;
                }

                if (!heap[child].Before(last))
                {
                    break;
                }

                heap[hole] = heap[child];
                hole = child;
            }

            heap[hole] = last;
            ref var here = ref nodes[current];
            if (here.Mark == closedMark)
            {
                continue;
            }

            if (current == goal)
            {
                return new PathResult(PathStatus.Found, Trace(nodes, goal, start, w), expanded, here.G);
            }

            if (++expanded > nodeBudget)
            {
                return new PathResult(PathStatus.BudgetExceeded, [], expanded);
            }

            here.Mark = closedMark;
            int cx = current % w, cz = current / w;
            var soFar = here.G;
            for (var i = 0; i < steps.Length; i++)
            {
                var (dx, dz, step) = steps[i];
                int nx = cx + dx, nz = cz + dz;
                if (nx < 0 || nz < 0 || nx >= w || nz >= d)
                {
                    continue;
                }

                var next = (nz * w) + nx;
                var there = cost[next];
                if (there == MapCell.Blocked)
                {
                    continue;
                }

                // No corner cutting: a diagonal step needs both orthogonal neighbours walkable.
                if (dx != 0 && dz != 0 && (cost[current + dx] == MapCell.Blocked || cost[next - dx] == MapCell.Blocked))
                {
                    continue;
                }

                var tentative = soFar + (step * there);
                ref var node = ref nodes[next];
                if (node.Mark == closedMark || (node.Mark == seenMark && tentative >= node.G))
                {
                    continue;
                }

                node.G = tentative;
                node.Mark = seenMark;
                node.Came = current;
                var h = Estimate(nx, nz, toX, toZ) * unit;

                // Put the entry in: it rises from the end to its place.
                if (count == heap.Length)
                {
                    Array.Resize(ref heap, heap.Length * 2);
                    s.Heap = heap;
                }

                var entry = new Open(tentative + h, h, next);
                var at = count++;
                while (at > 0)
                {
                    var parent = (at - 1) / 2;
                    if (!entry.Before(heap[parent]))
                    {
                        break;
                    }

                    heap[at] = heap[parent];
                    at = parent;
                }

                heap[at] = entry;
            }
        }

        return new PathResult(PathStatus.Unreachable, [], expanded);
    }

    /// <summary>
    /// The octile distance between two cells in thousandths of a straight step: straight steps for the
    /// difference, diagonal steps for the overlap (max x 1000 + min x 414).
    /// </summary>
    private static long Estimate(int x, int z, int toX, int toZ)
    {
        int dx = Math.Abs(x - toX), dz = Math.Abs(z - toZ);
        return (Straight * Math.Max(dx, dz)) + ((Diagonal - Straight) * Math.Min(dx, dz));
    }

    private static List<(int X, int Z)> Trace(Node[] nodes, int goal, int start, int w)
    {
        var cells = new List<(int X, int Z)>();
        for (var c = goal; c != start; c = nodes[c].Came)
        {
            cells.Add((c % w, c / w));
        }

        cells.Reverse();
        return cells;
    }
}
