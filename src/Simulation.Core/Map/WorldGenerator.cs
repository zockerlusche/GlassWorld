using GlassWorld.Contracts;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Random;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Map;

/// <summary>An object instance the generator places (type id from the catalogue, cell position).</summary>
public sealed record IslandObject(string TypeId, int X, int Z);

/// <summary>A start position of one inhabitant; pair members share the pair index.</summary>
public sealed record StartPosition(int Pair, int X, int Z);

/// <summary>A neighbour island behind the cloud cover with a small group (B2520; contact is B9).</summary>
public sealed record NeighbourIsland(int X, int Z, int GroupSize);

/// <summary>Result of one mandatory category of the start-island check (R7).</summary>
public sealed record MandatoryResult(string Id, int Reachable, int Total, int Min, int Max)
{
    /// <summary>Whether the category is satisfied: enough reachable from every start, not too many in total.</summary>
    public bool Passed => Reachable >= Min && Total <= Max;
}

/// <summary>A generated start island (B2 R2–R9, R11).</summary>
public sealed record GeneratedIsland(
    string PresetId,
    int GeneratorVersion,
    ulong Seed,
    int Attempts,
    bool Passed,
    WorldMap Map,
    IReadOnlyList<IslandObject> Objects,
    IReadOnlyList<StartPosition> Starts,
    IReadOnlyList<NeighbourIsland> Neighbours,
    IReadOnlyList<MandatoryResult> Check,
    int CliffCells)
{
    /// <summary>World hash over the generated data only (B2 acceptance 1).</summary>
    public string Hash()
    {
        using var hasher = new StateHasher();
        WriteTo(hasher);
        return hasher.Finish();
    }

    /// <summary>Writes the generated data in a fixed order.</summary>
    public void WriteTo(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Section("worldmap").Write(PresetId).Write(GeneratorVersion).Write(Seed).Write(Attempts).Write(Passed);
        Map.WriteTo(hasher);
        hasher.Write(Objects.Count);
        foreach (var o in Objects)
        {
            hasher.Write(o.TypeId).Write(o.X).Write(o.Z);
        }

        hasher.Write(Starts.Count);
        foreach (var s in Starts)
        {
            hasher.Write(s.Pair).Write(s.X).Write(s.Z);
        }

        hasher.Write(Neighbours.Count);
        foreach (var n in Neighbours)
        {
            hasher.Write(n.X).Write(n.Z).Write(n.GroupSize);
        }
    }
}

/// <summary>
/// The world generator (B2 R2–R9): a pure function of (world seed, preset, generator version). Integer
/// arithmetic only (ADR-0002); randomness from its own deterministic streams derived from the seed and a
/// name (<c>worldgen/height</c>, <c>worldgen/water</c>, …), so no other stream is touched. Height noise
/// depends only on (seed, cell), so chunks can be generated on their own later (B9). If the mandatory
/// check fails, it retries with derived sub-seeds and reports the attempts; after the last attempt the
/// failed result is returned (never silently passed).
/// </summary>
public static class WorldGenerator
{
    private static readonly (int Dx, int Dz)[] Neighbours8 = [.. FlowDirections.All];
    private static readonly (int Dx, int Dz)[] Neighbours4 = [(1, 0), (0, -1), (-1, 0), (0, 1)];

    /// <summary>Generates the start island; retries with derived seeds until the check passes.</summary>
    public static GeneratedIsland Generate(WorldGenContent content, ObjectCatalog catalog, string presetId, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!content.Presets.TryGetValue(presetId, out var preset))
        {
            throw new ArgumentException($"unknown world preset '{presetId}'", nameof(presetId));
        }

        GeneratedIsland? last = null;
        for (var attempt = 1; attempt <= preset.Start.MaxAttempts; attempt++)
        {
            var attemptSeed = attempt == 1 ? seed : Mix(seed ^ Mix((ulong)attempt));
            last = Attempt(content, catalog, preset, seed, attemptSeed, attempt);
            if (last.Passed)
            {
                return last;
            }
        }

