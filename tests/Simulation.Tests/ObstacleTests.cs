using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1790 (Marcel, 15a): inhabitants walked through trees and rocks. Trunks and rocks are obstacles of the path search
/// (obstacle_radius_cm from the catalogue): a route passes them outside their circle, and a walk to one ends in front of
/// it, the walker's body (half the spacing) away from its edge.
/// </summary>
public sealed class ObstacleTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");

    [Fact]
    public void TreesAndRockNichesAreObstaclesFromTheData()
    {
        var map = Island().Map!;
        Assert.True(map.ObstacleRadiusCm.GetValueOrDefault("deciduous_tree") > 0);
        Assert.True(map.ObstacleRadiusCm.GetValueOrDefault("rock_niche") > 0);
        Assert.False(map.ObstacleRadiusCm.ContainsKey("apple"));
        Assert.True(map.BodyRadiusM > 0);
        Assert.NotNull(map.Obstacles());
    }

    [Fact]
    public void ARouteAcrossATreeGoesAroundItsTrunk()
    {
        var host = Island();
        var map = host.Map!;
        var (tree, from, to) = CrossingOf(host, map);
        var route = map.Find(from, to);

        Assert.Equal(PathStatus.Found, route.Status);
        Assert.Equal(to, route.Waypoints[^1]);
        var points = new List<Position> { from };
        points.AddRange(route.Waypoints);
        var obstacles = Obstacles(host, map).ToList();
        for (var i = 1; i < points.Count; i++)
        {
            foreach (var (centre, radius) in obstacles)
            {
                Assert.True(
                    SegmentDistance(points[i - 1], points[i], centre) >= radius - 1e-9,
                    $"leg {i} from {points[i - 1]} to {points[i]} cuts the obstacle at {centre} (tree {tree.Id})");
            }
        }
    }

    [Fact]
    public void AWalkToATrunkOrARockEndsInFrontOfIt()
    {
        var host = Island();
        var map = host.Map!;
        foreach (var type in new[] { "deciduous_tree", "rock_niche" })
        {
            var thing = host.Objects.All.First(o => o.TypeId == type && Approach(map, o) is not null);
            var from = Approach(map, thing)!.Value;
            var route = map.Find(from, thing.Position);

            Assert.Equal(PathStatus.Found, route.Status);
            var end = route.Waypoints[^1];
            var clear = (map.ObstacleRadiusCm[type] / 100.0) + map.BodyRadiusM;
            Assert.Equal(clear, Distance(end, thing.Position), 6);

            // The end lies on the walker's side, not behind the thing.
            Assert.True(Distance(end, from) < Distance(thing.Position, from), type);
        }
    }

    [Fact]
    public void ObstaclesComeAndGoWithTheirThings()
    {
        var host = Island();
        var map = host.Map!;
        var (tree, from, to) = CrossingOf(host, map);
        var around = map.Find(from, to);
        Assert.True(map.Obstacles()![CellIndex(map, tree.Position)]);

        host.Objects.Remove(tree.Id);
        Assert.False(map.Obstacles()![CellIndex(map, tree.Position)]);
        var straight = map.Find(from, to);
        Assert.True(Length(from, straight.Waypoints) < Length(from, around.Waypoints), "without the tree the way is shorter");
    }

    private static SimulationHost Island()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        return host;
    }

    /// <summary>A tree with plain walkable ground 3 cells west and east of it and no other obstacle on that row.</summary>
    private static (WorldObject Tree, Position From, Position To) CrossingOf(SimulationHost host, WorldMapSystem map)
    {
        var obstacles = map.Obstacles()!;
        foreach (var tree in host.Objects.All.Where(o => o.TypeId == "deciduous_tree").OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            if (map.CellOf(tree.Position) is not { } at)
            {
                continue;
            }

            var free = true;
            for (var dz = -1; dz <= 1 && free; dz++)
            {
                for (var dx = -3; dx <= 3 && free; dx++)
                {
                    var (x, z) = (at.X + dx, at.Z + dz);
                    // Plain walkable ground, and no obstacle but the tree's own cell.
                    free = map.Map.Contains(x, z) && map.Map.Cell(x, z) is { Water: WaterKind.None } cell && cell.WalkCost != MapCell.Blocked
                        && ((dx == 0 && dz == 0) || !obstacles[CellIndex(map, x, z)]);
                }
            }

            if (free && obstacles[CellIndex(map, at.X, at.Z)])
            {
                return (tree, map.CentreOf(at.X - 3, at.Z), map.CentreOf(at.X + 3, at.Z));
            }
        }

        throw new InvalidOperationException("no free-standing tree on the island");
    }

    /// <summary>
    /// The centre of the first cell west of a thing that no obstacle covers, with plain walkable ground all the way and
    /// at least the walker's clearance away from the thing; null if there is none within four cells.
    /// </summary>
    private static Position? Approach(WorldMapSystem map, WorldObject thing)
    {
        if (map.CellOf(thing.Position) is not { } at)
        {
            return null;
        }

        var obstacles = map.Obstacles()!;
        var clear = (map.ObstacleRadiusCm[thing.TypeId] / 100.0) + map.BodyRadiusM;
        for (var dx = 1; dx <= 4; dx++)
        {
            var (x, z) = (at.X - dx, at.Z);
            if (!map.Map.Contains(x, z) || map.Map.Cell(x, z) is not { Water: WaterKind.None } cell || cell.WalkCost == MapCell.Blocked)
            {
                return null;
            }

            if (!obstacles[CellIndex(map, x, z)] && Distance(map.CentreOf(x, z), thing.Position) > clear)
            {
                return map.CentreOf(x, z);
            }
        }

        return null;
    }

    private static IEnumerable<(Position Centre, double RadiusM)> Obstacles(SimulationHost host, WorldMapSystem map) =>
        host.Objects.All.Where(o => map.ObstacleRadiusCm.ContainsKey(o.TypeId)).Select(o => (o.Position, map.ObstacleRadiusCm[o.TypeId] / 100.0));

    private static int CellIndex(WorldMapSystem map, Position at) => map.CellOf(at) is { } c ? CellIndex(map, c.X, c.Z) : -1;

    private static int CellIndex(WorldMapSystem map, int x, int z) => (z * map.Map.WidthCells) + x;

    private static double Distance(Position a, Position b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    private static double Length(Position from, IReadOnlyList<Position> waypoints)
    {
        var total = 0.0;
        var at = from;
        foreach (var p in waypoints)
        {
            total += Distance(at, p);
            at = p;
        }

        return total;
    }

    private static double SegmentDistance(Position a, Position b, Position p)
    {
        var (dx, dz) = (b.X - a.X, b.Z - a.Z);
        var length = (dx * dx) + (dz * dz);
        var t = length == 0 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Z - a.Z) * dz)) / length, 0, 1);
        return Distance(new Position(a.X + (t * dx), a.Z + (t * dz)), p);
    }
}
