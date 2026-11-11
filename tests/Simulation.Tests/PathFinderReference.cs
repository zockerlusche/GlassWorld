using GlassWorld.Simulation.Map;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// The path search as it was first written (B2 R10), kept here as the specification of
/// <see cref="PathFinder.Find"/>: A* with the library's priority queue and the priority (total, estimate,
/// cell index). The search in the core is built for speed (flat arrays, its own heap); it has to give the
/// same status, the same cells, the same cost and the same number of expanded nodes as this one for every
/// map and every pair of cells - the walks of a run, and so its state hash, depend on it.
/// </summary>
internal static class PathFinderReference
{
    private const long Straight = 1000;
    private const long Diagonal = 1414;

    private static readonly (int Dx, int Dz, long Step)[] Steps =
        [(1, 0, Straight), (0, 1, Straight), (-1, 0, Straight), (0, -1, Straight), (1, 1, Diagonal), (-1, 1, Diagonal), (-1, -1, Diagonal), (1, -1, Diagonal)];

    /// <summary>The cheapest path from <paramref name="from"/> to <paramref name="to"/>, as the first search found it.</summary>
    public static PathResult Find(WorldMap map, (int X, int Z) from, (int X, int Z) to, int nodeBudget, int minWalkCost, int[]? costs = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        var w = map.WidthCells;
        var size = w * map.DepthCells;
        if (costs is not null && costs.Length != size)
        {
            throw new ArgumentException($"The walk costs have {costs.Length} entries, the map has {size} cells.", nameof(costs));
        }

        int CostOf(int x, int z) => costs is null ? map.Cell(x, z).WalkCost : costs[(z * w) + x];
        if (!map.Contains(from.X, from.Z) || !map.Contains(to.X, to.Z) || CostOf(to.X, to.Z) == MapCell.Blocked)
        {
            return new PathResult(PathStatus.Unreachable, [], 0);
        }

        if (from == to)
        {
            return new PathResult(PathStatus.Found, [], 0);
        }

        var g = new long[size];
        var came = new int[size];
        var seen = new bool[size];
        var closed = new bool[size];
        bool IsClosed(int i) => closed[i];
        long G(int i) => seen[i] ? g[i] : long.MaxValue;
        var start = (from.Z * w) + from.X;
        var goal = (to.Z * w) + to.X;
        var unit = Math.Max(0, minWalkCost);

        long Estimate(int index)
        {
            int dx = Math.Abs((index % w) - to.X), dz = Math.Abs((index / w) - to.Z);
            // Straight steps for the difference, diagonal steps for the overlap: max·1000 + min·414.
            var octile = (Straight * Math.Max(dx, dz)) + ((Diagonal - Straight) * Math.Min(dx, dz));
            return octile * unit;
        }

        var open = new PriorityQueue<int, (long F, long H, int Index)>();
        g[start] = 0;
        seen[start] = true;
        came[start] = -1;
        open.Enqueue(start, (Estimate(start), Estimate(start), start));
        var expanded = 0;
        while (open.TryDequeue(out var current, out _))
        {
            if (IsClosed(current))
            {
                continue;
            }

            if (current == goal)
            {
                return new PathResult(PathStatus.Found, Trace(came, goal, start, w), expanded, g[goal]);
            }

            if (++expanded > nodeBudget)
            {
                return new PathResult(PathStatus.BudgetExceeded, [], expanded);
            }

            closed[current] = true;
            int cx = current % w, cz = current / w;
            foreach (var (dx, dz, step) in Steps)
            {
                int nx = cx + dx, nz = cz + dz;
                if (!map.Contains(nx, nz))
                {
                    continue;
                }

                var cost = CostOf(nx, nz);
                if (cost == MapCell.Blocked)
                {
                    continue;
                }

                // No corner cutting: a diagonal step needs both orthogonal neighbours walkable.
                if (dx != 0 && dz != 0 && (CostOf(cx + dx, cz) == MapCell.Blocked || CostOf(cx, cz + dz) == MapCell.Blocked))
                {
                    continue;
                }

                var next = (nz * w) + nx;
                var tentative = g[current] + (step * cost);
                if (IsClosed(next) || tentative >= G(next))
                {
                    continue;
                }

                g[next] = tentative;
                seen[next] = true;
                came[next] = current;
                var h = Estimate(next);
                open.Enqueue(next, (tentative + h, h, next));
            }
        }

        return new PathResult(PathStatus.Unreachable, [], expanded);
    }

    private static List<(int X, int Z)> Trace(int[] came, int goal, int start, int w)
    {
        var cells = new List<(int X, int Z)>();
        for (var c = goal; c != start; c = came[c])
        {
            cells.Add((c % w, c / w));
        }

        cells.Reverse();
        return cells;
    }
}
