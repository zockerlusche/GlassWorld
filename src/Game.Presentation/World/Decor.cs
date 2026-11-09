using GlassWorld.Contracts;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.World;

/// <summary>
/// Decorative plants of the island (TL #19722: "denser vegetation"): grass tufts, flowers and shrubs in
/// groups, from <c>decor</c> in <c>content/look/objects.json</c>. Presentation only - they are no core
/// objects, carry no state, are never saved and nobody can use them; they are drawn like objects (same
/// looks, same ground fit). Placed from the world seed and the cell alone (no random stream), so the same
/// world always shows the same plants: a cell of a listed biome may hold a group centre, the group's plants
/// scatter within its spread and are kept only on dry walkable ground of a listed biome, apart from each
/// other and from every core object at the start.
/// </summary>
public static class Decor
{
    /// <summary>Id prefix of decorative plants (never a core object id).</summary>
    public const string IdPrefix = "decor-";

    /// <summary>The decorative plants of a map as pseudo objects (id, decor type, position in metres).</summary>
    public static IReadOnlyList<WorldObject> Plan(IEnumerable<ObjectDecor>? decor, WorldMap map, ulong seed, IEnumerable<WorldObject> objects, double cellM)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(objects);
        var result = new List<WorldObject>();
        if (decor is null)
        {
            return result;
        }

        // Core objects and plants placed so far, bucketed by cell for the distance checks.
        var objectCells = new Dictionary<(int, int), List<(double X, double Z)>>();
        void Add(Dictionary<(int, int), List<(double X, double Z)>> into, double x, double z)
        {
            var key = ((int)Math.Floor(x / cellM), (int)Math.Floor(z / cellM));
            if (!into.TryGetValue(key, out var list))
            {
                into[key] = list = [];
            }

            list.Add((x, z));
        }

        bool Near(Dictionary<(int, int), List<(double X, double Z)>> within, double x, double z, double distance)
        {
            if (distance <= 0)
            {
                return false;
            }

            var reach = (int)Math.Ceiling(distance / cellM);
            int cx = (int)Math.Floor(x / cellM), cz = (int)Math.Floor(z / cellM);
            for (var dz = -reach; dz <= reach; dz++)
            {
                for (var dx = -reach; dx <= reach; dx++)
                {
                    if (within.TryGetValue((cx + dx, cz + dz), out var list) && list.Any(p => ((p.X - x) * (p.X - x)) + ((p.Z - z) * (p.Z - z)) < distance * distance))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        foreach (var o in objects)
        {
            Add(objectCells, o.Position.X, o.Position.Z);
        }

        var plants = new Dictionary<(int, int), List<(double X, double Z)>>();
        var kind = 0;
        foreach (var d in decor)
        {
            kind++;
            var biomes = d.Biomes.Select(b => map.BiomeIds.ToList().IndexOf(b)).Where(i => i >= 0).ToHashSet();
            bool Fits(int x, int z) =>
                map.Contains(x, z) && map.Cell(x, z) is { Water: WaterKind.None } c && c.WalkCost != MapCell.Blocked && biomes.Contains(c.Biome);
            for (var z = 0; z < map.DepthCells; z++)
            {
                for (var x = 0; x < map.WidthCells; x++)
                {
                    if (!Fits(x, z) || Hash(seed, kind, x, z, 0) % 10000 >= (ulong)d.GroupsPer10000Cells)
                    {
                        continue;
                    }

                    var count = d.Group.Min + (int)(Hash(seed, kind, x, z, 1) % (ulong)(Math.Max(d.Group.Min, d.Group.Max) - d.Group.Min + 1));
                    for (var i = 0; i < count; i++)
                    {
                        // A point in the disc of the spread around the cell centre (square root for an even spread).
                        var angle = Unit(Hash(seed, kind, x, z, 2 + (2 * i))) * 2 * Math.PI;
                        var radius = Math.Sqrt(Unit(Hash(seed, kind, x, z, 3 + (2 * i)))) * d.SpreadM;
                        double px = ((x + 0.5) * cellM) + (radius * Math.Cos(angle)), pz = ((z + 0.5) * cellM) + (radius * Math.Sin(angle));
                        if (!Fits((int)Math.Floor(px / cellM), (int)Math.Floor(pz / cellM))
                            || Near(objectCells, px, pz, d.MinObjectDistanceM) || Near(plants, px, pz, d.MinSpacingM))
                        {
                            continue;
                        }

                        Add(plants, px, pz);
                        result.Add(new WorldObject($"{IdPrefix}{kind}-{x}-{z}-{i}", d.Type, new Position(px, pz)));
                    }
                }
            }
        }

        return result;
    }

    private static double Unit(ulong h) => (h >> 11) * (1.0 / (1UL << 53));

    private static ulong Hash(ulong seed, int kind, int x, int z, int salt)
    {
        var h = seed ^ ((ulong)(uint)kind << 56) ^ ((ulong)(uint)x << 36) ^ ((ulong)(uint)z << 16) ^ (uint)salt;
        h += 0x9E3779B97F4A7C15;
        h = (h ^ (h >> 30)) * 0xBF58476D1CE4E5B9;
        h = (h ^ (h >> 27)) * 0x94D049BB133111EB;
        return h ^ (h >> 31);
    }
}
