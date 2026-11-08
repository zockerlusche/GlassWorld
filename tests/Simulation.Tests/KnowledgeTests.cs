using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>B4a-S5: knowledge with its source, trust between inhabitants, passing on, waiting for answers.</summary>
public sealed class KnowledgeTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);
    private static readonly DeciderSettings Settings = ContractSerializer.Deserialize<DeciderSettings>(
        File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public KnowledgeTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void TryingSomethingGivesCertainExperiencedKnowledgeAndAnEvent()
    {
        var w = NewWorld(new SilentPort());
        var events = new List<WorldEvent>();
        w.Inhabitants.Reported += events.Add;
        var eater = w.Inhabitants.Get("inh-000001");
        eater.Needs["hunger"] = 0.2;
        var berry = w.Objects.Spawn("blue_berry", new Position(1, 0));
        eater.Activity = new Activity("eat", berry.Id, berry.Position, null);
        RunUntil(w.World, () => eater.Activity is null);

        Assert.True(eater.Beliefs.TryGet("blue_berry", "edible", out var edible));
        Assert.True(edible.Value);
        Assert.Equal(KnowledgeSources.Experienced, edible.Source);
        Assert.Equal(Content.Knowledge.ExperiencedConfidence, edible.Confidence);
        Assert.Null(edible.ToldBy);
        Assert.True(edible.Tick > 0);
        var gained = Assert.Single(events, e => e.Kind == InhabitantsSystem.KnowledgeGained && e.Detail == "edible/experienced");
        Assert.Equal(("inh-000001", "blue_berry"), (gained.InhabitantId, gained.TargetId));
    }

    [Theory]
    [InlineData(0.8)]
    [InlineData(0.3)]
    public void WhatIsHeardIsAsSureAsTheTrustInTheTeller(double trust)
    {
        var w = NewWorld(new SilentPort());
        var teller = w.Inhabitants.Get("inh-000001");
        var listener = w.Inhabitants.Get("inh-000002");
        teller.Beliefs.Set("red_berry", "poisonous", true, 1.0, KnowledgeSources.Experienced, null, 1);
        listener.Relationships.Set(teller.Id, new Relation(0.3, trust, 0.5));
        teller.Activity = new Activity("warn", listener.Id, listener.Position, null);
        RunUntil(w.World, () => teller.Activity is null);

        Assert.True(listener.Beliefs.TryGet("red_berry", "poisonous", out var heard));
        Assert.True(heard.Value);
        Assert.Equal(KnowledgeSources.Heard, heard.Source);
        Assert.Equal(teller.Id, heard.ToldBy);
        Assert.Equal(trust, heard.Confidence, 9);
    }

    [Fact]
    public void FalseKnowledgeIsPassedOnButNeverOverwritesOwnExperience()
    {
        var w = NewWorld(new SilentPort());
        var teller = w.Inhabitants.Get("inh-000001");
        var listener = w.Inhabitants.Get("inh-000002");

        // The teller is wrong: blue berries are edible. "teach" carries any belief.
        teller.Beliefs.Set("blue_berry", "edible", false, 0.9, KnowledgeSources.Experienced, null, 1);
        teller.Activity = new Activity("teach", listener.Id, listener.Position, null);
        RunUntil(w.World, () => teller.Activity is null);
        Assert.True(listener.Beliefs.TryGet("blue_berry", "edible", out var heard) && !heard.Value && heard.Source == KnowledgeSources.Heard);

        listener.Beliefs.Set("blue_berry", "edible", true, 1.0, KnowledgeSources.Experienced, null, 2);
        Assert.Null(InhabitantsSystem.PassableBelief(Content.Knowledge, teller, listener, "teach"));
    }

    [Theory]
    [InlineData(false, 0.2)]
    [InlineData(false, 0.4)]
    [InlineData(true, 0.2)]
    public void ExperienceThatContradictsHearsayLowersTrustInTheTeller(bool heardValue, double loss)
    {
        var knowledge = Knowledge();
        knowledge.TrustFalseHeardLoss = loss;
        var w = NewWorld(new SilentPort(), knowledge);
        var listener = w.Inhabitants.Get("inh-000001");
        listener.Needs["hunger"] = 0.2;
        listener.Beliefs.Set("blue_berry", "edible", heardValue, 0.4, KnowledgeSources.Heard, "inh-000002", 1);
        var berry = w.Objects.Spawn("blue_berry", new Position(1, 0));
        listener.Activity = new Activity("eat", berry.Id, berry.Position, null);
        RunUntil(w.World, () => listener.Activity is null);

        var start = knowledge.RelationshipStart.Trust;
        var expected = heardValue ? start + knowledge.TrustTrueHeardGain : start - loss;
        Assert.Equal(expected, listener.Relationships.Get("inh-000002", default).Trust, 9);
        Assert.True(listener.Beliefs.TryGet("blue_berry", "edible", out var own) && own.Value && own.Source == KnowledgeSources.Experienced);
    }

    [Fact]
    public void TheRoutineDoesNotEatWhatItAskedAboutUntilTheAnswerComes()
    {
        // The decider never answers: the question stays open, the unknown berry stays uneaten.
        var waiting = Hungry(NewWorld(new SilentPort()));
        var started = new List<WorldEvent>();
        waiting.Inhabitants.Reported += e => started.Add(e);
        RunUntil(waiting.World, () => false, 2_000);
        Assert.Single(waiting.Decisions.OpenQuestionIds("inh-000001"));
        Assert.False(waiting.Inhabitants.Get("inh-000001").Beliefs.TryGet("blue_berry", "edible", out _));
        Assert.DoesNotContain(started, e => e.Kind == InhabitantsSystem.ActionStarted && e.Action == "eat");

        // Data change: without the waiting list the routine tries the berry anyway (B1 behaviour).
        var knowledge = Knowledge();
        knowledge.WaitWhileQuestionOpen = [];
        var eager = Hungry(NewWorld(new SilentPort(), knowledge));
        RunUntil(eager.World, () => eager.Inhabitants.Get("inh-000001").Beliefs.TryGet("blue_berry", "edible", out _), 2_000);
        Assert.True(eager.Inhabitants.Get("inh-000001").Beliefs.TryGet("blue_berry", "edible", out _));
    }

    [Fact]
    public void TheDeciderGetsBeliefsWithTheirSourceButNeverTheTruth()
    {
        var w = NewWorld(new SilentPort());
        var asker = w.Inhabitants.Get("inh-000001");
        asker.Beliefs.Set("blue_berry", "edible", false, 0.35, KnowledgeSources.Heard, "inh-000002", 3);
        asker.Beliefs.Set("red_berry", "poisonous", true, 1.0, KnowledgeSources.Experienced, null, 2);
        w.Objects.Spawn("blue_berry", new Position(2, 2));
        w.Objects.Spawn("red_berry", new Position(-2, 2));
        w.Objects.Spawn("apple", new Position(2, 0));
        w.World.Step();

        var json = w.Decisions.Log.Entries.First(e => e.Kind == DecisionLogKind.Request).Json;
        Assert.Empty(ContractValidator.Validate("decision-request", json));
        var request = ContractSerializer.Deserialize<DecisionRequest>(json);
        var knowledge = request.Inhabitant.Knowledge;
        var blue = Assert.Single(knowledge, k => k.Subject.Id == "blue_berry");
        Assert.False(blue.Value); // the belief, not the catalog (blue berries are edible)
        Assert.Equal(KnowledgeSource.Heard, blue.Source);
        Assert.Equal("inh-000002", blue.ToldBy);
        Assert.Equal(0.35, blue.Confidence);
        Assert.Contains(knowledge, k => k.Subject.Id == "red_berry" && k.Source == KnowledgeSource.Experienced && k.ToldBy is null);
        Assert.DoesNotContain(knowledge, k => k.Subject.Id == "apple");
        Assert.True(knowledge.Count <= Content.Knowledge.RequestMaxKnowledge);

        var other = Assert.Single(request.Inhabitant.Relationships);
        Assert.Equal(("inh-000002", Content.Knowledge.RelationshipStart.Trust), (other.Other, other.Trust));
        Assert.Contains(request.Perception.Inhabitants, i => i.Id == "inh-000002");
        Assert.Contains(request.Options, o => o.Action == "warn" && o.Target == "inh-000002");
    }

    [Theory]
    [InlineData(32)]
    [InlineData(2)]
    public void PassingKnowledgeOnNeverPushesTheRequestOverTheContractLimit(int maxPassOn)
    {
        // Review GW2 #103: three pass-on actions x many inhabitants in sight exceeded the 32 options of the
        // contract, so every request was refused. Pass-on options are capped by data and by the contract.
        var settings = ContractSerializer.Deserialize<KnowledgeSettings>(ContractSerializer.Serialize(Content.Knowledge));
        settings.MaxPassOnOptions = maxPassOn;
        settings.RequestMaxInhabitants = 16; // the schema maximum (the contract holds 16 inhabitants)
        var w = NewWorld(new SilentPort(), settings);
        var asker = w.Inhabitants.Get("inh-000001");
        asker.Beliefs.Set("red_berry", "poisonous", true, 1.0, KnowledgeSources.Experienced, null, 2);
        for (var i = 0; i < 20; i++)
        {
            w.Inhabitants.Add(new Inhabitant(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"inh-9{i:00000}"),
                asker.Sex,
                asker.Group,
                new Position(1 + (i % 5), 1 + (i / 5)),
                new Dictionary<string, double>(asker.Needs),
                new Dictionary<string, double>(asker.Moods),
                new Dictionary<string, double>(asker.Traits),
                asker.WillToLive));
        }

        w.Objects.Spawn("red_berry", new Position(2, 2));
        w.Objects.Spawn("apple", new Position(2, 0)); // something unknown, so the asker asks
        bool AskerAsked() => w.Decisions.Log.Entries.Any(e => e.Kind == DecisionLogKind.Request
            && ContractSerializer.Deserialize<DecisionRequest>(e.Json).Inhabitant.Id == "inh-000001");
        RunUntil(w.World, AskerAsked);

        var requests = w.Decisions.Log.Entries.Where(e => e.Kind == DecisionLogKind.Request).Select(e => e.Json).ToList();
        Assert.All(requests, json => Assert.Empty(ContractValidator.Validate("decision-request", json)));
        var request = requests.Select(ContractSerializer.Deserialize<DecisionRequest>).First(r => r.Inhabitant.Id == "inh-000001");
        Assert.InRange(request.Options.Count, 1, DecisionsSystem.MaxOptions);
        var passOn = request.Options.Count(o => settings.Passing.Any(p => p.Action == o.Action));
        Assert.InRange(passOn, 1, maxPassOn);
    }

    [Fact]
    public void KnowledgeAndTrustSurviveSaveAndLoadAndReplay()
    {
        var original = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), "de");
        original.World.Run(400);
        var first = original.Inhabitants.All.First();
        first.Beliefs.Set("red_berry", "poisonous", false, 0.25, KnowledgeSources.Heard, "inh-000002", original.World.Tick);
        first.Relationships.Set("inh-000002", new Relation(0.4, 0.45, 0.6));
        var path = Path.Combine(_directory, "knowledge.db");
        SaveGameStore.Save(path, StateCodec.Capture(original));

        var loaded = Restore(SaveGameStore.Load(path), new ReplayDecisionPort(original.Decisions.Log));
        Assert.Equal(original.World.StateHash(), loaded.World.StateHash());
        var restored = loaded.Inhabitants.Get(first.Id);
        Assert.True(restored.Beliefs.TryGet("red_berry", "poisonous", out var belief));
        Assert.Equal(new Belief(false, 0.25, KnowledgeSources.Heard, "inh-000002", 400), belief);
        Assert.Equal(new Relation(0.4, 0.45, 0.6), restored.Relationships.Get("inh-000002", default));

        original.World.Run(original.World.Calendar.TicksPerDay);
        var replay = Restore(SaveGameStore.Load(path), new ReplayDecisionPort(original.Decisions.Log));
        replay.World.Run(original.World.Calendar.TicksPerDay);
        Assert.Equal(original.World.StateHash(), replay.World.StateHash());
    }

    [Fact]
    public void EveryEventKindTheCoreReportsIsInTheVocabulary()
    {
        var w = NewWorld(new SilentPort());
        Assert.Contains(InhabitantsSystem.KnowledgeGained, (IEnumerable<string>)Content.EventKinds);
        Assert.Throws<InvalidDataException>(() => w.Inhabitants.Report(new WorldEvent("not_an_event", 0, "inh-000001", null, null)));

        // A month of the start world reports only known kinds (the check throws otherwise).
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 42, new MockDecisionPort(), "de");
        var kinds = new SortedSet<string>(StringComparer.Ordinal);
        host.Inhabitants.Reported += e => kinds.Add(e.Kind);
        host.World.Run(3 * host.World.Calendar.TicksPerDay);
        Assert.Subset(Content.EventKinds.ToHashSet(StringComparer.Ordinal), kinds.ToHashSet(StringComparer.Ordinal));
        Assert.Contains(InhabitantsSystem.KnowledgeGained, kinds);
    }

    private static KnowledgeSettings Knowledge() => ContractSerializer.Deserialize<KnowledgeSettings>(
        File.ReadAllText(Path.Combine(ContentDirectory, "knowledge", "settings.json")));

    private static (World World, WorldObjects Objects, InhabitantsSystem Inhabitants, DecisionsSystem Decisions) Hungry(
        (World World, WorldObjects Objects, InhabitantsSystem Inhabitants, DecisionsSystem Decisions) w)
    {
        w.Inhabitants.Get("inh-000001").Needs["hunger"] = 0.0;
        w.Objects.Spawn("blue_berry", new Position(2, 0));
        return w;
    }

    private static (World World, WorldObjects Objects, InhabitantsSystem Inhabitants, DecisionsSystem Decisions) NewWorld(IDecisionPort port, KnowledgeSettings? knowledge = null)
    {
        knowledge ??= Content.Knowledge;
        var world = new World(1, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = knowledge,
            EventKinds = Content.EventKinds,
        };
        var decisions = new DecisionsSystem(Settings, inhabitants, objects, Content.Actions, Content.Brain, Content.Vocabularies[VocabularyKind.Action], port, "de")
        {
            Knowledge = knowledge,
        };
        inhabitants.QuestionOpen = decisions.HasOpenQuestion;
        world.Add(objects).Add(decisions).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [new Position(0, 0), new Position(4, 0), new Position(1000, 0), new Position(0, 1000)]);
        return (world, objects, inhabitants, decisions);
    }

    private static void RunUntil(World world, Func<bool> done, int maxTicks = 5_000)
    {
        for (var i = 0; i < maxTicks && !done(); i++)
        {
            world.Step();
        }
    }

    private static SimulationHost Restore(SaveState state, IDecisionPort port) =>
        StateCodec.Restore(new SimulationHostFactory(Content, Settings, port, "de"), state);

    /// <summary>A decider that never answers.</summary>
    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
