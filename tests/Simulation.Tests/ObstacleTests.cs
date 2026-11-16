using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
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

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.0)]
    public void APickUpAtARockNicheStartsBesideItAndAtItsVeryCentre(double offset)
    {
        // Review GW1 #515: a rock niche clears 1.3 m (radius + body), pick_up reaches 1.0 m, and nothing at the start of an
        // action measures the distance again - the walk's arrival decides. A thing lying by the rock is walked to and
        // reached; a thing at the rock's very centre is reached from in front of it, at the rock's surface.
        // A silent decider: no answer to an open question takes the place of the pick-up set here.
        var host = Island(new SilentPort());
        var map = host.Map!;
        var niche = host.Objects.All.First(o => o.TypeId == "rock_niche" && Approach(map, o) is not null);
        var radius = map.ObstacleRadiusCm["rock_niche"] / 100.0;
        var reach = host.Content.Actions.Timing("pick_up").ReachM;
        var inhabitant = host.Inhabitants.All.First();
        var stone = host.Objects.Spawn("fieldstone", new Position(niche.Position.X - offset, niche.Position.Z));
        inhabitant.Position = Approach(map, niche)!.Value;
        foreach (var need in inhabitant.Needs.Keys.ToList())
        {
            inhabitant.Needs[need] = 1.0;
        }

        inhabitant.Activity = new Activity("pick_up", stone.Id, stone.Position, null);
        double? startedAt = null;
        var seen = new List<string>();
        host.Inhabitants.Reported += e =>
        {
            if (e.InhabitantId == inhabitant.Id && seen.Count < 12)
            {
                seen.Add($"{e.Kind} {e.Action} {e.TargetId} {e.Detail}");
            }

            if (e.Kind == InhabitantsSystem.ActionStarted && e.TargetId == stone.Id)
            {
                startedAt ??= Distance(inhabitant.Position, stone.Position);
            }
        };
        for (var i = 0; i < 2000 && startedAt is null; i++)
        {
            host.World.Run(1);
        }

        Assert.True(startedAt is not null, "never started: " + string.Join("; ", seen));
        var surface = offset < 0.01 ? radius : 0;
        Assert.True(startedAt!.Value - surface <= reach + 1e-6, $"offset {offset}: the pick-up started {startedAt:0.00} m from the stone");
        Assert.True(Distance(inhabitant.Position, niche.Position) >= radius, "and the walker stands outside the rock");
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

    [Fact]
    public void AChangeOfTypeChangesTheObstacle()
    {
        // Review GW1 #515: a tree that becomes a rock keeps the number of obstacles - the type change alone rebuilds them.
        var host = Island();
        var map = host.Map!;
        var (tree, _, _) = CrossingOf(host, map);
        var before = map.Obstacles()!.Count(c => c);
        Assert.True(host.Objects.ChangeType(tree.Id, "rock_niche"));
        Assert.True(map.Obstacles()!.Count(c => c) > before, "a rock covers more cells than a trunk");
    }

    [Fact]
    public void AHeldTreeBlocksNothingAndAMovedOneBlocksWhereItStands()
    {
        // A move fires no event (a young tree picked up goes with its holder): the cells follow it anyway, or a loaded
        // world, which builds them fresh, would walk other ways than the one that ran on (ExplorationTests save/load).
        var host = Island();
        var map = host.Map!;
        var (tree, _, _) = CrossingOf(host, map);
        var cell = CellIndex(map, tree.Position);
        Assert.True(map.Obstacles()![cell]);

        // A move fires no event: the cells follow by the next tick (A1813: checked once per tick).
        host.Objects.SetHeld(tree.Id, host.Inhabitants.All.First().Id);
        host.World.Step();
        Assert.False(map.Obstacles()![cell]);

        host.Objects.SetHeld(tree.Id, null);
        Assert.True(host.Objects.Move(tree.Id, new Position(tree.Position.X + 2, tree.Position.Z)));
        host.World.Step();
        Assert.False(map.Obstacles()![cell]);
        Assert.True(map.Obstacles()![cell + 2]);
    }

    [Fact]
    public void APocketClosedInByTrunksIsAnAreaOfItsOwnAndTheTrunksStayReachable()
    {
        // A1813 (review GW1 #515): the walkable areas know the obstacles - nobody starts a search into a closed pocket.
        var host = Island();
        var map = host.Map!;
        var obstacles = map.Obstacles()!;
        var free = Enumerable.Range(0, map.Map.WidthCells * map.Map.DepthCells)
            .Select(i => (X: i % map.Map.WidthCells, Z: i / map.Map.WidthCells))
            .First(c => Enumerable.Range(-3, 7).All(dz => Enumerable.Range(-3, 7).All(dx =>
                map.Map.Contains(c.X + dx, c.Z + dz) && map.Map.Cell(c.X + dx, c.Z + dz) is { Water: WaterKind.None } cell
                && cell.WalkCost != MapCell.Blocked && !obstacles[CellIndex(map, c.X + dx, c.Z + dz)])));
        var inside = map.CentreOf(free.X, free.Z);
        var outside = map.CentreOf(free.X + 3, free.Z);
        Assert.True(map.Connected(inside, outside));

        var ring = new List<WorldObject>();
        foreach (var (dx, dz) in new[] { (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1) })
        {
            ring.Add(host.Objects.Spawn("young_tree", map.CentreOf(free.X + dx, free.Z + dz)));
        }

        Assert.False(map.Connected(inside, outside), "the pocket is closed");
        Assert.NotEqual(map.AreaOf(free.X, free.Z), map.AreaOf(free.X + 3, free.Z));
        Assert.All(ring, tree => Assert.True(map.Connected(outside, tree.Position) && map.Connected(inside, tree.Position), "a trunk borders both"));

        host.Objects.Remove(ring[0].Id);
        Assert.True(map.Connected(inside, outside), "open again");
    }

    private static SimulationHost Island(IDecisionPort? port = null)
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, port ?? new MockDecisionPort(), "de", "start_island");
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

    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
