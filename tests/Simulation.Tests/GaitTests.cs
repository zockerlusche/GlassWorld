using System.Text.Json;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>Walking speed and gaits as data (Marcel F5264 = A, content/behaviour/gaits.json).</summary>
public sealed class GaitTests
{
    private static readonly ContentSet Content = ContentSet.Load(Path.Combine(TestData.Root, "content"));
    private static readonly Position[] StartPositions = [new(0, 0), new(500, 0), new(0, 500), new(500, 500)];

    private static GaitSettings Data => Content.Gaits.Data;

    private static double Factor(string gait) => Data.Gaits.Single(g => g.Id == gait).FactorOfWalk;

    [Fact]
    public void AWalkCoversTheMetresPerTickOfItsGait()
    {
        var (world, inhabitants, objects) = NewWorld(Content.Gaits);
        var walker = Satisfied(inhabitants.Get("inh-000001"));
        var apple = objects.Spawn("apple", new Position(100, 0));
        walker.Activity = new Activity("eat", apple.Id, apple.Position, null);

        world.Step();
        Assert.Equal(Data.DefaultGait, inhabitants.GaitOf(walker));
        Assert.Equal(Data.WalkMPerTick * Factor(Data.DefaultGait), walker.Position.X, 9);

        // About 1.5 m per tick (F5264): a walk of 100 m takes many ticks, not one or two.
        world.Run(10);
        Assert.InRange(walker.Position.X, 10.0, 30.0);
    }

    [Fact]
    public void WanderStrollsAndAPressingNeedHurries()
    {
        var (world, inhabitants, objects) = NewWorld(Content.Gaits);
        var stroller = Satisfied(inhabitants.Get("inh-000001"));
        stroller.Activity = new Activity("wander", null, new Position(100, 0), null);
        var hungry = Satisfied(inhabitants.Get("inh-000002"));
        hungry.Needs["hunger"] = 0.05;
        var apple = objects.Spawn("apple", new Position(600, 0));
        hungry.Activity = new Activity("eat", apple.Id, apple.Position, null);

        world.Step();
        var wander = Data.ActionGaits.Single(a => a.Action == "wander").Gait;
        Assert.Equal(wander, inhabitants.GaitOf(stroller));
        Assert.Equal(Data.WalkMPerTick * Factor(wander), stroller.Position.X, 9);
        Assert.Equal(Data.PressingNeedGait, inhabitants.GaitOf(hungry));
        Assert.Equal(500 + (Data.WalkMPerTick * Factor(Data.PressingNeedGait!)), hungry.Position.X, 9);

        // Run only where the data maps it: nothing runs by default.
        Assert.DoesNotContain(Data.ActionGaits, a => a.Gait == "run");
        Assert.NotEqual("run", Data.PressingNeedGait);
        Assert.NotEqual("run", Data.DefaultGait);
    }

    [Fact]
    public void ChangedDataChangesSpeedAndGaitWithoutCodeChange()
    {
        var data = Clone(Data);
        data.WalkMPerTick = 3.0;
        data.ActionGaits.Add(new ActionGait { Action = "eat", Gait = "run" });
        var gaits = GaitTable.Create(data, Ids(VocabularyKind.Action), Content.Brain.Weights, Content.InhabitantModel);
        var (world, inhabitants, objects) = NewWorld(gaits);
        var runner = Satisfied(inhabitants.Get("inh-000001"));
        var apple = objects.Spawn("apple", new Position(100, 0));
        runner.Activity = new Activity("eat", apple.Id, apple.Position, null);

        world.Step();
        Assert.Equal("run", inhabitants.GaitOf(runner));
        Assert.Equal(3.0 * Factor("run"), runner.Position.X, 9);
    }

    [Fact]
    public void NoGaitWhileStandingOrActing()
    {
        var (world, inhabitants, objects) = NewWorld(Content.Gaits);
        var eater = Satisfied(inhabitants.Get("inh-000001"));
        Assert.Null(inhabitants.GaitOf(eater));
        var apple = objects.Spawn("apple", new Position(5, 0));
        eater.Activity = new Activity("eat", apple.Id, apple.Position, null);
        Assert.Equal(Data.DefaultGait, inhabitants.GaitOf(eater));
        for (var i = 0; i < 100 && eater.Activity?.RemainingTicks is null; i++)
        {
            world.Step();
        }

        Assert.NotNull(eater.Activity?.RemainingTicks);
        Assert.Null(inhabitants.GaitOf(eater));
    }

    [Fact]
    public void TheSnapshotShowsTheGait()
    {
        var host = SimulationHost.CreatePlaceholderWorld(Path.Combine(TestData.Root, "content"), 3, new MockDecisionPort(), "de");
        var walker = host.Inhabitants.Get("inh-000001");
        walker.Activity = new Activity("wander", null, new Position(walker.Position.X + 50, walker.Position.Z), null);
        var snapshot = host.Snapshot().Inhabitants.Single(i => i.Id == walker.Id);
        Assert.Equal(Data.ActionGaits.Single(a => a.Action == "wander").Gait, snapshot.Gait);

        // The gait is derived from activity and needs: taking snapshots changes nothing in the state.
        var hash = host.World.StateHash();
        _ = host.Snapshot();
        Assert.Equal(hash, host.World.StateHash());
        host.World.Step();
        Assert.Equal(host.Inhabitants.GaitOf(walker), host.Snapshot().Inhabitants.Single(i => i.Id == walker.Id).Gait);
    }

    [Fact]
    public void GaitDataIsChecked()
    {
        var unknownGait = Clone(Data);
        unknownGait.DefaultGait = "gallop";
        var unknownAction = Clone(Data);
        unknownAction.ActionGaits.Add(new ActionGait { Action = "fly", Gait = "walk" });
        var twice = Clone(Data);
        twice.Gaits.Add(new Gait { Id = "walk", FactorOfWalk = 2.0 });
        foreach (var data in new[] { unknownGait, unknownAction, twice })
        {
            Assert.Throws<InvalidDataException>(() => GaitTable.Create(data, Ids(VocabularyKind.Action), Content.Brain.Weights, Content.InhabitantModel));
        }
    }

    [Fact]
    public void EveryGaitOfTheCoreHasAnAnimationGaitOfTheSameId()
    {
        var library = ContractSerializer.Deserialize<AnimationLibrary>(File.ReadAllText(Path.Combine(TestData.Root, "content", "animation", "library.json")));
        var animated = library.Gaits.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(Content.Gaits.Gaits, g => Assert.Contains(g, animated));
    }

    private static Inhabitant Satisfied(Inhabitant inhabitant)
    {
        foreach (var need in inhabitant.Needs.Keys.ToList())
        {
            inhabitant.Needs[need] = 1.0;
        }

        return inhabitant;
    }

    private static (World World, InhabitantsSystem Inhabitants, WorldObjects Objects) NewWorld(GaitTable gaits)
    {
        var world = new World(1, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, gaits, objects, world.Calendar.Settings.TicksPerHour);
        world.Add(objects).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, StartPositions);
        return (world, inhabitants, objects);
    }

    private static HashSet<string> Ids(VocabularyKind kind) =>
        Content.Vocabularies[kind].Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
