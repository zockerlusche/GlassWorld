using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// AK1a (A1630, D1046 v2): an inhabitant holds one thing in its hand. The thing stays in the world and follows it; nobody
/// else can act on it; putting it down leaves it at the feet; eating it from the hand frees the hand. Which actions take
/// and put down is data (timing holds / releases).
/// </summary>
public sealed class CarryingTests
{
    private const string Portable = "apple";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    private static string TakeAction => Content.Value.Actions.OfferedActions.Concat(["pick_up"]).First(a => Content.Value.Actions.Timing(a).Holds);

    private static string PutAction => Content.Value.Actions.OfferedActions.Concat(["drop"]).First(a => Content.Value.Actions.Timing(a).Releases);

    [Fact]
    public void ATakenThingIsHeldFollowsAndIsPutDownAtTheFeet()
    {
        var (host, a) = World();
        var apple = host.Objects.Spawn(Portable, new Position(0.5, 0));
        Do(host, a, TakeAction, apple.Id);
        Assert.Equal(apple.Id, a.Held);
        Assert.Equal(a.Id, host.Objects.HeldBy(apple.Id));

        // It follows the carrier; on the ground it is not (fire, shade and the others' sight skip it).
        a.Position = new Position(4, 3);
        host.World.Step();
        Assert.True(host.Objects.TryGet(apple.Id, out var carried));
        Assert.Equal(a.Position, carried.Position);
        var near = new List<WorldObject>();
        host.Objects.Within(a.Position, 1, near);
        Assert.DoesNotContain(near, o => o.Id == apple.Id);

        Do(host, a, PutAction, apple.Id);
        Assert.Null(a.Held);
        Assert.Null(host.Objects.HeldBy(apple.Id));
        near.Clear();
        host.Objects.Within(a.Position, 1, near);
        Assert.Contains(near, o => o.Id == apple.Id);
    }

    [Fact]
    public void AHandHoldsOneThingAndOnlyWhatTheDataLetItTake()
    {
        var (host, a) = World();
        var first = host.Objects.Spawn(Portable, new Position(0.5, 0));
        var second = host.Objects.Spawn(Portable, new Position(0, 0.5));
        var heavy = Content.Value.Objects.TypeIds.First(t => !Content.Value.Objects.TrueProperties(t).Contains("portable"));
        var rock = host.Objects.Spawn(heavy, new Position(-0.5, 0));

        Do(host, a, TakeAction, rock.Id);
        Assert.Null(a.Held);
        Do(host, a, TakeAction, first.Id);
        Do(host, a, TakeAction, second.Id);
        Assert.Equal(first.Id, a.Held);
        Assert.Null(host.Objects.HeldBy(second.Id));
    }

    [Fact]
    public void AThingInSomeoneElsesHandIsNoTargetAndEatingFromTheHandFreesIt()
    {
        var (host, a) = World();
        var b = Person(host, "inh-000002", new Position(1, 0));
        var apple = host.Objects.Spawn(Portable, new Position(0.5, 0));
        Do(host, a, TakeAction, apple.Id);

        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        b.Activity = new Activity("eat", apple.Id, apple.Position, null);
        host.World.Step();
        Assert.Contains(events, e => e.Kind == InhabitantsSystem.ActionCancelled && e.InhabitantId == b.Id);
        Assert.True(host.Objects.TryGet(apple.Id, out _));

        Do(host, a, "eat", apple.Id);
        Assert.False(host.Objects.TryGet(apple.Id, out _));
        Assert.Null(a.Held);
    }

    [Fact]
    public void WhatIsHeldSurvivesSavingAndLoadingWithTheSameHash()
    {
        var (host, a) = World();
        var before = host.World.StateHash();
        var apple = host.Objects.Spawn(Portable, new Position(0.5, 0));
        Do(host, a, TakeAction, apple.Id);
        Assert.NotEqual(before, host.World.StateHash());

        var path = Path.Combine(Path.GetTempPath(), "gw-carrying-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var loaded = StateCodec.Restore(Factory(), SaveGameStore.Load(path));
            Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
            Assert.Equal(apple.Id, loaded.Inhabitants.Get(a.Id).Held);
            Assert.Equal(a.Id, loaded.Objects.HeldBy(apple.Id));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".sha256");
        }
    }

    /// <summary>Gives the inhabitant the action and steps until it has finished (or a bound is reached).</summary>
    private static void Do(SimulationHost host, Inhabitant who, string action, string targetId)
    {
        Assert.True(host.Objects.TryGet(targetId, out var target));
        who.Activity = new Activity(action, targetId, target.Position, null);
        for (var i = 0; i < 2000 && who.Activity is { } doing && doing.Action == action; i++)
        {
            host.World.Step();
        }

        Busy(who);
    }

    private static void Busy(Inhabitant who) => who.Activity = new Activity("idle", null, who.Position, 100_000_000);

    private static (SimulationHost Host, Inhabitant A) World()
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 41, new MockDecisionPort(), "de");
        return (host, Person(host, "inh-000001", new Position(0, 0)));
    }

    private static Inhabitant Person(SimulationHost host, string id, Position at)
    {
        var model = Content.Value.InhabitantModel;
        var who = new Inhabitant(
            id,
            "female",
            1,
            at,
            model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
            model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal),
            model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal),
            model.WillToLive.Start);
        host.Inhabitants.Add(who);
        Busy(who);
        return who;
    }

    private static SimulationHostFactory Factory()
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new MockDecisionPort(), "de");
    }
}
