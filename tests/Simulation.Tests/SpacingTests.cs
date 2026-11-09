using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
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

    private static (World World, InhabitantsSystem Inhabitants, WorldObjects Objects) NewWorldWithObjects(Position first, Position second)
    {
        var world = new World(1, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Content.Knowledge,
            EventKinds = Content.EventKinds,
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

    /// <summary>A decider that never answers.</summary>
    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
