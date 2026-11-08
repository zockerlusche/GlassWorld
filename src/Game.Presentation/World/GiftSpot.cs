using GlassWorld.Simulation.Map;

namespace GlassWorld.Presentation.World;

/// <summary>
/// Where a gift may be placed on the island (GW3 review #113, finding 1), without Godot: the click ray
/// meets the ground or the sea surface, whichever is higher, and a spot on water or off the island is
/// refused with a reason key before anything goes to the core (so it costs no favour). The core's own
/// walkable-ground check comes with B2; until then the screen keeps gifts on dry land.
/// </summary>
public static class GiftSpot
{
    /// <summary>Reason key: the spot is water (sea, lake, stream, swamp).</summary>
    public const string OnWater = "msg.gift.on_water";

    /// <summary>Reason key: the spot lies outside the island map.</summary>
    public const string OffIsland = "msg.gift.off_island";

    /// <summary>The surface a click ray meets: the drawn ground, or the sea surface where that lies higher.</summary>
    public static Func<double, double, double> Surface(Func<double, double, double> ground, double seaLevelM)
    {
        ArgumentNullException.ThrowIfNull(ground);
        return (x, z) => Math.Max(ground(x, z), seaLevelM);
    }

    /// <summary>
    /// Null if a gift may lie at (x, z) in metres, otherwise the reason key: off the map, a water cell, or
    /// ground below the sea surface (the open sea around the map edge).
    /// </summary>
    public static string? Refusal(WorldMap map, double cellM, double x, double z, double groundM, double seaLevelM)
    {
        ArgumentNullException.ThrowIfNull(map);
        var cx = (int)Math.Floor(x / cellM);
        var cz = (int)Math.Floor(z / cellM);
        if (!map.Contains(cx, cz))
        {
            return OffIsland;
        }

        return map.Cell(cx, cz).Water != WaterKind.None || groundM < seaLevelM ? OnWater : null;
    }
}