        return last!;
    }

    private static GeneratedIsland Attempt(WorldGenContent content, ObjectCatalog catalog, WorldPreset preset, ulong seed, ulong attemptSeed, int attempt)
    {
        var grid = preset.Grid;
        int w = grid.WidthCells, d = grid.DepthCells;
        var sea = preset.Terrain.SeaLevel;
        var heights = Heights(preset, attemptSeed);
        var cliffs = Terraces(preset, heights, w, d, attemptSeed);
        Rocks(preset, heights, w, d, Stream(attemptSeed, "worldgen/rocks"));

        var water = new WaterKind[w * d];
        var depth = new int[w * d];
        var flow = new sbyte[w * d];
        Array.Fill(flow, (sbyte)-1);
        for (var i = 0; i < w * d; i++)
        {
            if (heights[i] < sea)
            {
                water[i] = WaterKind.Salt;
                depth[i] = sea - heights[i];
            }
        }

        Streams(preset, heights, water, depth, flow, w, d, Stream(attemptSeed, "worldgen/water"));
        var moisture = Moisture(preset, heights, water, w, d, attemptSeed);
        Swamp(preset, heights, water, depth, moisture, w, d);

        var biomeIds = content.BiomeIds;
        var builder = new WorldMapBuilder(grid.ChunkCells, w / grid.ChunkCells, d / grid.ChunkCells, biomeIds);
        var biomeOf = new byte[w * d];
        var walk = new int[w * d];
        for (var z = 0; z < d; z++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (z * w) + x;
                var temperature = preset.Climate.BaseTemperatureTenthC - (preset.Climate.TemperatureDropPer100Height * Math.Max(0, heights[i] - sea) / 100);
                biomeOf[i] = Biome(content.Biomes, biomeIds, heights[i] - sea, water[i], depth[i], moisture[i], temperature);
                walk[i] = WalkCost(preset, content.Biomes, biomeIds[biomeOf[i]], heights, water, depth, w, d, x, z);
                builder.Set(x, z, new MapCell(heights[i], water[i], depth[i], flow[i], moisture[i], temperature, biomeOf[i], walk[i]));
            }
        }

        var map = builder.Build();
        var objects = new List<IslandObject>();
        Caves(preset, map, objects, Stream(attemptSeed, "worldgen/caves"));
        Spawn(content.Spawns, map, objects, Stream(attemptSeed, "worldgen/spawns"));
        var starts = Starts(preset, map, Stream(attemptSeed, "worldgen/start"));
        StartFavor(preset.Start.Favor, content.Spawns, catalog, map, objects, starts, Stream(attemptSeed, "worldgen/favor"));
        var neighbours = Neighbour(preset, w, d, Stream(attemptSeed, "worldgen/neighbours"));
        var check = Check(content.Spawns, catalog, map, objects, starts);
        var present = new bool[biomeIds.Count];
        foreach (var b in biomeOf)
        {
            present[b] = true;
        }

        var passed = starts.Count == preset.Start.Pairs * 2 && check.All(c => c.Passed) && Connected(map, starts) && present.All(p => p);
        objects.Sort((a, b) => (a.Z, a.X, string.CompareOrdinal(a.TypeId, b.TypeId)).CompareTo((b.Z, b.X, 0)));
        return new GeneratedIsland(preset.Id, preset.GeneratorVersion, seed, attempt, passed, map, objects, starts, neighbours, check, cliffs);
    }

    // ---------------------------------------------------------------- terrain

    private static int[] Heights(WorldPreset preset, ulong seed)
    {
        int w = preset.Grid.WidthCells, d = preset.Grid.DepthCells;
        var terrain = preset.Terrain;
        var octaves = terrain.Noise.ToList();
        var heights = new int[w * d];
        var falloffStart = 1000 - terrain.CoastFalloffPermille;
        for (var z = 0; z < d; z++)
        {
            for (var x = 0; x < w; x++)
            {
                var raw = 0;
                for (var o = 0; o < octaves.Count; o++)
                {
                    raw += octaves[o].Amplitude * ValueNoise(seed, o, x, z, octaves[o].WavelengthCells) / 1000;
                }

                // Distance from the centre in permille of the half size (0 centre, 1000 at the edge midpoints).
                var dx = Math.Abs((2 * x) + 1 - w) * 1000 / w;
                var dz = Math.Abs((2 * z) + 1 - d) * 1000 / d;
                var r = Isqrt((dx * dx) + (dz * dz));
                r += (ValueNoise(seed ^ 0xC0A57, 9, x, z, 40) - 500) * 2 / 5;
                var fall = terrain.CoastFalloffPermille == 0 ? (r >= 1000 ? 1000 : 0) : Math.Clamp((r - falloffStart) * 1000 / terrain.CoastFalloffPermille, 0, 1000);
                var h = (raw + terrain.SeaLevel) * (1000 - fall) / 1000;
                heights[(z * w) + x] = Math.Clamp(h, 0, terrain.MaxHeight);
            }
        }

        return heights;
    }

    /// <summary>High ground becomes terraces of cliff height, so the hills get cliffs (R3). Returns cliff cells.</summary>
    private static int Terraces(WorldPreset preset, int[] heights, int w, int d, ulong seed)
    {
        // Terraces (and so cliffs) only where a coarse mask is high: elsewhere the hills keep walkable
        // slopes, so every terrace can be reached over a ramp.
        var step = preset.Terrain.CliffMinDrop;
        var from = preset.Terrain.SeaLevel + ((preset.Terrain.MaxHeight - preset.Terrain.SeaLevel) * 2 / 5);
        for (var z = 0; z < d; z++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (z * w) + x;
                if (heights[i] > from && ValueNoise(seed ^ 0x7E44AC, 7, x, z, 24) > 600)
                {
                    heights[i] = from + ((heights[i] - from) / step * step);
                }
            }
        }

        var cliffs = 0;
        for (var z = 0; z < d; z++)
        {
            for (var x = 0; x < w; x++)
            {
                if (IsCliffTop(heights, w, d, x, z, step))
                {
                    cliffs++;
                }
            }
        }

        return cliffs;
    }

    private static bool IsCliffTop(int[] heights, int w, int d, int x, int z, int drop)
    {
        foreach (var (dx, dz) in Neighbours4)
        {
            int nx = x + dx, nz = z + dz;
            if (nx >= 0 && nz >= 0 && nx < w && nz < d && heights[(z * w) + x] - heights[(nz * w) + nx] >= drop)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Single rocks: small round bumps on land (R3).</summary>
    private static void Rocks(WorldPreset preset, int[] heights, int w, int d, Pcg32 rng)
    {
        var sea = preset.Terrain.SeaLevel;
        var count = rng.NextInt(preset.Terrain.Rocks.Min, preset.Terrain.Rocks.Max + 1);
        for (var k = 0; k < count; k++)
        {
            int x = rng.NextInt(2, w - 2), z = rng.NextInt(2, d - 2);
            if (heights[(z * w) + x] <= sea + 2)
            {
                continue;
            }

            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    heights[((z + dz) * w) + x + dx] += dx == 0 && dz == 0 ? 6 : 3;
                }
            }
        }
    }

    // ---------------------------------------------------------------- water

    /// <summary>Springs on slopes; streams run down the steepest way; pits fill into lakes (R4).</summary>
    private static void Streams(WorldPreset preset, int[] heights, WaterKind[] water, int[] depth, sbyte[] flow, int w, int d, Pcg32 rng)
    {
        var sea = preset.Terrain.SeaLevel;
        var candidates = new List<int>();
        for (var z = 1; z < d - 1; z++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                var i = (z * w) + x;
                if (water[i] == WaterKind.None && heights[i] >= sea + preset.Water.SpringMinHeight && Slope(heights, w, d, x, z) >= 2)
                {
                    candidates.Add(i);
                }
            }
        }

        var springs = rng.NextInt(preset.Water.Springs.Min, preset.Water.Springs.Max + 1);
        var sides = new List<(int Side, int Main)>();
        for (var s = 0; s < springs && candidates.Count > 0; s++)
        {
            var start = candidates[rng.NextInt(0, candidates.Count)];
            Run(preset, heights, water, depth, flow, w, d, start % w, start / w, sides);
        }

        SideFlow(heights, water, depth, flow, w, d, sides);
    }

    /// <summary>
    /// Streams are two cells wide; once all streams and lakes are in place, each side cell runs the way
    /// its main cell runs, so it belongs to the running stream instead of lying still beside it. Where
    /// that way would lead uphill or onto dry land, it takes its lowest water neighbour that is not
    /// higher (ties by direction order); without one it stays still. Side cells a lake took stay still.
    /// </summary>
    private static void SideFlow(int[] heights, WaterKind[] water, int[] depth, sbyte[] flow, int w, int d, List<(int Side, int Main)> sides)
    {
        foreach (var (side, main) in sides)
        {
            if (flow[side] >= 0 || flow[main] < 0 || water[side] != WaterKind.Fresh || depth[side] != 2)
            {
                continue;
            }

            int sx = side % w, sz = side / w;
            bool Runs(int k)
            {
                int nx = sx + Neighbours8[k].Dx, nz = sz + Neighbours8[k].Dz;
                return nx >= 0 && nz >= 0 && nx < w && nz < d && water[(nz * w) + nx] != WaterKind.None && heights[(nz * w) + nx] <= heights[side];
            }

            if (Runs(flow[main]))
            {
                flow[side] = flow[main];
                continue;
            }

            int best = -1, bestHeight = int.MaxValue;
            for (var k = 0; k < 8; k++)
            {
                var n = ((sz + Neighbours8[k].Dz) * w) + sx + Neighbours8[k].Dx;
                if (Runs(k) && heights[n] < bestHeight)
                {
                    best = k;
                    bestHeight = heights[n];
                }
            }

            flow[side] = (sbyte)best;
        }
    }

    private static void Run(WorldPreset preset, int[] heights, WaterKind[] water, int[] depth, sbyte[] flow, int w, int d, int x, int z, List<(int Side, int Main)> sides)
    {
        var sea = preset.Terrain.SeaLevel;
        for (var guard = 0; guard < w * d; guard++)
        {
            var i = (z * w) + x;
            if (water[i] == WaterKind.Salt)
            {
                return;
            }

            if (water[i] == WaterKind.None)
            {
                // Streams are a little deeper than swamp water (swamp: depth 1), so the biome table tells them apart.
                water[i] = WaterKind.Fresh;
                depth[i] = 2;
            }

            // Steepest descent among the 8 neighbours; ties by direction order (deterministic).
            int best = -1, bestHeight = heights[i];
            for (var k = 0; k < 8; k++)
            {
                int nx = x + Neighbours8[k].Dx, nz = z + Neighbours8[k].Dz;
                if (nx < 0 || nz < 0 || nx >= w || nz >= d)
                {
                    continue;
                }

                if (heights[(nz * w) + nx] < bestHeight)
                {
                    best = k;
                    bestHeight = heights[(nz * w) + nx];
                }
            }

            if (best < 0)
            {
                // A pit: fill it to its spill point as a lake, then continue from the outlet.
                var outlet = Lake(preset, heights, water, depth, flow, w, d, x, z);
                if (outlet < 0)
                {
                    return;
                }

                x = outlet % w;
                z = outlet / w;
                continue;
            }

            flow[i] = (sbyte)best;

            // Streams are two cells wide: the lower side neighbour carries water too; it gets its flow once
            // all streams are in place (SideFlow), so it is drawn as part of the sloped stream.
            int side = -1, sideHeight = int.MaxValue;
            foreach (var (dx, dz) in Neighbours4)
            {
                int sx = x + dx, sz = z + dz;
                if (sx < 0 || sz < 0 || sx >= w || sz >= d || (dx == Neighbours8[best].Dx && dz == Neighbours8[best].Dz))
                {
                    continue;
                }

                var s = (sz * w) + sx;
                if (water[s] == WaterKind.None && heights[s] <= heights[i] + 1 && heights[s] < sideHeight)
                {
                    side = s;
                    sideHeight = heights[s];
                }
            }

            if (side >= 0)
            {
                water[side] = WaterKind.Fresh;
                depth[side] = 2;
                sides.Add((side, i));
            }

            x += Neighbours8[best].Dx;
            z += Neighbours8[best].Dz;
            if (heights[(z * w) + x] < sea)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Fills a pit as a lake (priority flood): cells are taken lowest first from the rim; the water level
    /// rises to the highest cell taken; the first rim cell lower than the level is the outlet, where the
    /// stream continues. Returns the outlet cell or -1.
    /// </summary>
    private static int Lake(WorldPreset preset, int[] heights, WaterKind[] water, int[] depth, sbyte[] flow, int w, int d, int x, int z)
    {
        var start = (z * w) + x;
        var level = heights[start];
        var taken = new List<int>();
        var seen = new HashSet<int> { start };
        var rim = new PriorityQueue<int, (int Height, int Index)>();
        rim.Enqueue(start, (heights[start], start));
        var outlet = -1;
        while (rim.TryDequeue(out var c, out var key) && taken.Count < 6000)
        {
            if (key.Height < level && water[c] != WaterKind.Fresh)
            {
                outlet = c;
                break;
            }

            level = Math.Max(level, key.Height);
            taken.Add(c);
            if (water[c] == WaterKind.Salt)
            {
                break;
            }

            int cx = c % w, cz = c / w;
            foreach (var (dx, dz) in Neighbours4)
            {
                int nx = cx + dx, nz = cz + dz;
                if (nx < 0 || nz < 0 || nx >= w || nz >= d)
                {
                    continue;
                }

                var n = (nz * w) + nx;
                if (seen.Add(n))
                {
                    rim.Enqueue(n, (heights[n], n));
                }
            }
        }

        foreach (var c in taken)
        {
            if (water[c] != WaterKind.Salt && heights[c] <= level)
            {
                water[c] = WaterKind.Fresh;
                depth[c] = Math.Max(depth[c], Math.Max(2, level - heights[c] + 1));

                // A lake lies still, also where an earlier stream ran through it.
                flow[c] = -1;
            }
        }

        _ = preset;
        return outlet;
    }

    private static int[] Moisture(WorldPreset preset, int[] heights, WaterKind[] water, int w, int d, ulong seed)
    {
        var climate = preset.Climate;
        var radius = climate.MoistureWaterRadiusCells;
        var distance = new int[w * d];
        Array.Fill(distance, int.MaxValue);
        var queue = new Queue<int>();
        for (var i = 0; i < w * d; i++)
        {
            if (water[i] == WaterKind.Fresh)
            {
                distance[i] = 0;
                queue.Enqueue(i);
            }
        }

        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (distance[c] >= radius)
            {
                continue;
            }

            int cx = c % w, cz = c / w;
            foreach (var (dx, dz) in Neighbours4)
            {
                int nx = cx + dx, nz = cz + dz;
                if (nx >= 0 && nz >= 0 && nx < w && nz < d && distance[(nz * w) + nx] == int.MaxValue)
                {
                    distance[(nz * w) + nx] = distance[c] + 1;
                    queue.Enqueue((nz * w) + nx);
                }
            }
        }

        var moisture = new int[w * d];
        for (var z = 0; z < d; z++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (z * w) + x;
                if (water[i] != WaterKind.None)
                {
                    moisture[i] = 1000;
                    continue;
                }

                var nearSea = heights[i] < preset.Terrain.SeaLevel + 4 ? 120 : 0;

                var near = distance[i] == int.MaxValue || radius == 0 ? 0 : Math.Max(0, radius - distance[i]) * (1000 - climate.MoistureBasePermille) / radius;
                var valley = heights[i] + 2 < LocalMean(heights, w, d, x, z, 3) ? climate.ValleyMoistureBonusPermille : 0;
                // Noise gives wet zones natural edges instead of even rings around the water.
                var noise = (ValueNoise(seed ^ 0x3015E, 11, x, z, climate.MoistureNoiseWavelengthCells) - 500) * climate.MoistureNoisePermille / 500;
                moisture[i] = Math.Clamp(climate.MoistureBasePermille + (near * 4 / 5) + valley + nearSea + noise, 0, 1000);
            }
        }

        return moisture;
    }

    /// <summary>Flat wet hollows next to fresh water become swamp (shallow fresh water, R4).</summary>
    private static void Swamp(WorldPreset preset, int[] heights, WaterKind[] water, int[] depth, int[] moisture, int w, int d)
    {
        var sea = preset.Terrain.SeaLevel;
        var marked = new List<int>();
        for (var z = 1; z < d - 1; z++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                var i = (z * w) + x;
                if (water[i] == WaterKind.None
                    && heights[i] > sea + 1 && heights[i] <= sea + 40
                    && Slope(heights, w, d, x, z) <= preset.Water.SwampMaxSlope
                    && moisture[i] >= preset.Water.SwampMinMoisturePermille
                    && heights[i] < LocalMean(heights, w, d, x, z, 2))
                {
                    marked.Add(i);
                }
            }
        }

        // Too few flat wet hollows: the lowest flat cell next to fresh water grows into a small swamp.
        if (marked.Count < 80)
        {
            var best = -1;
            for (var z = 2; z < d - 2; z++)
            {
                for (var x = 2; x < w - 2; x++)
                {
                    var i = (z * w) + x;
                    if (water[i] == WaterKind.None && heights[i] > sea + 2 && heights[i] <= sea + 40
                        && Slope(heights, w, d, x, z) <= 4 && NearFresh(water, w, d, x, z, 8) && !NearFresh(water, w, d, x, z, 3)
                        && (best < 0 || heights[i] < heights[best]))
                    {
                        best = i;
                    }
                }
            }

            if (best >= 0)
            {
                int bx = best % w, bz = best / w;
                // A flat hollow: cells around the point up to a little higher are levelled to its height.
                for (var dz = -8; dz <= 8; dz++)
                {
                    for (var dx = -8; dx <= 8; dx++)
                    {
                        int px = bx + dx, pz = bz + dz;
                        if (px < 0 || pz < 0 || px >= w || pz >= d)
                        {
                            continue;
                        }

                        var i = (pz * w) + px;
                        if ((dx * dx) + (dz * dz) <= 64 && water[i] == WaterKind.None && heights[i] <= heights[best] + 10)
                        {
                            heights[i] = heights[best];
                            marked.Add(i);
                        }
                    }
                }
            }
        }

        foreach (var i in marked)
        {
            water[i] = WaterKind.Fresh;
            depth[i] = 1;
            moisture[i] = 1000;
        }
    }

    // ---------------------------------------------------------------- cells

    private static byte Biome(BiomeTable table, IReadOnlyList<string> ids, int heightAboveSea, WaterKind water, int depth, int moisture, int temperature)
    {
        foreach (var rule in table.Rules)
        {
            var waterOk = rule.Water switch
            {
                BiomeRuleWater.None => water == WaterKind.None,
                BiomeRuleWater.Fresh => water == WaterKind.Fresh,
                BiomeRuleWater.Salt => water == WaterKind.Salt,
                _ => true,
            };
            if (waterOk && (water == WaterKind.None || depth <= rule.MaxWaterDepth)
                && heightAboveSea >= rule.MinHeight && heightAboveSea <= rule.MaxHeight
                && moisture >= rule.MinMoisturePermille && moisture <= rule.MaxMoisturePermille
                && temperature >= rule.MinTemperatureTenthC && temperature <= rule.MaxTemperatureTenthC)
            {
                return (byte)IndexOf(ids, rule.Biome);
            }
        }

        return (byte)IndexOf(ids, table.FallbackBiome);
    }

    private static int WalkCost(WorldPreset preset, BiomeTable table, string biome, int[] heights, WaterKind[] water, int[] depth, int w, int d, int x, int z)
    {
        var i = (z * w) + x;
        // A cell is an obstacle where it stands on a steep edge: higher than its lowest neighbour by more
        // than the walkable slope. The foot of a cliff stays walkable (caves are there), the top edge not.
        var slope = Slope(heights, w, d, x, z);
        var drop = heights[i] - LowestNeighbour(heights, w, d, x, z);
        if (drop > preset.Terrain.WalkableSlope || (water[i] != WaterKind.None && depth[i] > preset.Water.ShallowMaxDepth))
        {
            return MapCell.Blocked;
        }

        var cost = table.Biomes.First(b => string.Equals(b.Id, biome, StringComparison.Ordinal)).WalkCostPermille;
        cost += slope * preset.Pathfinding.CostPerSlopeUnit * 100 / Math.Max(1, preset.Pathfinding.CostFlat);
        if (water[i] != WaterKind.None)
        {
            cost += preset.Pathfinding.CostShallowWater * 100 / Math.Max(1, preset.Pathfinding.CostFlat);
        }

        return cost;
    }

    // ---------------------------------------------------------------- objects and starts

    /// <summary>
    /// Start favour (TL #19280): every start position finds the listed resources within the radius;
    /// missing ones are placed there from the island's own spawn types that have the wanted true
    /// properties (never by type id), on free walkable dry cells chosen by the generator stream.
    /// </summary>
    private static void StartFavor(StartFavor favor, SpawnTable spawns, ObjectCatalog catalog, WorldMap map, List<IslandObject> objects, List<StartPosition> starts, Pcg32 rng)
    {
        var islandTypes = spawns.Spawns.Select(s => s.ObjectType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var r2 = favor.RadiusCells * favor.RadiusCells;
        foreach (var start in starts)
        {
            foreach (var resource in favor.Resources)
            {
                bool Matches(string type) =>
                    resource.PropertiesAll.All(p => catalog.HasProperty(type, p)) && !resource.PropertiesNone.Any(p => catalog.HasProperty(type, p));
                bool Near(int x, int z) => ((x - start.X) * (x - start.X)) + ((z - start.Z) * (z - start.Z)) <= r2;

                var have = objects.Count(o => Near(o.X, o.Z) && Matches(o.TypeId));
                var candidates = islandTypes.Where(Matches).ToList();
                if (have >= resource.Min || candidates.Count == 0)
                {
                    continue;
                }

                var taken = objects.Select(o => (o.X, o.Z)).ToHashSet();
                var cells = new List<(int X, int Z)>();
                for (var z = Math.Max(0, start.Z - favor.RadiusCells); z <= Math.Min(map.DepthCells - 1, start.Z + favor.RadiusCells); z++)
                {
                    for (var x = Math.Max(0, start.X - favor.RadiusCells); x <= Math.Min(map.WidthCells - 1, start.X + favor.RadiusCells); x++)
                    {
                        var c = map.Cell(x, z);
                        if (Near(x, z) && c.Water == WaterKind.None && c.WalkCost != MapCell.Blocked && !taken.Contains((x, z)) && (x, z) != (start.X, start.Z))
                        {
                            cells.Add((x, z));
                        }
                    }
                }

                for (var k = have; k < resource.Min && cells.Count > 0; k++)
                {
                    var pick = rng.NextInt(0, cells.Count);
                    var type = candidates[rng.NextInt(0, candidates.Count)];
                    objects.Add(new IslandObject(type, cells[pick].X, cells[pick].Z));
                    cells.RemoveAt(pick);
                }
            }
        }
    }

    /// <summary>Caves: rock niches at the foot of cliffs in rocky hills (R6, B2514 T3).</summary>
    private static void Caves(WorldPreset preset, WorldMap map, List<IslandObject> objects, Pcg32 rng)
    {
        var candidates = new List<(int X, int Z)>();
        for (var z = 1; z < map.DepthCells - 1; z++)
        {
            for (var x = 1; x < map.WidthCells - 1; x++)
            {
                var c = map.Cell(x, z);
                if (c.WalkCost == MapCell.Blocked || c.Water != WaterKind.None)
                {
                    continue;
                }

                foreach (var (dx, dz) in Neighbours4)
                {
                    var n = map.Cell(x + dx, z + dz);
                    if (n.Height - c.Height >= preset.Terrain.CliffMinDrop && map.BiomeIds[n.Biome] == preset.Caves.CliffBiome)
                    {
                        candidates.Add((x, z));
                        break;
                    }
                }
            }
        }

        var count = Math.Min(candidates.Count, rng.NextInt(preset.Caves.Min, preset.Caves.Max + 1));
        for (var k = 0; k < count; k++)
        {
            var pick = rng.NextInt(0, candidates.Count);
            objects.Add(new IslandObject(preset.Caves.ObjectType, candidates[pick].X, candidates[pick].Z));
            candidates.RemoveAt(pick);
        }
    }

    /// <summary>
    /// Objects by biome × type with densities, clusters and upper limits (R8). Candidate cells of a biome
    /// are its dry walkable cells; a water biome (stream, lake, swamp) uses its banks, and if it has
    /// none, its shallow water (reed in the swamp, clay at the stream).
    /// </summary>
    private static void Spawn(SpawnTable spawns, WorldMap map, List<IslandObject> objects, Pcg32 rng)
    {
        var dry = new Dictionary<string, List<(int X, int Z)>>(StringComparer.Ordinal);
        var bank = new Dictionary<string, List<(int X, int Z)>>(StringComparer.Ordinal);
        var shallow = new Dictionary<string, List<(int X, int Z)>>(StringComparer.Ordinal);
        static void Add(Dictionary<string, List<(int X, int Z)>> into, string biome, (int X, int Z) cell)
        {
            if (!into.TryGetValue(biome, out var list))
            {
                into[biome] = list = [];
            }

            if (list.Count == 0 || list[^1] != cell)
            {
                list.Add(cell);
            }
        }

        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                var c = map.Cell(x, z);
                if (c.WalkCost == MapCell.Blocked)
                {
                    continue;
                }

                var biome = map.BiomeIds[c.Biome];
                if (c.Water == WaterKind.None)
                {
                    Add(dry, biome, (x, z));
                    foreach (var (dx, dz) in Neighbours4)
                    {
                        if (map.Contains(x + dx, z + dz) && map.Cell(x + dx, z + dz).Water != WaterKind.None)
                        {
                            Add(bank, map.BiomeIds[map.Cell(x + dx, z + dz).Biome], (x, z));
                        }
                    }
                }
                else
                {
                    Add(shallow, biome, (x, z));
                }
            }
        }

        var byBiome = new Dictionary<string, List<(int X, int Z)>>(StringComparer.Ordinal);
        foreach (var biome in map.BiomeIds)
        {
            byBiome[biome] = dry.GetValueOrDefault(biome) is { Count: > 0 } d ? d
                : bank.GetValueOrDefault(biome) is { Count: > 0 } b ? b
                : shallow.GetValueOrDefault(biome) ?? [];
        }

        var taken = new HashSet<(int, int)>(objects.Select(o => (o.X, o.Z)));
        foreach (var rule in spawns.Spawns)
        {
            if (!byBiome.TryGetValue(rule.Biome, out var cells) || cells.Count == 0)
            {
                continue;
            }

            var allowed = cells.ToHashSet();
            var target = Math.Min(rule.MaxTotal, Math.Max(rule.Per1000Cells > 0 ? 1 : 0, cells.Count * rule.Per1000Cells / 1000));
            var placed = 0;
            for (var guard = 0; placed < target && guard < target * 20; guard++)
            {
                var (cx, cz) = cells[rng.NextInt(0, cells.Count)];
                var size = rng.NextInt(rule.Cluster.Min, rule.Cluster.Max + 1);
                for (var k = 0; k < size && placed < target; k++)
                {
                    int x = cx + rng.NextInt(-2, 3), z = cz + rng.NextInt(-2, 3);
                    if (!map.Contains(x, z) || taken.Contains((x, z)))
                    {
                        continue;
                    }

                    if (!allowed.Contains((x, z)))
                    {
                        continue;
                    }

                    taken.Add((x, z));
                    objects.Add(new IslandObject(rule.ObjectType, x, z));
                    placed++;
                }
            }
        }
    }

    /// <summary>Pairs (woman and man) on different beaches, far enough apart not to see each other (R9, B2517).</summary>
    private static List<StartPosition> Starts(WorldPreset preset, WorldMap map, Pcg32 rng)
    {
        var beach = new List<(int X, int Z)>();
        for (var z = 1; z < map.DepthCells - 1; z++)
        {
            for (var x = 1; x < map.WidthCells - 1; x++)
            {
                var c = map.Cell(x, z);
                if (c.Water == WaterKind.None && c.WalkCost != MapCell.Blocked && map.BiomeIds[c.Biome] == preset.Start.Biome && NearSalt(map, x, z, 3))
                {
                    beach.Add((x, z));
                }
            }
        }

        var starts = new List<StartPosition>();
        var anchors = new List<(int X, int Z)>();
        for (var pair = 0; pair < preset.Start.Pairs && beach.Count > 0; pair++)
        {
            var options = beach.Where(b => anchors.All(a => Within(a, b, preset.Start.MinPairDistanceCells, preset.Start.MaxPairDistanceCells))).ToList();
            if (options.Count == 0)
            {
                break;
            }

            var anchor = options[rng.NextInt(0, options.Count)];
            var partner = beach.Where(b => b != anchor && Math.Abs(b.X - anchor.X) <= 2 && Math.Abs(b.Z - anchor.Z) <= 2).ToList();
            if (partner.Count == 0)
            {
                continue;
            }

            anchors.Add(anchor);
            var second = partner[rng.NextInt(0, partner.Count)];
            starts.Add(new StartPosition(pair, anchor.X, anchor.Z));
            starts.Add(new StartPosition(pair, second.X, second.Z));
        }

        return starts;
    }

    private static List<NeighbourIsland> Neighbour(WorldPreset preset, int w, int d, Pcg32 rng)
    {
        var count = rng.NextInt(preset.Neighbours.Islands.Min, preset.Neighbours.Islands.Max + 1);
        var list = new List<NeighbourIsland>();
        for (var k = 0; k < count; k++)
        {
            // Placed on a ring around the start island, beyond the cloud cover (contact and boats: B9).
            var side = rng.NextInt(0, 4);
            var along = rng.NextInt(0, Math.Max(w, d));
            var away = preset.Neighbours.MinDistanceCells;
            var (x, z) = side switch
            {
                0 => (-away, along),
                1 => (w + away, along),
                2 => (along, -away),
                _ => (along, d + away),
            };
            list.Add(new NeighbourIsland(x, z, rng.NextInt(preset.Neighbours.GroupSize.Min, preset.Neighbours.GroupSize.Max + 1)));
        }

        return list;
    }

    // ---------------------------------------------------------------- mandatory check (R7)

    private static List<MandatoryResult> Check(SpawnTable spawns, ObjectCatalog catalog, WorldMap map, List<IslandObject> objects, List<StartPosition> starts)
    {
        var reach = starts.Count == 0 ? new bool[map.WidthCells * map.DepthCells] : Reachable(map, starts[0].X, starts[0].Z);
        var results = new List<MandatoryResult>();
        foreach (var m in spawns.Mandatory)
        {
            int reachable = 0, total = 0;
            if (m.Source == MandatoryResourceSource.Fresh_water)
            {
                for (var z = 0; z < map.DepthCells; z++)
                {
                    for (var x = 0; x < map.WidthCells; x++)
                    {
                        if (map.Cell(x, z).Water != WaterKind.Fresh)
                        {
                            continue;
                        }

                        total++;
                        if (reach[(z * map.WidthCells) + x] || NextToReachable(map, reach, x, z))
                        {
                            reachable++;
                        }
                    }
                }
            }
            else
            {
                foreach (var o in objects)
                {
                    if (!m.PropertiesAll.All(p => catalog.HasProperty(o.TypeId, p)) || m.PropertiesNone.Any(p => catalog.HasProperty(o.TypeId, p)))
                    {
                        continue;
                    }

                    total++;
                    if (reach[(o.Z * map.WidthCells) + o.X])
                    {
                        reachable++;
                    }
                }
            }

            results.Add(new MandatoryResult(m.Id, reachable, total, m.Min, m.Max));
        }

        return results;
    }

    /// <summary>Cells reachable on foot (no deep water, no steep slopes) from (x, z).</summary>
    public static bool[] Reachable(WorldMap map, int x, int z)
    {
        ArgumentNullException.ThrowIfNull(map);
        var reach = new bool[map.WidthCells * map.DepthCells];
        if (!map.Contains(x, z) || map.Cell(x, z).WalkCost == MapCell.Blocked)
        {
            return reach;
        }

        var queue = new Queue<(int X, int Z)>();
        queue.Enqueue((x, z));
        reach[(z * map.WidthCells) + x] = true;
        while (queue.Count > 0)
        {
            var (cx, cz) = queue.Dequeue();
            foreach (var (dx, dz) in Neighbours4)
            {
                int nx = cx + dx, nz = cz + dz;
                if (map.Contains(nx, nz) && !reach[(nz * map.WidthCells) + nx] && map.Cell(nx, nz).WalkCost != MapCell.Blocked)
                {
                    reach[(nz * map.WidthCells) + nx] = true;
                    queue.Enqueue((nx, nz));
                }
            }
        }

        return reach;
    }

    private static bool Connected(WorldMap map, List<StartPosition> starts)
    {
        if (starts.Count == 0)
        {
            return false;
        }

        var reach = Reachable(map, starts[0].X, starts[0].Z);
        return starts.All(s => reach[(s.Z * map.WidthCells) + s.X]);
    }

    private static bool NextToReachable(WorldMap map, bool[] reach, int x, int z)
    {
        foreach (var (dx, dz) in Neighbours4)
        {
            if (map.Contains(x + dx, z + dz) && reach[((z + dz) * map.WidthCells) + x + dx])
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- helpers

    private static bool NearSalt(WorldMap map, int x, int z, int radius)
    {
        for (var dz = -radius; dz <= radius; dz++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (map.Contains(x + dx, z + dz) && map.Cell(x + dx, z + dz).Water == WaterKind.Salt)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Within((int X, int Z) a, (int X, int Z) b, int min, int max)
    {
        var d2 = ((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z));
        return d2 >= min * min && d2 <= max * max;
    }

    private static int Slope(int[] heights, int w, int d, int x, int z)
    {
        var h = heights[(z * w) + x];
        var slope = 0;
        foreach (var (dx, dz) in Neighbours4)
        {
            int nx = x + dx, nz = z + dz;
            if (nx >= 0 && nz >= 0 && nx < w && nz < d)
            {
                slope = Math.Max(slope, Math.Abs(heights[(nz * w) + nx] - h));
            }
        }

        return slope;
    }

    private static bool NearFresh(WaterKind[] water, int w, int d, int x, int z, int radius)
    {
        for (var dz = -radius; dz <= radius; dz++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                int nx = x + dx, nz = z + dz;
                if (nx >= 0 && nz >= 0 && nx < w && nz < d && water[(nz * w) + nx] == WaterKind.Fresh)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int LowestNeighbour(int[] heights, int w, int d, int x, int z)
    {
        var lowest = heights[(z * w) + x];
        foreach (var (dx, dz) in Neighbours4)
        {
            int nx = x + dx, nz = z + dz;
            if (nx >= 0 && nz >= 0 && nx < w && nz < d)
            {
                lowest = Math.Min(lowest, heights[(nz * w) + nx]);
            }
        }

        return lowest;
    }

    private static int LocalMean(int[] heights, int w, int d, int x, int z, int radius)
    {
        long sum = 0;
        var n = 0;
        for (var dz = -radius; dz <= radius; dz++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                int nx = x + dx, nz = z + dz;
                if (nx >= 0 && nz >= 0 && nx < w && nz < d)
                {
                    sum += heights[(nz * w) + nx];
                    n++;
                }
            }
        }

        return (int)(sum / Math.Max(1, n));
    }

    private static int IndexOf(IReadOnlyList<string> ids, string id)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            if (string.Equals(ids[i], id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidDataException($"biome '{id}' not in the biome list");
    }

    /// <summary>Integer value noise in 0..1000 with smoothstep interpolation between lattice points.</summary>
    private static int ValueNoise(ulong seed, int octave, int x, int z, int wavelength)
    {
        int ix = x / wavelength, iz = z / wavelength;
        var fx = (x % wavelength) * 1000 / wavelength;
        var fz = (z % wavelength) * 1000 / wavelength;
        var sx = fx * fx * (3000 - (2 * fx)) / 1_000_000;
        var sz = fz * fz * (3000 - (2 * fz)) / 1_000_000;
        int a = Lattice(seed, octave, ix, iz), b = Lattice(seed, octave, ix + 1, iz);
        int c = Lattice(seed, octave, ix, iz + 1), e = Lattice(seed, octave, ix + 1, iz + 1);
        var top = a + ((b - a) * sx / 1000);
        var bottom = c + ((e - c) * sx / 1000);
        return top + ((bottom - top) * sz / 1000);
    }

    private static int Lattice(ulong seed, int octave, int ix, int iz) =>
        (int)(Mix(seed ^ Mix(((ulong)(uint)octave << 48) ^ ((ulong)(uint)ix << 24) ^ (ulong)(uint)iz)) % 1001);

    private static Pcg32 Stream(ulong seed, string name)
    {
        ulong h = 14695981039346656037;
        foreach (var ch in name)
        {
            h = (h ^ ch) * 1099511628211;
        }

        return new Pcg32(Mix(seed ^ h), Mix(h));
    }

    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
        return z ^ (z >> 31);
    }

    private static int Isqrt(int n)
    {
        if (n <= 0)
        {
            return 0;
        }

        var r = n;
        var y = (r + 1) / 2;
        while (y < r)
        {
            r = y;
            y = (r + (n / r)) / 2;
        }

        return r;
    }

}
