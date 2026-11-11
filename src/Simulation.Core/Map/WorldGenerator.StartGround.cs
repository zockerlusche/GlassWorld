using GlassWorld.Contracts;

namespace GlassWorld.Simulation.Map;

/// <summary>
/// Dry start ground (generator version 5, TL #20750; findings 17 and 18 of the visual survey D704): the
/// beach of the start biome lies below the high-tide line, so a pair placed on it stood in the sea at
/// every high tide - and a world starts at high tide. With <c>start.dry_ground</c> in the preset a start
/// cell and the cells around it are dry land above the highest tide; the mandatory check says so in its
/// own line. All values are data; whole numbers only.
/// </summary>
public static partial class WorldGenerator
{
    /// <summary>Id of the check line of the dry start ground.</summary>
    public const string StartDryGroundCheck = "start_dry_ground";

    /// <summary>
    /// The lowest ground, in tenths of a height unit, that counts as dry for a start: the mean sea level
    /// plus the highest tide of the preset plus the margin of <c>start.dry_ground</c>; null without the data.
    /// </summary>
    public static int? StartDryLineTenths(WorldPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return preset.Start.DryGround is { } dry
            ? (preset.Terrain.SeaLevel * 10) + new Tides(preset.Tides).HighestLevelTenths + (dry.Margin * 10)
            : null;
    }

    /// <summary>
    /// Whether a cell and every cell within the radius of <c>start.dry_ground</c> (distance between cell
    /// centres) is dry land - no sea, no fresh water - with its ground at or above the dry line
    /// (<see cref="StartDryLineTenths"/>). Cells beyond the map edge count as sea. Always true without the data.
    /// </summary>
    public static bool OnDryStartGround(WorldPreset preset, WorldMap map, int x, int z)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(map);
        return preset.Start.DryGround is not { } dry || StartDryLineTenths(preset) is not { } line || DryAround(map, x, z, dry.RadiusCells, line);
    }

    private static bool DryAround(WorldMap map, int x, int z, int radius, int line)
    {
        for (var dz = -radius; dz <= radius; dz++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if ((dx * dx) + (dz * dz) <= radius * radius && !Dry(map, x + dx, z + dz, line))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The check line of the dry start ground: how many start cells stand on dry ground above the highest
    /// tide (<see cref="OnDryStartGround"/>); all starts of the preset must. Null without the data.
    /// </summary>
    public static MandatoryResult? StartGroundCheck(WorldPreset preset, WorldMap map, IReadOnlyList<StartPosition> starts)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(starts);
        if (preset.Start.DryGround is null)
        {
            return null;
        }

        var dry = starts.Count(s => OnDryStartGround(preset, map, s.X, s.Z));
        return new MandatoryResult(StartDryGroundCheck, dry, dry, preset.Start.Pairs * 2, int.MaxValue);
    }

    /// <summary>
    /// Whether a cell is beach as the start rule up to version 4 knew it: a dry walkable cell of the start
    /// biome within three cells of the sea. With <c>start.dry_ground</c> the starts lie beside these cells.
    /// </summary>
    public static bool IsStartBeach(WorldPreset preset, WorldMap map, int x, int z)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(map);
        return map.Contains(x, z) && map.Cell(x, z) is { Water: WaterKind.None } cell && cell.WalkCost != MapCell.Blocked
            && map.BiomeIds[cell.Biome] == preset.Start.Biome && NearSalt(map, x, z, 3);
    }

    /// <summary>
    /// The cells a start may stand on with <c>start.dry_ground</c>: walkable dry ground above the highest
    /// tide with its surroundings (<see cref="OnDryStartGround"/>), at most the data distance from a beach
    /// cell (<see cref="IsStartBeach"/>), so the pairs still start by the sea, just above the beach.
    /// </summary>
    private static bool[] StartGround(WorldPreset preset, WorldMap map, WorldPresetStartGround dry)
    {
        int w = map.WidthCells, d = map.DepthCells;
        var line = StartDryLineTenths(preset)!.Value;
        var shore = dry.ShoreDistanceCells;
        var byTheSea = new bool[w * d];
        for (var z = 1; z < d - 1; z++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                if (!IsStartBeach(preset, map, x, z))
                {
                    continue;
                }

                for (var nz = Math.Max(0, z - shore); nz <= Math.Min(d - 1, z + shore); nz++)
                {
                    for (var nx = Math.Max(0, x - shore); nx <= Math.Min(w - 1, x + shore); nx++)
                    {
                        byTheSea[(nz * w) + nx] |= Sq(nx - x) + Sq(nz - z) <= shore * shore;
                    }
                }
            }
        }

        var ground = new bool[w * d];
        for (var i = 0; i < w * d; i++)
        {
            ground[i] = byTheSea[i] && map.Cell(i % w, i / w).WalkCost != MapCell.Blocked && DryAround(map, i % w, i / w, dry.RadiusCells, line);
        }

        return ground;
    }

    private static bool Dry(WorldMap map, int x, int z, int lineTenths) =>
        map.Contains(x, z) && map.Cell(x, z) is { Water: WaterKind.None } cell && cell.Height * 10 >= lineTenths;

}
