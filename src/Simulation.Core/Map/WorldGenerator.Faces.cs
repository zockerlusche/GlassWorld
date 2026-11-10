using GlassWorld.Contracts;

namespace GlassWorld.Simulation.Map;

/// <summary>Step-stack figures of a map (W1726): how many one-step terrace steps follow each other closely.</summary>
/// <param name="OneSteps">Edges between 4-neighbours that drop by one cliff step (at least one, less than two).</param>
/// <param name="StackedSteps">One-step edges followed by another one-step edge in the same direction within the window.</param>
/// <param name="StackRuns">Stacked sequences: 4-connected groups of the top cells of stacked steps.</param>
/// <param name="MaxFaceDrop">Highest drop between two 4-neighbours on the land (height units).</param>
public sealed record StepStackFigures(int OneSteps, int StackedSteps, int StackRuns, int MaxFaceDrop);

/// <summary>
/// Rock faces (generator version 4, TL #20548, W1726): a stack of one-step terraces (each a narrow walkable
/// ledge with a wall of one cliff step) reads as Minecraft blocks. Where the terraces form such a stack,
/// the ledges sink to the level below until the stack is one higher face with level ground in front.
/// </summary>
public static partial class WorldGenerator
{
    /// <summary>
    /// Sinks the ledges of step stacks (TL #20548): a terraced cell whose tread between a wall above and a
    /// wall below is at most the data's ledge width sinks by one cliff step per round, all cells of a round
    /// at once (order-free, integer), as long as the face above it stays within the data's highest face.
    /// The stack's height gathers in its top face; its ledges join the level ground in front. Fins (a level
    /// band no wider than a ledge with walls down on both sides) and small lone one-step blocks sink with them.
    /// </summary>
    private static void SinkLedges(int[] heights, bool[] terraced, int w, int d, int step, WorldPresetRockFaces faces)
    {
        var limit = faces.MaxSteps * step;
        for (var round = 0; round < faces.Rounds; round++)
        {
            var below = LevelDistance(heights, terraced, w, d, faces.LedgeMaxCells, -step);
            var above = LevelDistance(heights, terraced, w, d, faces.LedgeMaxCells, step);
            var sink = new List<int>();
            for (var i = 0; i < heights.Length; i++)
            {
                var ledge = below[i] + above[i] + 1 <= faces.LedgeMaxCells && HighestNeighbour(heights, w, d, i % w, i / w) - (heights[i] - step) <= limit;
                if (terraced[i] && (ledge || IsFin(heights, terraced, w, d, i % w, i / w, step, faces.LedgeMaxCells)))
                {
                    sink.Add(i);
                }
            }

            sink.AddRange(SmallBlocks(heights, terraced, w, d, step, faces.BlockMaxCells));
            sink = [.. sink.Distinct()];
            if (sink.Count == 0)
            {
                return;
            }

            foreach (var i in sink)
            {
                heights[i] -= step;
            }
        }
    }

    /// <summary>The largest group of walkable cells connected over 4-neighbours (the walk of <see cref="Reachable"/>).</summary>
    private static bool[] LargestWalkableArea(WorldMap map)
    {
        int w = map.WidthCells, d = map.DepthCells;
        var seen = new bool[w * d];
        var best = new bool[w * d];
        var bestCount = 0;
        for (var i = 0; i < w * d; i++)
        {
            if (seen[i] || map.Cell(i % w, i / w).WalkCost == MapCell.Blocked)
            {
                continue;
            }

            var area = Reachable(map, i % w, i / w);
            var count = 0;
            for (var j = 0; j < area.Length; j++)
            {
                if (area[j])
                {
                    seen[j] = true;
                    count++;
                }
            }

            if (count > bestCount)
            {
                bestCount = count;
                best = area;
            }
        }

        return best;
    }

