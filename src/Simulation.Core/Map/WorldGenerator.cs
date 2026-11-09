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
    /// <summary>Id prefix of lines that only report (such as the start supply) and do not decide whether the island passes.</summary>
    public const string ReportPrefix = "report/";

    /// <summary>Whether this line only reports (its id starts with <see cref="ReportPrefix"/>); kept in the id, so saved islands keep it.</summary>
    public bool ReportOnly => Id.StartsWith(ReportPrefix, StringComparison.Ordinal);

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
public static partial class WorldGenerator
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
        var cliffs = Terraces(preset, heights, w, d, attemptSeed, RockHeight(content.Biomes, preset.Caves.CliffBiome));
        Rocks(preset, heights, w, d, Stream(attemptSeed, "worldgen/rocks"));

        var water = new WaterKind[w * d];
        var depth = new int[w * d];
        var flow = new sbyte[w * d];
        Array.Fill(flow, (sbyte)-1);
        Sea(sea, heights, water, depth, w, d);

        // The groundwater table comes first: springs rise where it meets the ground (TL #19717, #19722).
        var table = GroundwaterTable(preset, heights, water, w, d, attemptSeed);
        var groundwater = new int[w * d];
        for (var i = 0; i < w * d; i++)
        {
            groundwater[i] = heights[i] - table[i];
        }

        var drainage = Hydrology(preset, heights, groundwater, water, depth, flow, w, d, attemptSeed);
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
                biomeOf[i] = Biome(content.Biomes, biomeIds, heights[i] - sea, Slope(heights, w, d, x, z), water[i], depth[i], moisture[i], temperature);
                walk[i] = WalkCost(preset, content.Biomes, biomeIds[biomeOf[i]], heights, water, depth, w, d, x, z);
                builder.Set(x, z, new MapCell(heights[i], water[i], depth[i], flow[i], moisture[i], temperature, biomeOf[i], walk[i]));
            }
        }

        builder.Hydrology = new HydrologyLayer(w, d, drainage.Accumulation, drainage.WidthCm, drainage.Features);
        builder.Underground = Underground(preset, heights, water, moisture, table, w, d, attemptSeed);
        var map = builder.Build();
        var objects = new List<IslandObject>();
        Caves(preset, map, objects, Stream(attemptSeed, "worldgen/caves"));
        DrinkSpots(preset, map, objects, Stream(attemptSeed, "worldgen/drink"));
        Spawn(content.Spawns, map, objects, Stream(attemptSeed, "worldgen/spawns"));
        var supply = preset.Start.Supply;
        var feasible = SupplyFeasible(supply, content.Spawns, catalog, map, objects);

        // Close to the springs first (TL #19722): the mandatory resources within the smaller radius.
        var near = supply.PreferRadiusCells is { } prefer
            ? SupplyFeasible(new StartSupply { RadiusCells = prefer, Resources = [.. supply.Resources.Where(r => r.Mandatory == true)] }, content.Spawns, catalog, map, objects)
            : null;
        var starts = Starts(preset, map, feasible, near, Stream(attemptSeed, "worldgen/start"));
        StartSupply(preset.Start.Supply, content.Spawns, catalog, map, objects, starts, Stream(attemptSeed, "worldgen/supply"));
        QuietStarts(preset, catalog, objects, starts, Stream(attemptSeed, "worldgen/quiet"));
        var neighbours = Neighbour(preset, w, d, Stream(attemptSeed, "worldgen/neighbours"));
        var check = Check(content.Spawns, catalog, map, objects, starts);
        var present = new bool[biomeIds.Count];
        foreach (var b in biomeOf)
        {
            present[b] = true;
        }

        // The start supply joins the check: a mandatory resource (drink) that a start misses fails the
        // attempt, so the next derived sub-seed is tried; the others only report (TL #19553, GW3 #120).
        check.AddRange(SupplyCheck(preset.Start.Supply, catalog, map, objects, starts));
        var passed = starts.Count == preset.Start.Pairs * 2 && check.All(c => c.Passed || c.ReportOnly) && Connected(map, starts) && present.All(p => p);
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
        for (var z = 0; z < d; z++)
        {
            for (var x = 0; x < w; x++)
            {
                var falloff = CoastFalloff(terrain, seed, x, z);
                var raw = 0;
                for (var o = 0; o < octaves.Count; o++)
                {
                    raw += Octave(octaves[o], ValueNoise(seed, o, x, z, octaves[o].WavelengthCells));
                }

                // Distance from the centre in permille of the half size (0 centre, 1000 at the edge midpoints).
                var dx = Math.Abs((2 * x) + 1 - w) * 1000 / w;
                var dz = Math.Abs((2 * z) + 1 - d) * 1000 / d;
                var r = Isqrt((dx * dx) + (dz * dz));
                r += (ValueNoise(seed ^ 0xC0A57, 9, x, z, 40) - 500) * 2 / 5;
                var fall = falloff == 0 ? (r >= 1000 ? 1000 : 0) : Math.Clamp((r - (1000 - falloff)) * 1000 / falloff, 0, 1000);
                var h = (raw + terrain.SeaLevel) * (1000 - fall) / 1000;

                // The map edge is always sea, so every stream finds the sea as its outlet.
                var edge = x == 0 || z == 0 || x == w - 1 || z == d - 1;
                heights[(z * w) + x] = Math.Clamp(edge ? Math.Min(h, terrain.SeaLevel - 1) : h, 0, terrain.MaxHeight);
            }
        }

        return heights;
    }

    /// <summary>
    /// The coast falloff band at a cell (permille of the half width): the preset's, narrowed towards the
    /// steep coast's where the coast noise lies above its threshold (TL #19990: cliffs at the coast),
    /// blended over the data range so the coast changes gradually.
    /// </summary>
    private static int CoastFalloff(WorldPresetTerrain terrain, ulong seed, int x, int z)
    {
        if (terrain.CoastCliffs is not { } cliffs)
        {
            return terrain.CoastFalloffPermille;
        }

        var noise = ValueNoise(seed ^ 0xC11FF5, 10, x, z, cliffs.NoiseWavelengthCells);
        var steep = Math.Clamp((noise - cliffs.ThresholdPermille) * 1000 / cliffs.BlendPermille, 0, 1000);
        return terrain.CoastFalloffPermille + ((cliffs.FalloffPermille - terrain.CoastFalloffPermille) * steep / 1000);
    }

    /// <summary>
    /// One octave's contribution: plain 0..amplitude; signed around zero (digs hollows as well as raising
    /// hills); ridged folds the noise at its middle and squares it, so it adds sharp crests.
    /// </summary>
    private static int Octave(WorldPresetNoiseOctave octave, int noise)
    {
        if (octave.Ridged == true)
        {
            var fold = 1000 - Math.Abs((2 * noise) - 1000);
            return octave.Amplitude * (fold * fold / 1000) / 1000;
        }

        return octave.Signed == true ? octave.Amplitude * (noise - 500) / 1000 : octave.Amplitude * noise / 1000;
    }

    /// <summary>
    /// The height above sea from which the cliff biome takes ground by height alone (its rule without a
    /// slope condition); null if the table has none.
    /// </summary>
    private static int? RockHeight(BiomeTable table, string cliffBiome) =>
        table.Rules.Where(r => r.Biome == cliffBiome && r.Water == BiomeRuleWater.None && (r.MinSlope ?? 0) == 0)
            .Select(r => (int?)r.MinHeight).Min();

    /// <summary>
    /// Rock ground becomes terraces of cliff height, so the rocky hills get cliffs (R3, D576). Only where
    /// the cliff biome takes the ground by its height (TL #19722: no terrace rings on meadow hills; without
    /// such a rule, from two fifths of the height range) and where a coarse mask is high; elsewhere the
    /// hills keep smooth walkable slopes, so every terrace can be reached over a ramp. Returns cliff cells.
    /// </summary>
    private static int Terraces(WorldPreset preset, int[] heights, int w, int d, ulong seed, int? rockHeight)
    {
        var step = preset.Terrain.CliffMinDrop;
        var from = preset.Terrain.SeaLevel + (rockHeight ?? ((preset.Terrain.MaxHeight - preset.Terrain.SeaLevel) * 2 / 5));
        var tread = new int[w * d];
        var terraced = new bool[w * d];
        for (var z = 0; z < d; z++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (z * w) + x;
                if (heights[i] > from && ValueNoise(seed ^ 0x7E44AC, 7, x, z, 24) > 600)
                {
                    tread[i] = (heights[i] - from) % step;
                    heights[i] -= tread[i];
                    terraced[i] = true;
                }
            }
        }

        SmoothShortCliffs(heights, w, d, step, preset.Terrain.CliffMinRunCells, preset.Terrain.CliffSmoothingRounds);
        if (preset.Terrain.RockSlopes is { } slopes)
        {
            NaturalRock(heights, tread, terraced, w, d, step, slopes);
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

    /// <summary>
    /// Cliff steps shorter than the data run (4-connected cliff-top cells) become walkable slopes: their
    /// cells come down to one below the cliff drop above their lowest neighbour, for the data's rounds, so the walls
    /// that stay follow the terrace edges instead of standing as isolated teeth (TL #19722 polish).
    /// </summary>
    private static void SmoothShortCliffs(int[] heights, int w, int d, int step, int minRun, int rounds)
    {
        for (var round = 0; round < rounds; round++)
        {
            var top = new bool[w * d];
            for (var z = 0; z < d; z++)
            {
                for (var x = 0; x < w; x++)
                {
                    top[(z * w) + x] = IsCliffTop(heights, w, d, x, z, step);
                }
            }

            var changed = false;
            foreach (var run in Components4(top, w, d).Where(r => r.Count < minRun))
            {
                foreach (var c in run)
                {
                    var lowest = LowestNeighbour(heights, w, d, c % w, c / w);
                    if (heights[c] - lowest >= step)
                    {
                        heights[c] = lowest + step - 1;
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Natural rock (TL #19984, #19990): the longest terrace walls (cliff-top runs) stay sheer with their foot, so
    /// caves keep their place; every other terrace tread gets back the data share of its original relief
    /// (no flat plateau), and every step steeper than the talus settles into a scree slope.
    /// </summary>
    private static void NaturalRock(int[] heights, int[] tread, bool[] terraced, int w, int d, int step, WorldPresetRockSlopes slopes)
    {
        var top = new bool[w * d];
        for (var z = 0; z < d; z++)
        {
            for (var x = 0; x < w; x++)
            {
                top[(z * w) + x] = terraced[(z * w) + x] && IsCliffTop(heights, w, d, x, z, step);
            }
        }

        var kept = new bool[w * d];
        foreach (var run in Components4(top, w, d).OrderByDescending(r => r.Count).ThenBy(r => r[0]).Take(slopes.WallsKept))
        {
            foreach (var c in run)
            {
                kept[c] = true;
                foreach (var (dx, dz) in Neighbours4)
                {
                    int nx = (c % w) + dx, nz = (c / w) + dz;
                    if (nx >= 0 && nz >= 0 && nx < w && nz < d && heights[c] - heights[(nz * w) + nx] >= step)
                    {
                        kept[(nz * w) + nx] = true;
                    }
                }
            }
        }

        for (var i = 0; i < heights.Length; i++)
        {
            if (!kept[i])
            {
                heights[i] += tread[i] * slopes.TreadReliefPermille / 1000;
            }
        }

        Settle(heights, kept, w, d, slopes.Talus, slopes.Rounds);
    }

    /// <summary>
    /// Scree slopes (thermal erosion, TL #19984): for the given rounds, every pair of 4-neighbours steeper
    /// than the talus passes a quarter of the excess (at least 1) from the higher to the lower cell, all
    /// pairs of a round at once (order-free, integer). Fixed cells (the kept walls) and the map edge keep
    /// their height.
    /// </summary>
    private static void Settle(int[] heights, bool[] fixedCells, int w, int d, int talus, int rounds)
    {
        var delta = new int[w * d];
        for (var round = 0; round < rounds; round++)
        {
            Array.Clear(delta);
            var changed = false;
            for (var z = 1; z < d - 1; z++)
            {
                for (var x = 1; x < w - 1; x++)
                {
                    var i = (z * w) + x;
                    if (fixedCells[i])
                    {
                        continue;
                    }

                    foreach (var j in (ReadOnlySpan<int>)[i + 1, i + w])
                    {
                        var jx = j % w;
                        if (fixedCells[j] || jx == w - 1 || j / w == d - 1)
                        {
                            continue;
                        }

                        var excess = Math.Abs(heights[i] - heights[j]) - talus;
                        if (excess <= 0)
                        {
                            continue;
                        }

                        var move = (excess + 3) / 4;
                        var (high, low) = heights[i] > heights[j] ? (i, j) : (j, i);
                        delta[high] -= move;
                        delta[low] += move;
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                return;
            }

            for (var i = 0; i < delta.Length; i++)
            {
                heights[i] += delta[i];
            }
        }
    }

    /// <summary>Connected groups (4 neighbours) of marked cells.</summary>
    private static List<List<int>> Components4(bool[] marked, int w, int d)
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
            var queue = new Queue<int>([i]);
            seen[i] = true;
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                group.Add(c);
                foreach (var (dx, dz) in Neighbours4)
                {
                    int nx = (c % w) + dx, nz = (c / w) + dz;
                    if (nx >= 0 && nz >= 0 && nx < w && nz < d && marked[(nz * w) + nx] && !seen[(nz * w) + nx])
                    {
                        seen[(nz * w) + nx] = true;
                        queue.Enqueue((nz * w) + nx);
                    }
                }
            }

            groups.Add(group);
        }

        return groups;
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

    private static byte Biome(BiomeTable table, IReadOnlyList<string> ids, int heightAboveSea, int slope, WaterKind water, int depth, int moisture, int temperature)
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
                && heightAboveSea >= rule.MinHeight && heightAboveSea <= rule.MaxHeight && slope >= (rule.MinSlope ?? 0)
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
    /// Start supply (TL #19280): every start position finds the listed resources within the radius and
    /// within its walkable area. Missing ones are placed there by the island's own spawn rules whose type
    /// has the wanted true properties (never by type id): only on the cells where that rule spawns (its
    /// biome), only while the rule stays within its max_total, preferably on cells the start can walk
    /// to, chosen by the generator stream. Where nothing fits within the radius it stays missing and the
    /// mandatory check reports it.
    /// </summary>
    private static void StartSupply(StartSupply supply, SpawnTable spawns, ObjectCatalog catalog, WorldMap map, List<IslandObject> objects, List<StartPosition> starts, Pcg32 rng)
    {
        var areas = PathFinder.WalkableAreas(map);
        var w = map.WidthCells;
        var cellsOf = SpawnCells(map);
        var rules = spawns.Spawns.ToList();

        // Room left in each rule: its max_total minus the objects of its type already on its cells.
        var room = rules.Select(rule =>
        {
            var cells = cellsOf.GetValueOrDefault(rule.Biome) ?? [];
            var on = cells.ToHashSet();
            return rule.MaxTotal - objects.Count(o => o.TypeId == rule.ObjectType && on.Contains((o.X, o.Z)));
        }).ToArray();
        var r2 = supply.RadiusCells * supply.RadiusCells;
        foreach (var start in starts)
        {
            var area = areas[(start.Z * w) + start.X];
            bool Near(int x, int z) => ((x - start.X) * (x - start.X)) + ((z - start.Z) * (z - start.Z)) <= r2;
            bool Reachable(int x, int z) => area != 0 && areas[(z * w) + x] == area;
            foreach (var resource in supply.Resources)
            {
                bool Matches(string type) =>
                    resource.PropertiesAll.All(p => catalog.HasProperty(type, p)) && !resource.PropertiesNone.Any(p => catalog.HasProperty(type, p));

                var have = objects.Count(o => Near(o.X, o.Z) && Reachable(o.X, o.Z) && Matches(o.TypeId));
                if (have >= resource.Min)
                {
                    continue;
                }

                // Options: (rule, cell) for every matching rule with room, on its cells within the radius.
                var taken = objects.Select(o => (o.X, o.Z)).ToHashSet();
                var options = new List<(int Rule, int X, int Z)>();
                for (var r = 0; r < rules.Count; r++)
                {
                    if (room[r] <= 0 || !Matches(rules[r].ObjectType) || cellsOf.GetValueOrDefault(rules[r].Biome) is not { } cells)
                    {
                        continue;
                    }

                    options.AddRange(cells
                        .Where(c => Near(c.X, c.Z) && !taken.Contains(c) && c != (start.X, start.Z))
                        .Select(c => (r, c.X, c.Z)));
                }

                // Cells the start can walk to come first; others within sight only if there are none.
                var pool = options.Where(o => Reachable(o.X, o.Z)).ToList();
                if (pool.Count == 0)
                {
                    pool = options;
                }

                for (var k = have; k < resource.Min && pool.Count > 0; k++)
                {
                    var (rule, x, z) = pool[rng.NextInt(0, pool.Count)];
                    objects.Add(new IslandObject(rules[rule].ObjectType, x, z));
                    room[rule]--;
                    pool.RemoveAll(o => (o.X == x && o.Z == z) || room[o.Rule] <= 0);
                }
            }
        }
    }

    /// <summary>
    /// A quieter start (data, TL #19722 polish): within the quiet radius of a start only the data share of
    /// the objects that serve no start supply resource stays (chosen by the generator stream); caves and
    /// drinking water stay. The inhabitants meet fewer unknown things at once; the rest keeps the plenty.
    /// </summary>
    private static void QuietStarts(WorldPreset preset, ObjectCatalog catalog, List<IslandObject> objects, List<StartPosition> starts, Pcg32 rng)
    {
        var supply = preset.Start.Supply;
        if (supply.QuietRadiusCells is not { } radius || starts.Count == 0)
        {
            return;
        }

        var keep = supply.QuietKeepPermille ?? 1000;
        var kept = preset.Water.DrinkSpots.Select(s => s.ObjectType).Append(preset.Caves.ObjectType).ToHashSet(StringComparer.Ordinal);
        bool Serves(string type) => supply.Resources.Any(r => r.PropertiesAll.All(p => catalog.HasProperty(type, p)) && !r.PropertiesNone.Any(p => catalog.HasProperty(type, p)));
        objects.RemoveAll(o =>
            !kept.Contains(o.TypeId) && !Serves(o.TypeId)
            && starts.Any(s => Sq(s.X - o.X) + Sq(s.Z - o.Z) <= radius * radius)
            && rng.NextInt(0, 1000) >= keep);
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
    /// Drinkable water where the hydrology put it (TL #19722): an object exactly on every spring cell,
    /// and as the data says along pond and lake edges and stream banks (dry walkable cells next to that
    /// water, in an order from the generator stream). Each entry keeps its spacing to every drink spot
    /// placed before and, if the data asks, a distance from running streams (no pool on a stream mouth).
    /// </summary>
    private static void DrinkSpots(WorldPreset preset, WorldMap map, List<IslandObject> objects, Pcg32 rng)
    {
        if (map.Hydrology is not { } hydrology)
        {
            return;
        }

        int w = map.WidthCells, d = map.DepthCells;
        var taken = objects.Select(o => (o.X, o.Z)).ToHashSet();
        var placed = new List<(int X, int Z)>();
        bool Walkable(int x, int z) => map.Cell(x, z).WalkCost != MapCell.Blocked;
        bool Beside(int x, int z, WaterFeature feature) =>
            map.Cell(x, z).Water == WaterKind.None && Neighbours4.Any(n => map.Contains(x + n.Dx, z + n.Dz) && (hydrology.Feature(x + n.Dx, z + n.Dz) & feature) != 0);
        bool NearStream(int x, int z, int radius)
        {
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if ((dx * dx) + (dz * dz) <= radius * radius && map.Contains(x + dx, z + dz) && hydrology.Feature(x + dx, z + dz) == WaterFeature.Stream)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        foreach (var spot in preset.Water.DrinkSpots)
        {
            var candidates = new List<(int X, int Z)>();
            if (spot.At == WorldPresetDrinkSpotAt.Spring)
            {
                candidates.AddRange(hydrology.Springs().Where(c => Walkable(c.X, c.Z)));
            }
            else
            {
                var feature = spot.At == WorldPresetDrinkSpotAt.Lake_edge ? WaterFeature.Lake : WaterFeature.Stream;
                for (var z = 1; z < d - 1; z++)
                {
                    for (var x = 1; x < w - 1; x++)
                    {
                        // A stream bank with a minimum stream distance lies one step beyond it (the pool keeps
                        // off the cut bed); otherwise right beside the water. A hollow is any dry cell; the
                        // depression test below decides.
                        var bank = spot.At == WorldPresetDrinkSpotAt.Hollow
                            ? map.Cell(x, z).Water == WaterKind.None
                            : feature == WaterFeature.Stream && spot.MinStreamDistanceCells > 0
                            ? map.Cell(x, z).Water == WaterKind.None && NearStream(x, z, spot.MinStreamDistanceCells + 1)
                            : Beside(x, z, feature);
                        if (Walkable(x, z) && bank && (spot.MinHollowDepth is not { } hollow || InHollow(map, x, z, spot.HollowRadiusCells ?? 1, hollow)))
                        {
                            candidates.Add((x, z));
                        }
                    }
                }

                for (var k = candidates.Count - 1; k > 0; k--)
                {
                    var j = rng.NextInt(0, k + 1);
                    (candidates[k], candidates[j]) = (candidates[j], candidates[k]);
                }
            }

            var spacing2 = spot.SpacingCells * spot.SpacingCells;
            var count = 0;
            foreach (var (x, z) in candidates)
            {
                if (count >= spot.MaxTotal)
                {
                    break;
                }

                if (taken.Contains((x, z)) || placed.Any(p => Sq(p.X - x) + Sq(p.Z - z) < spacing2)
                    || (spot.MinStreamDistanceCells > 0 && NearStream(x, z, spot.MinStreamDistanceCells)))
                {
                    continue;
                }

                objects.Add(new IslandObject(spot.ObjectType, x, z));
                taken.Add((x, z));
                placed.Add((x, z));
                count++;
            }
        }
    }

    /// <summary>
    /// Whether a cell lies in a depression (TL #19990): at least the depth below the mean ground of the
    /// square radius around it (map cells only, water included at its bed).
    /// </summary>
    private static bool InHollow(WorldMap map, int x, int z, int radius, int depth)
    {
        long sum = 0;
        var count = 0;
        for (var dz = -radius; dz <= radius; dz++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (map.Contains(x + dx, z + dz))
                {
                    sum += map.Cell(x + dx, z + dz).Height;
                    count++;
                }
            }
        }

        return (map.Cell(x, z).Height + depth) * (long)count <= sum;
    }

    /// <summary>
    /// Objects by biome × type with densities, clusters and upper limits (R8). Candidate cells of a biome
    /// are its dry walkable cells; a water biome (stream, lake, swamp) uses its banks, and if it has
    /// none, its shallow water (reed in the swamp, clay at the stream).
    /// </summary>
    private static void Spawn(SpawnTable spawns, WorldMap map, List<IslandObject> objects, Pcg32 rng)
    {
        var byBiome = SpawnCells(map);
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

    /// <summary>
    /// The cells each biome spawns on: a land biome its dry walkable cells; a water biome (stream, lake,
    /// swamp) its banks, and if it has none, its shallow water.
    /// </summary>
    private static Dictionary<string, List<(int X, int Z)>> SpawnCells(WorldMap map)
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

        return byBiome;
    }

    /// <summary>Pairs (woman and man) on different beaches, far enough apart not to see each other (R9, B2517).</summary>
    private static List<StartPosition> Starts(WorldPreset preset, WorldMap map, Func<(int X, int Z), bool> feasible, Func<(int X, int Z), bool>? near, Pcg32 rng)
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

            // Beaches where the start supply is possible come first (TL F5263 A), of those the ones with
            // the mandatory resources within the preferred radius (TL #19722); others only without one.
            options = Feasible(options);
            var anchor = options[rng.NextInt(0, options.Count)];
            var partner = Feasible(beach.Where(b => b != anchor && Math.Abs(b.X - anchor.X) <= 2 && Math.Abs(b.Z - anchor.Z) <= 2).ToList());
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

        List<(int X, int Z)> Feasible(List<(int X, int Z)> cells)
        {
            var good = cells.Where(feasible).ToList();
            if (good.Count == 0)
            {
                return cells;
            }

            return near is not null && good.Where(near).ToList() is { Count: > 0 } close ? close : good;
        }
    }

    /// <summary>
    /// Whether the start supply is possible at a cell (TL F5263 A): for every resource, the matching
    /// objects already there plus the free cells where a matching spawn rule with room left may place
    /// one (its biome cells, within its max_total) reach the minimum, all within the radius and in the
    /// cell's walkable area - the same rules <see cref="StartSupply"/> places by. Answers are cached.
    /// </summary>
    private static Func<(int X, int Z), bool> SupplyFeasible(StartSupply supply, SpawnTable spawns, ObjectCatalog catalog, WorldMap map, List<IslandObject> objects)
    {
        int w = map.WidthCells, d = map.DepthCells;
        var areas = PathFinder.WalkableAreas(map);
        var cellsOf = SpawnCells(map);
        var taken = objects.Select(o => (o.X, o.Z)).ToHashSet();
        var rules = spawns.Spawns.ToList();

        // Per resource: how much each cell offers (matching objects on it, or 1 if a rule may place there).
        var offers = supply.Resources.Select(resource =>
        {
            bool Matches(string type) =>
                resource.PropertiesAll.All(p => catalog.HasProperty(type, p)) && !resource.PropertiesNone.Any(p => catalog.HasProperty(type, p));
            var offer = new int[w * d];
            foreach (var o in objects.Where(o => Matches(o.TypeId)))
            {
                offer[(o.Z * w) + o.X]++;
            }

            foreach (var rule in rules.Where(r => Matches(r.ObjectType) && cellsOf.ContainsKey(r.Biome)))
            {
                var cells = cellsOf[rule.Biome];
                var on = cells.ToHashSet();
                if (rule.MaxTotal - objects.Count(o => o.TypeId == rule.ObjectType && on.Contains((o.X, o.Z))) <= 0)
                {
                    continue;
                }

                foreach (var (x, z) in cells.Where(c => !taken.Contains(c)))
                {
                    offer[(z * w) + x] = Math.Max(offer[(z * w) + x], 1);
                }
            }

            return (resource.Min, Offer: offer);
        }).ToList();

        var radius = supply.RadiusCells;
        var cache = new Dictionary<(int X, int Z), bool>();
        return cell =>
        {
            if (cache.TryGetValue(cell, out var known))
            {
                return known;
            }

            var area = areas[(cell.Z * w) + cell.X];
            var ok = area != 0 && offers.All(r =>
            {
                var sum = 0;
                for (var z = Math.Max(0, cell.Z - radius); z <= Math.Min(d - 1, cell.Z + radius) && sum < r.Min; z++)
                {
                    for (var x = Math.Max(0, cell.X - radius); x <= Math.Min(w - 1, cell.X + radius) && sum < r.Min; x++)
                    {
                        var i = (z * w) + x;
                        if (r.Offer[i] > 0 && areas[i] == area && ((x - cell.X) * (x - cell.X)) + ((z - cell.Z) * (z - cell.Z)) <= radius * radius && (x, z) != cell)
                        {
                            sum += r.Offer[i];
                        }
                    }
                }

                return sum >= r.Min;
            });
            cache[cell] = ok;
            return ok;
        };
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

    /// <summary>
    /// The start supply as check lines: reachable is what the poorest start finds within the radius and
    /// its walkable area, so a start the supply could not serve shows as a failed line instead of passing
    /// silently. A mandatory resource's line (<c>start_supply/&lt;id&gt;</c>) decides whether the attempt
    /// passes; the others only report (<c>report/start_supply/&lt;id&gt;</c>).
    /// </summary>
    private static IEnumerable<MandatoryResult> SupplyCheck(StartSupply supply, ObjectCatalog catalog, WorldMap map, List<IslandObject> objects, List<StartPosition> starts)
    {
        var areas = PathFinder.WalkableAreas(map);
        var w = map.WidthCells;
        var r2 = supply.RadiusCells * supply.RadiusCells;
        foreach (var resource in supply.Resources)
        {
            var fewest = starts.Count == 0 ? 0 : starts.Min(start =>
            {
                var area = areas[(start.Z * w) + start.X];
                return objects.Count(o =>
                    ((o.X - start.X) * (o.X - start.X)) + ((o.Z - start.Z) * (o.Z - start.Z)) <= r2
                    && area != 0 && areas[(o.Z * w) + o.X] == area
                    && resource.PropertiesAll.All(p => catalog.HasProperty(o.TypeId, p))
                    && !resource.PropertiesNone.Any(p => catalog.HasProperty(o.TypeId, p)));
            });
            var prefix = resource.Mandatory == true ? string.Empty : MandatoryResult.ReportPrefix;
            yield return new MandatoryResult($"{prefix}start_supply/{resource.Id}", fewest, fewest, resource.Min, int.MaxValue);
        }
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
