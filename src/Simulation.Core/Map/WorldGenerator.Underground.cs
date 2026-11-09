using GlassWorld.Contracts;

namespace GlassWorld.Simulation.Map;

/// <summary>
/// The underground from the seed (TL #19717, W1672 generator part): a groundwater table that follows the
/// smoothed ground (deeper under hills, near the surface in valleys and at the foot of slopes, where the
/// springs rise), soil by height, slope and moisture (first matching data rule), stones and ore from their
/// own noise. Only data and the seed decide; nothing uses it yet (digging and wells: B5).
/// </summary>
public static partial class WorldGenerator
{
    /// <summary>
    /// The groundwater table per cell in height units: sea level plus the followed share of the mean
    /// ground around (data radius) above the sea, minus the base depth, plus noise. The sea is its own table.
    /// </summary>
    private static int[] GroundwaterTable(WorldPreset preset, int[] heights, WaterKind[] water, int w, int d, ulong seed)
    {
        var settings = preset.Underground.Groundwater;
        var sea = preset.Terrain.SeaLevel;
        var r = settings.SmoothingRadiusCells;

        // Summed-area table for the mean ground around each cell.
        var sum = new long[(w + 1) * (d + 1)];
        for (var z = 0; z < d; z++)
        {
            for (var x = 0; x < w; x++)
            {
                sum[((z + 1) * (w + 1)) + x + 1] = heights[(z * w) + x] + sum[(z * (w + 1)) + x + 1] + sum[((z + 1) * (w + 1)) + x] - sum[(z * (w + 1)) + x];
            }
        }

        var noiseSeed = NamedSeed(seed, "worldgen/groundwater");
        var table = new int[w * d];
        for (var z = 0; z < d; z++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (z * w) + x;
                if (water[i] == WaterKind.Salt)
                {
                    table[i] = sea;
                    continue;
                }

                int x0 = Math.Max(0, x - r), z0 = Math.Max(0, z - r), x1 = Math.Min(w, x + r + 1), z1 = Math.Min(d, z + r + 1);
                var total = sum[(z1 * (w + 1)) + x1] - sum[(z0 * (w + 1)) + x1] - sum[(z1 * (w + 1)) + x0] + sum[(z0 * (w + 1)) + x0];
                var mean = (int)(total / ((x1 - x0) * (z1 - z0)));
                var noise = (ValueNoise(noiseSeed, 1, x, z, settings.NoiseWavelengthCells) - 500) * settings.NoiseAmplitude / 500;
                table[i] = sea + (Math.Max(0, mean - sea) * settings.FollowPermille / 1000) - settings.BaseDepth + noise;
            }
        }

        return table;
    }

    /// <summary>The underground layer of the finished terrain: groundwater depth, soil, stones, ore.</summary>
    private static UndergroundLayer Underground(WorldPreset preset, int[] heights, WaterKind[] water, int[] moisture, int[] table, int w, int d, ulong seed)
    {
        var settings = preset.Underground;
        var sea = preset.Terrain.SeaLevel;
        var soils = new List<string> { settings.FallbackSoil };
        foreach (var rule in settings.SoilRules.Where(rule => !soils.Contains(rule.Soil)))
        {
            soils.Add(rule.Soil);
        }

        var stoneSoil = soils.IndexOf(settings.Stones.StoneSoil);
        var stonesSeed = NamedSeed(seed, "worldgen/stones");
        var oreSeed = NamedSeed(seed, "worldgen/ore");
        var cells = new GroundCell[w * d];
        for (var z = 0; z < d; z++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (z * w) + x;
                var above = heights[i] - sea;
                var slope = Slope(heights, w, d, x, z);
                var soil = 0;
                foreach (var rule in settings.SoilRules)
                {
                    if (above >= rule.MinHeight && above <= rule.MaxHeight && slope >= rule.MinSlope
                        && moisture[i] >= rule.MinMoisturePermille && moisture[i] <= rule.MaxMoisturePermille)
                    {
                        soil = soils.IndexOf(rule.Soil);
                        break;
                    }
                }

                var stones = settings.Stones.BasePermille + (slope * settings.Stones.PerSlopePermille) + (soil == stoneSoil ? settings.Stones.StoneSoilPermille : 0)
                    + ((ValueNoise(stonesSeed, 1, x, z, settings.Stones.NoiseWavelengthCells) - 500) * settings.Stones.NoisePermille / 500);
                var ore = 0;
                if (above >= settings.Ore.MinHeight && water[i] == WaterKind.None)
                {
                    var vein = ValueNoise(oreSeed, 1, x, z, settings.Ore.NoiseWavelengthCells);
                    if (vein > settings.Ore.ThresholdPermille)
                    {
                        ore = (vein - settings.Ore.ThresholdPermille) * settings.Ore.MaxPermille / Math.Max(1, 1000 - settings.Ore.ThresholdPermille);
                    }
                }

                var groundwater = water[i] == WaterKind.None ? Math.Max(0, heights[i] - table[i]) : 0;
                cells[i] = new GroundCell(groundwater, (byte)soil, Math.Clamp(stones, 0, 1000), Math.Clamp(ore, 0, 1000));
            }
        }

        return new UndergroundLayer(w, d, soils, cells);
    }

    /// <summary>A noise seed of its own for a name, derived from the seed like a named stream.</summary>
    private static ulong NamedSeed(ulong seed, string name)
    {
        ulong h = 14695981039346656037;
        foreach (var ch in name)
        {
            h = (h ^ ch) * 1099511628211;
        }

        return Mix(seed ^ h);
    }
}