    /// <summary>
    /// A fin: a terraced cell on a level band (east-west or north-south, terraced cells of exactly its
    /// height) at most the given width across, with ground a cliff step or more lower on both sides.
    /// </summary>
    private static bool IsFin(int[] heights, bool[] terraced, int w, int d, int x, int z, int step, int maxWidth)
    {
        var h = heights[(z * w) + x];
        foreach (var (dx, dz) in (ReadOnlySpan<(int, int)>)[(1, 0), (0, 1)])
        {
            var width = 1;
            var fin = true;
            foreach (var sign in (ReadOnlySpan<int>)[1, -1])
            {
                int cx = x, cz = z;
                while (true)
                {
                    cx += sign * dx;
                    cz += sign * dz;
                    if (cx < 0 || cz < 0 || cx >= w || cz >= d)
                    {
                        fin = false;
                        break;
                    }

                    var n = (cz * w) + cx;
                    if (!terraced[n] || heights[n] != h)
                    {
                        fin &= h - heights[n] >= step;
                        break;
                    }

                    width++;
                    if (width > maxWidth)
                    {
                        fin = false;
                        break;
                    }
                }

                if (!fin)
                {
                    break;
                }
            }

            if (fin)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Terraced cells at the top of a wall (a 4-neighbour at least one cliff step lower).</summary>
    private static bool[] WallTops(int[] heights, bool[] terraced, int w, int d, int step)
    {
        var top = new bool[w * d];
        for (var z = 0; z < d; z++)
        {
            for (var x = 0; x < w; x++)
            {
                top[(z * w) + x] = terraced[(z * w) + x] && IsCliffTop(heights, w, d, x, z, step);
            }
        }

        return top;
    }

    /// <summary>The cells of the given number of longest walls (4-connected runs of wall tops), as version 3 keeps them.</summary>
    private static bool[] LongestWalls(int[] heights, bool[] terraced, int w, int d, int step, int count)
    {
        var chosen = new bool[w * d];
        foreach (var run in Components4(WallTops(heights, terraced, w, d, step), w, d).OrderByDescending(r => r.Count).ThenBy(r => r[0]).Take(count))
        {
            foreach (var c in run)
            {
                chosen[c] = true;
            }
        }

        return chosen;
    }

    /// <summary>
    /// Small one-step blocks (TL #20548): a level group of terraced cells (4-neighbours of exactly the same
    /// height) of at most the given size whose every neighbour outside lies one cliff step lower (at least
    /// one step, less than two) is a leftover block of a sunk stack; it sinks with the ledges.
    /// </summary>
    private static List<int> SmallBlocks(int[] heights, bool[] terraced, int w, int d, int step, int maxCells)
    {
        var seen = new bool[w * d];
        var blocks = new List<int>();
        for (var i = 0; i < heights.Length; i++)
        {
            if (!terraced[i] || seen[i])
            {
                continue;
            }

            var group = new List<int>();
            var queue = new Queue<int>([i]);
            seen[i] = true;
            var block = true;
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                group.Add(c);
                foreach (var (dx, dz) in Neighbours4)
                {
                    int nx = (c % w) + dx, nz = (c / w) + dz;
                    if (nx < 0 || nz < 0 || nx >= w || nz >= d)
                    {
                        continue;
                    }

                    var n = (nz * w) + nx;
                    if (terraced[n] && heights[n] == heights[c])
                    {
                        if (!seen[n])
                        {
                            seen[n] = true;
                            queue.Enqueue(n);
                        }
                    }
                    else if (heights[c] - heights[n] < step || heights[c] - heights[n] >= 2 * step)
                    {
                        block = false;
                    }
                }
            }

            if (block && group.Count <= maxCells)
            {
                blocks.AddRange(group);
            }
        }

        return blocks;
    }

    /// <summary>
    /// For every terraced cell the distance (cells, 4-neighbours, over terraced cells of exactly its height)
    /// to the nearest cell next to a wall: going down (sign -1 step: a neighbour lower by at least a step)
    /// or going up (+1 step: a neighbour higher by at least a step). Beyond the cap the distance is the cap.
    /// </summary>
    private static int[] LevelDistance(int[] heights, bool[] terraced, int w, int d, int cap, int signedStep)
    {
        var dist = new int[w * d];
        Array.Fill(dist, cap);
        var queue = new Queue<int>();
        for (var i = 0; i < heights.Length; i++)
        {
            if (!terraced[i])
            {
                continue;
            }

            foreach (var (dx, dz) in Neighbours4)
            {
                int nx = (i % w) + dx, nz = (i / w) + dz;
                if (nx < 0 || nz < 0 || nx >= w || nz >= d)
                {
                    continue;
                }

                var diff = heights[(nz * w) + nx] - heights[i];
                if (signedStep > 0 ? diff >= signedStep : diff <= signedStep)
                {
                    dist[i] = 0;
                    queue.Enqueue(i);
                    break;
                }
            }
        }

        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (dist[c] + 1 >= cap)
            {
                continue;
            }

            foreach (var (dx, dz) in Neighbours4)
            {
                int nx = (c % w) + dx, nz = (c / w) + dz;
                var n = (nz * w) + nx;
                if (nx >= 0 && nz >= 0 && nx < w && nz < d && terraced[n] && heights[n] == heights[c] && dist[n] > dist[c] + 1)
                {
                    dist[n] = dist[c] + 1;
                    queue.Enqueue(n);
                }
            }
        }

        return dist;
    }

