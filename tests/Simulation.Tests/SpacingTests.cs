using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// Personal space between inhabitants (Marcel W1687, W1688/B2569, W1691; TL #19976, #19981, #19991, #20001):
/// conversation distance, facing, yielding to the right, minimum separation. All values from
/// content/behaviour/gaits.json (spacing).
/// </summary>
public sealed class SpacingTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);
    private static readonly DeciderSettings Settings = ContractSerializer.Deserialize<DeciderSettings>(
        File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));

    private static Spacing Space => Content.Gaits.Data.Spacing!;

    [Fact]
    public void ActorStopsWithinConversationDistanceAndBothFaceEachOther()
    {
        var (world, inhabitants) = NewWorld(new Position(0, 0), new Position(6, 0));
        var (actor, listener) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        Stand(listener);
        actor.Activity = new Activity("show", listener.Id, listener.Position, null);

        RunUntil(world, () => actor.Activity?.RemainingTicks is not null);

        var distance = Distance(actor.Position, listener.Position);
        Assert.InRange(distance, Space.ConversationMinM, Space.ConversationMaxM);
        Assert.Equal(listener.Id, inhabitants.FacingOf(actor));
        Assert.Equal(actor.Id, inhabitants.FacingOf(listener));
    }

    [Fact]
    public void ActorTooCloseStepsBackToTheLowerConversationDistance()
    {
        var (world, inhabitants) = NewWorld(new Position(0, 0), new Position(0.3, 0));
        var (actor, listener) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        Stand(listener);
        actor.Activity = new Activity("warn", listener.Id, listener.Position, null);

        RunUntil(world, () => actor.Activity?.RemainingTicks is not null);

        Assert.InRange(Distance(actor.Position, listener.Position), Space.ConversationMinM - 1e-9, Space.ConversationMaxM);
    }

    [Fact]
    public void ListenerBusyWithItsOwnActionKeepsItsBody()
    {
        // TL #20001: one action owns legs and body; a listener that eats does not turn round.
        var (world, inhabitants) = NewWorld(new Position(0, 0), new Position(6, 0));
        var (actor, listener) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        listener.Activity = new Activity("eat", null, listener.Position, 10_000);
        actor.Activity = new Activity("teach", listener.Id, listener.Position, null);

        RunUntil(world, () => actor.Activity?.RemainingTicks is not null);

        Assert.Equal(listener.Id, inhabitants.FacingOf(actor));
        Assert.Null(inhabitants.FacingOf(listener));
    }

    [Fact]
    public void NobodyFacesWhileWalking()
    {
        var (world, inhabitants) = NewWorld(new Position(0, 0), new Position(20, 0));
        var (actor, listener) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        Stand(listener);
        actor.Activity = new Activity("show", listener.Id, listener.Position, null);
        world.Step();

        Assert.Null(actor.Activity?.RemainingTicks);
        Assert.Null(inhabitants.FacingOf(actor));
        Assert.Null(inhabitants.FacingOf(listener));
    }

    [Fact]
    public void WalkersMeetingHeadOnPassEachOtherOnTheirRight()
    {
        var (world, inhabitants) = NewWorld(new Position(0, 0), new Position(20, 0));
        var (a, b) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        a.Activity = new Activity("wander", null, new Position(20, 0), null);
        b.Activity = new Activity("wander", null, new Position(0, 0), null);

        var passed = false;
        for (var tick = 0; tick < 60 && !(a.Activity?.RemainingTicks is not null && b.Activity?.RemainingTicks is not null); tick++)
        {
            world.Step();
            Assert.True(Distance(a.Position, b.Position) >= Space.MinSeparationM - 1e-9, $"tick {tick}: {a.Position} {b.Position}");
            if (!passed && a.Position.X >= b.Position.X)
            {
                // a walks towards +x, so its right is +z; b walks towards -x, its right is -z.
                passed = true;
                Assert.True(a.Position.Z > b.Position.Z, $"not passed on the right: {a.Position} {b.Position}");
                Assert.True(a.Position.Z - b.Position.Z >= Space.MinSeparationM - 1e-9);
            }
        }

        Assert.True(passed);
        Assert.NotNull(a.Activity?.RemainingTicks);
        Assert.NotNull(b.Activity?.RemainingTicks);
    }

    [Fact]
    public void AStandingInhabitantKeepsTheMinimumSeparation()
    {
        var (world, inhabitants) = NewWorld(new Position(0, 0), new Position(5, 0));
        var (walker, stander) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        Stand(stander);
        walker.Activity = new Activity("wander", null, stander.Position, null);

        for (var tick = 0; tick < 30; tick++)
        {
            world.Step();
            Assert.True(Distance(walker.Position, stander.Position) >= Space.MinSeparationM - 1e-9, $"tick {tick}: {walker.Position}");
            Assert.Equal(new Position(5, 0), stander.Position);
        }

        Assert.NotNull(walker.Activity?.RemainingTicks);
    }

    [Theory]
    [InlineData(2.5, 0.1, 8.0)]
    [InlineData(1.2, 0.0, 2.0)]
    public void AStandingInhabitantInTheWayIsNeverWalkedThrough(double standX, double standZ, double goalX)
    {
        // W1695: the game shows each tick as a straight walk; a goal behind someone must not lead through them
        // (the second case once jumped to the free spot behind the other when it ended a step too close).
        var (world, inhabitants) = NewWorld(new Position(0, 0), new Position(standX, standZ));
        var (walker, stander) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        Stand(stander);
        walker.Activity = new Activity("wander", null, new Position(goalX, 0), null);
        for (var tick = 0; tick < 40 && walker.Activity?.RemainingTicks is null; tick++)
        {
            var (w0, s0) = (walker.Position, stander.Position);
            world.Step();
            Assert.True(Closest(w0, walker.Position, s0, stander.Position) >= Space.MinSeparationM - 1e-6, $"tick {tick}: {w0} -> {walker.Position}");
        }

        Assert.NotNull(walker.Activity?.RemainingTicks);
        Assert.True(walker.Position.X > stander.Position.X, "it got past");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(1.1)]
    public void WalkersCrossingEachOtherNeverPassThrough(double offset)
    {
        // W1695: crossing paths (not head-on), both walking at once, at every moment of the shown move.
        var (world, inhabitants) = NewWorld(new Position(-6, 0), new Position(offset, -6));
        var (a, b) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        a.Activity = new Activity("wander", null, new Position(6, 0), null);
        b.Activity = new Activity("wander", null, new Position(offset, 6), null);
        for (var tick = 0; tick < 60 && !(a.Activity?.RemainingTicks is not null && b.Activity?.RemainingTicks is not null); tick++)
        {
            var (a0, b0) = (a.Position, b.Position);
            world.Step();
            Assert.True(Closest(a0, a.Position, b0, b.Position) >= Space.MinSeparationM - 1e-6, $"tick {tick}: {a0}->{a.Position} {b0}->{b.Position}");
        }

        Assert.NotNull(a.Activity?.RemainingTicks);
        Assert.NotNull(b.Activity?.RemainingTicks);
    }

    [Fact]
    public void TwoGoingForTheSameThingStandApart()
    {
        var (world, inhabitants, objects) = NewWorldWithObjects(new Position(0, 0), new Position(0, 0.1));
        var water = objects.Spawn("spring_water", new Position(6, 0));
        var (a, b) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        a.Activity = new Activity("drink", water.Id, water.Position, null);
        b.Activity = new Activity("drink", water.Id, water.Position, null);

        RunUntil(world, () => a.Activity?.RemainingTicks is not null && b.Activity?.RemainingTicks is not null);

        Assert.True(Distance(a.Position, b.Position) >= Space.MinSeparationM - 1e-9);
    }

    /// <summary>
    /// W1726 seed 2: two inhabitants met head-on on a shallow strip one cell wide along the map edge (cliff
    /// behind, map edge in front) and waited for each other for 25 days. On a path with no room aside the
    /// one with the higher id gives way backwards; both arrive, never closer than the separation.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WalkersMeetingHeadOnInAOneCellPathBothGetThrough(bool swapped)
    {
        var routes = new Corridor();
        var (west, east) = (new Position(6, 0.5), new Position(30, 0.5));
        var (world, inhabitants, _) = NewWorldWithObjects(swapped ? east : west, swapped ? west : east, routes);
        var (a, b) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        var (westEnd, eastEnd) = (new Position(1, 0.5), new Position(39, 0.5));
        a.Activity = new Activity("wander", null, swapped ? westEnd : eastEnd, null);
        b.Activity = new Activity("wander", null, swapped ? eastEnd : westEnd, null);
        for (var tick = 0; tick < 600 && !(a.Activity?.RemainingTicks is not null && b.Activity?.RemainingTicks is not null); tick++)
        {
            var (a0, b0) = (a.Position, b.Position);
            world.Step();
            Assert.True(Closest(a0, a.Position, b0, b.Position) >= Space.MinSeparationM - 1e-6, $"tick {tick}: {a0}->{a.Position} {b0}->{b.Position}");
        }

        Assert.True(a.Activity?.RemainingTicks is not null && b.Activity?.RemainingTicks is not null, $"stuck: {a.Position} {a.Activity} / {b.Position} {b.Activity}");
    }

    [Fact]
    public void AWalkerPushedOffItsRouteNeverEndsAStepOnGroundNobodyCanWalkOn()
    {
        // W1744 (island seed 7): giving way had pushed a walker off its route. The straight line to its next
        // waypoint then led over a blocked cell, a step ended on it, and from there nothing could be walked
        // to any more (no goal in reach, no way back): the inhabitant stood still until its needs ran out.
        var pond = new Pond();
        var destination = new Position(3.5, 1.5);
        var (world, inhabitants, _) = NewWorldWithObjects(new Position(0.5, 1.5), new Position(0.5, 0.2), pond);
        var walker = inhabitants.Get("inh-000001");
        Stand(inhabitants.Get("inh-000002"));

        // The kept route still names the cell beyond the pond as the next waypoint.
        walker.Activity = new Activity("idle", null, destination, null, [new Position(2.5, 1.5), destination]);
        for (var t = 0; t < 40 && walker.Activity is { RemainingTicks: null }; t++)
        {
            world.Step();
            Assert.True(pond.Connected(walker.Position, walker.Position), $"tick {world.Tick}: on blocked ground at {walker.Position}");
        }

        Assert.NotNull(walker.Activity?.RemainingTicks);
        Assert.True(Distance(walker.Position, destination) < 1e-6, "it walked around the pond and arrived");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AWalkWhoseNewRouteLeavesTheWalkableGroundIsCancelledInsteadOfSearchedAgainEveryTick(bool kept)
    {
        // Review GW2 #228: the search for a new route is bounded. If the first step of a route just searched
        // would end on blocked ground as well, the walk ends with a reason; the walker never stands on it.
        var ditch = new Ditch();
        var destination = new Position(2.5, 0.5);
        var (world, inhabitants, _) = NewWorldWithObjects(new Position(0.5, 0.5), new Position(3.5, 0.5), ditch);
        var walker = inhabitants.Get("inh-000001");
        Stand(inhabitants.Get("inh-000002"));
        var cancelled = new List<WorldEvent>();
        inhabitants.Reported += e =>
        {
            if (e.Kind == InhabitantsSystem.ActionCancelled && e.InhabitantId == walker.Id)
            {
                cancelled.Add(e);
            }
        };

        walker.Activity = new Activity("idle", null, destination, null, kept ? [destination] : null);
        world.Step();

        var e = Assert.Single(cancelled);
        Assert.Equal(InhabitantsSystem.NoWalkableStep, e.Detail);
        Assert.Equal(new Position(0.5, 0.5), walker.Position);
        Assert.Null(walker.Activity);
        Assert.Equal(1, ditch.Searches);
    }

    [Fact]
    public void SpacingIsDeterministic()
    {
        static string Run()
        {
            var (world, inhabitants) = NewWorld(new Position(0, 0), new Position(20, 0.2));
            var (a, b) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
            a.Activity = new Activity("wander", null, new Position(20, 0), null);
            b.Activity = new Activity("wander", null, new Position(0, 0), null);
            for (var i = 0; i < 20; i++)
            {
                world.Step();
            }

            return world.StateHash();
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void ContradictorySpacingIsRefused()
    {
        var data = ContractSerializer.Deserialize<GaitSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "behaviour", "gaits.json")));
        data.Spacing!.ConversationMaxM = data.Spacing.ConversationMinM / 2;
        var actions = Content.Vocabularies[VocabularyKind.Action].Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Throws<InvalidDataException>(() => Behaviour.GaitTable.Create(data, actions, Content.Brain.Weights, Content.InhabitantModel));
    }

    private static void Stand(Inhabitant inhabitant) => inhabitant.Activity = new Activity("idle", null, inhabitant.Position, 10_000);

    /// <summary>Closest distance of two inhabitants that both move straight over the same time.</summary>
    private static double Closest(Position a0, Position a1, Position b0, Position b1)
    {
        var (px, pz) = (a0.X - b0.X, a0.Z - b0.Z);
        var (vx, vz) = ((a1.X - a0.X) - (b1.X - b0.X), (a1.Z - a0.Z) - (b1.Z - b0.Z));
        var vv = (vx * vx) + (vz * vz);
        var t = vv < 1e-12 ? 0 : Math.Clamp(-((px * vx) + (pz * vz)) / vv, 0, 1);
        return Math.Sqrt(((px + (vx * t)) * (px + (vx * t))) + ((pz + (vz * t)) * (pz + (vz * t))));
    }

    private static double Distance(Position a, Position b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    private static void RunUntil(World world, Func<bool> done, int maxTicks = 200)
    {
        for (var i = 0; i < maxTicks && !done(); i++)
        {
            world.Step();
        }

        Assert.True(done(), "condition not reached");
    }

    /// <summary>Inhabitants 1 and 2 at the given spots; 3 and 4 far away and resting, so they never meet.</summary>
    private static (World World, InhabitantsSystem Inhabitants) NewWorld(Position first, Position second)
    {
        var (world, inhabitants, _) = NewWorldWithObjects(first, second);
        return (world, inhabitants);
    }

    private static (World World, InhabitantsSystem Inhabitants, WorldObjects Objects) NewWorldWithObjects(Position first, Position second, IRouteFinder? routes = null)
    {
        var world = new World(1, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Content.Knowledge,
            EventKinds = Content.EventKinds,
            Routes = routes,
        };
        var decisions = new DecisionsSystem(Settings, inhabitants, objects, Content.Actions, Content.Brain, Content.Vocabularies[VocabularyKind.Action], new SilentPort(), "de")
        {
            Knowledge = Content.Knowledge,
        };
        inhabitants.QuestionOpen = decisions.HasOpenQuestion;
        world.Add(objects).Add(decisions).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [first, second, new Position(1000, 0), new Position(0, 1000)]);
        Stand(inhabitants.Get("inh-000003"));
        Stand(inhabitants.Get("inh-000004"));
        foreach (var inhabitant in inhabitants.All)
        {
            foreach (var need in inhabitant.Needs.Keys.ToList())
            {
                inhabitant.Needs[need] = 1.0;
            }
        }

        return (world, inhabitants, objects);
    }

    /// <summary>
    /// The shape of seed 2's strip: a path one metre wide (z 0..1) from x 0 to 40, wider (z -3..4) only at
    /// both ends; the resting inhabitants 3 and 4 far away count as on their own ground.
    /// </summary>
    private sealed class Corridor : IRouteFinder
    {
        public Route Find(Position start, Position destination) =>
            Connected(start, destination) ? new Route(PathStatus.Found, [destination]) : new Route(PathStatus.Unreachable, []);

        public double SpeedFactor(Position at) => 1.0;

        public bool Connected(Position a, Position b) => On(a) == On(b) && On(a) != 0;

        private static int On(Position p)
        {
            if (p.X > 500 || p.Z > 500)
            {
                return 2;
            }

            var wide = p.X < 3 || p.X > 37;
            return p.X >= 0 && p.X <= 40 && p.Z >= (wide ? -3 : 0) && p.Z < (wide ? 4 : 1) ? 1 : 0;
        }
    }

    /// <summary>
    /// Cells of one metre, 4 wide (x 0..4) and 3 deep (z 0..3), with a blocked cell (1, 1) in the middle of
    /// the west side; slow ground, so one step is shorter than a cell. Routes lead from cell centre to cell
    /// centre as on the island (the last waypoint is the destination itself).
    /// </summary>
    private sealed class Pond : IRouteFinder
    {
        private const int Width = 4;
        private const int Depth = 3;

        public Route Find(Position start, Position destination)
        {
            if (Cell(start) is not { } from || Cell(destination) is not { } to || !Walkable(to))
            {
                return new Route(PathStatus.Unreachable, []);
            }

            // Breadth first over the four neighbours, in a fixed order.
            var came = new Dictionary<(int X, int Z), (int X, int Z)> { [from] = from };
            var queue = new Queue<(int X, int Z)>([from]);
            while (queue.TryDequeue(out var cell) && cell != to)
            {
                foreach (var next in new[] { (cell.X + 1, cell.Z), (cell.X, cell.Z + 1), (cell.X - 1, cell.Z), (cell.X, cell.Z - 1) })
                {
                    if (Walkable(next) && came.TryAdd(next, cell))
                    {
                        queue.Enqueue(next);
                    }
                }
            }

            if (!came.TryGetValue(to, out var before))
            {
                return new Route(PathStatus.Unreachable, []);
            }

            var points = new List<Position> { destination };
            for (var cell = before; cell != from && to != from; cell = came[cell])
            {
                points.Insert(0, new Position(cell.X + 0.5, cell.Z + 0.5));
            }

            return new Route(PathStatus.Found, points);
        }

        public double SpeedFactor(Position at) => 0.5;

        public bool Connected(Position a, Position b) => Area(a) == Area(b) && Area(a) != 0;

        private static int Area(Position p) => p.X > 500 || p.Z > 500 ? 2 : Cell(p) is { } cell && Walkable(cell) ? 1 : 0;

        private static (int X, int Z)? Cell(Position p) =>
            p.X >= 0 && p.X < Width && p.Z >= 0 && p.Z < Depth ? ((int)Math.Floor(p.X), (int)Math.Floor(p.Z)) : null;

        private static bool Walkable((int X, int Z) cell) =>
            cell.X >= 0 && cell.X < Width && cell.Z >= 0 && cell.Z < Depth && cell != (1, 1);
    }

    /// <summary>
    /// Ground from x 0 to 4 (z 0..1) with a ditch nobody can walk on from x 1 to 2, and a route finder that
    /// is wrong about it: both sides count as one area and every route is the straight line across. Slow
    /// ground, so one step ends in the ditch.
    /// </summary>
    private sealed class Ditch : IRouteFinder
    {
        public int Searches { get; private set; }

        public Route Find(Position start, Position destination)
        {
            Searches++;
            return new Route(PathStatus.Found, [destination]);
        }

        public double SpeedFactor(Position at) => 0.5;

        public bool Connected(Position a, Position b) => Area(a) == Area(b) && Area(a) != 0;

        private static int Area(Position p) =>
            p.X > 500 || p.Z > 500 ? 2 : p.X >= 0 && p.X < 4 && p.Z >= 0 && p.Z < 1 && (p.X < 1 || p.X >= 2) ? 1 : 0;
    }

    /// <summary>A decider that never answers.</summary>
    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
