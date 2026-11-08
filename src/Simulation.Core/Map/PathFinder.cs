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
public sealed record PathResult(PathStatus Status, IReadOnlyList<(int X, int Z)> Cells, int Expanded);

/// <summary>
/// A* over the walk costs of the base grid (B2 R10): eight directions, diagonals only when both
/// orthogonal neighbours are walkable, step cost = 10 (straight) or 14 (diagonal) × walk cost / 1000.
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
        internal long[] G { get; } = new long[size];

        internal int[] Came { get; } = new int[size];

        internal int[] Seen { get; } = new int[size];

        internal int[] Closed { get; } = new int[size];

        internal int Stamp { get; set; }

        internal int Size { get; } = size;
    }

    private static readonly (int Dx, int Dz, int Step)[] Steps =
        [(1, 0, 10), (0, 1, 10), (-1, 0, 10), (0, -1, 10), (1, 1, 14), (-1, 1, 14), (-1, -1, 14), (1, -1, 14)];

    /// <summary>Finds the cheapest path from <paramref name="from"/> to <paramref name="to"/>.</summary>
    /// <param name="map">The base grid.</param>
    /// <param name="from">Start cell (may itself be costly; it is never entered).</param>
    /// <param name="to">Goal cell.</param>
    /// <param name="nodeBudget">Most nodes to expand.</param>
    /// <param name="minWalkCost">Lowest walk cost on the map (keeps the estimate admissible).</param>
    /// <param name="scratch">Reusable arrays (optional; must match the map size).</param>
    public static PathResult Find(WorldMap map, (int X, int Z) from, (int X, int Z) to, int nodeBudget, int minWalkCost, Scratch? scratch = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (!map.Contains(from.X, from.Z) || !map.Contains(to.X, to.Z) || map.Cell(to.X, to.Z).WalkCost == MapCell.Blocked)
        {
            return new PathResult(PathStatus.Unreachable, [], 0);
        }

        if (from == to)
        {
            return new PathResult(PathStatus.Found, [], 0);
        }

        var w = map.WidthCells;
        var size = w * map.DepthCells;
        var s = scratch is { } given && given.Size == size ? given : new Scratch(size);
        if (++s.Stamp == int.MaxValue)
        {
            Array.Clear(s.Seen);
            Array.Clear(s.Closed);
            s.Stamp = 1;
        }

        var stamp = s.Stamp;
        var g = s.G;
        var came = s.Came;
        bool IsClosed(int i) => s.Closed[i] == stamp;
        long G(int i) => s.Seen[i] == stamp ? g[i] : long.MaxValue;
        var start = (from.Z * w) + from.X;
        var goal = (to.Z * w) + to.X;
        var unit = Math.Max(1, minWalkCost);

        long Estimate(int index)
        {
            int dx = Math.Abs((index % w) - to.X), dz = Math.Abs((index / w) - to.Z);
            var octile = (10L * Math.Max(dx, dz)) + (4L * Math.Min(dx, dz));
            return octile * unit / 1000;
        }

        var open = new PriorityQueue<int, (long F, long H, int Index)>();
        g[start] = 0;
        s.Seen[start] = stamp;
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
                return new PathResult(PathStatus.Found, Trace(came, goal, start, w), expanded);
            }

            if (++expanded > nodeBudget)
            {
                return new PathResult(PathStatus.BudgetExceeded, [], expanded);
            }

            s.Closed[current] = stamp;
            int cx = current % w, cz = current / w;
            foreach (var (dx, dz, step) in Steps)
            {
                int nx = cx + dx, nz = cz + dz;
                if (!map.Contains(nx, nz))
                {
                    continue;
                }

                var cost = map.Cell(nx, nz).WalkCost;
                if (cost == MapCell.Blocked)
                {
                    continue;
                }

                // No corner cutting: a diagonal step needs both orthogonal neighbours walkable.
                if (dx != 0 && dz != 0 && (map.Cell(cx + dx, cz).WalkCost == MapCell.Blocked || map.Cell(cx, cz + dz).WalkCost == MapCell.Blocked))
                {
                    continue;
                }

                var next = (nz * w) + nx;
                var tentative = g[current] + ((long)step * cost / 1000);
                if (IsClosed(next) || tentative >= G(next))
                {
                    continue;
                }

                g[next] = tentative;
                s.Seen[next] = stamp;
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