    /// <summary>
    /// Level ground in front of the kept faces (TL #20548): from their foot cells, the terraced cells of
    /// exactly the foot's height within the data distance are kept too (no tread relief, no scree).
    /// </summary>
    private static void KeepLevelFoot(int[] heights, bool[] terraced, bool[] kept, List<int> feet, int w, int d, int footCells)
    {
        var dist = new int[w * d];
        Array.Fill(dist, int.MaxValue);
        var queue = new Queue<int>();
        foreach (var f in feet)
        {
            dist[f] = 0;
            queue.Enqueue(f);
        }

        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            kept[c] = true;
            if (dist[c] >= footCells)
            {
                continue;
            }

            foreach (var (dx, dz) in Neighbours4)
            {
                int nx = (c % w) + dx, nz = (c / w) + dz;
                var n = (nz * w) + nx;
                if (nx >= 0 && nz >= 0 && nx < w && nz < d && terraced[n] && heights[n] == heights[c] && dist[n] == int.MaxValue)
                {
                    dist[n] = dist[c] + 1;
                    queue.Enqueue(n);
                }
            }
        }
    }

    /// <summary>
    /// Scree stays scree (TL #20548): where a long steep slope (scree that did not settle within its rounds,
    /// a steep coast) still has one-step walls, a land cell away from the kept walls that stands a cliff
    /// step or more above its lowest land neighbour comes down to one below the cliff step above it, all
    /// cells of a round at once, for the data's rounds. Walls to the sea stay; only the kept faces are walls.
    /// </summary>
    private static void ScreeBelowCliff(int[] heights, bool[] kept, int w, int d, int step, int sea, int rounds)
    {
        for (var round = 0; round < rounds; round++)
        {
            var lower = new List<(int Cell, int Height)>();
            for (var i = 0; i < heights.Length; i++)
            {
                if (!kept[i] && heights[i] >= sea)
                {
                    var lowest = LowestLandNeighbour(heights, w, d, i % w, i / w, sea);
                    if (heights[i] - lowest >= step)
                    {
                        lower.Add((i, lowest + step - 1));
                    }
                }
            }

            if (lower.Count == 0)
            {
                return;
            }

            foreach (var (cell, height) in lower)
            {
                heights[cell] = height;
            }
        }
    }

    /// <summary>Whether a rock bump at (x, z) (+6 in the middle, +3 around) would stand a cliff step above a 4-neighbour.</summary>
    private static bool BumpMakesWall(int[] heights, int w, int x, int z, int step)
    {
        static int Bump(int dx, int dz) => Math.Abs(dx) > 1 || Math.Abs(dz) > 1 ? 0 : dx == 0 && dz == 0 ? 6 : 3;
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var high = heights[((z + dz) * w) + x + dx] + Bump(dx, dz);
                foreach (var (nx, nz) in Neighbours4)
                {
                    if (high - (heights[((z + dz + nz) * w) + x + dx + nx] + Bump(dx + nx, dz + nz)) >= step)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static int LowestLandNeighbour(int[] heights, int w, int d, int x, int z, int sea)
    {
        var lowest = heights[(z * w) + x];
        foreach (var (dx, dz) in Neighbours4)
        {
            int nx = x + dx, nz = z + dz;
            if (nx >= 0 && nz >= 0 && nx < w && nz < d && heights[(nz * w) + nx] >= sea)
            {
                lowest = Math.Min(lowest, heights[(nz * w) + nx]);
            }
        }

        return lowest;
    }

    private static int HighestNeighbour(int[] heights, int w, int d, int x, int z)
    {
        var highest = heights[(z * w) + x];
        foreach (var (dx, dz) in Neighbours4)
        {
            int nx = x + dx, nz = z + dz;
            if (nx >= 0 && nz >= 0 && nx < w && nz < d)
            {
                highest = Math.Max(highest, heights[(nz * w) + nx]);
            }
        }

        return highest;
    }

    /// <summary>
    /// Step-stack figures of a map (W1726 metric): a one-step edge drops between 4-neighbours by at least
    /// <paramref name="step"/> and less than twice it; it is stacked when, walking on in the same direction
    /// from its foot, another one-step edge follows within <paramref name="windowCells"/> cells. Only edges
    /// whose top cell has <paramref name="biome"/> count (null: all land). Stack runs are the 4-connected
    /// groups of the top cells of stacked edges.
    /// </summary>
    public static StepStackFigures StepStacks(WorldMap map, int step, int windowCells, string? biome)
    {
        ArgumentNullException.ThrowIfNull(map);
        int w = map.WidthCells, d = map.DepthCells;
        var tops = new bool[w * d];
        int oneSteps = 0, stacked = 0, maxDrop = 0;
        bool OneStep(int x, int z, int dx, int dz)
        {
            if (!map.Contains(x + dx, z + dz))
            {
                return false;
            }

            var drop = map.Cell(x, z).Height - map.Cell(x + dx, z + dz).Height;
            return drop >= step && drop < 2 * step;
        }

        for (var z = 0; z < d; z++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = map.Cell(x, z);
                if (c.Water != WaterKind.None || (biome is not null && map.BiomeIds[c.Biome] != biome))
                {
                    continue;
                }

                foreach (var (dx, dz) in Neighbours4)
                {
                    if (map.Contains(x + dx, z + dz))
                    {
                        maxDrop = Math.Max(maxDrop, c.Height - map.Cell(x + dx, z + dz).Height);
                    }

                    if (!OneStep(x, z, dx, dz))
                    {
                        continue;
                    }

                    oneSteps++;
                    for (var k = 1; k <= windowCells; k++)
                    {
                        if (OneStep(x + (k * dx), z + (k * dz), dx, dz))
                        {
                            stacked++;
                            tops[(z * w) + x] = true;
                            break;
                        }
                    }
                }
            }
        }

        return new StepStackFigures(oneSteps, stacked, Components4(tops, w, d).Count, maxDrop);
    }
}
