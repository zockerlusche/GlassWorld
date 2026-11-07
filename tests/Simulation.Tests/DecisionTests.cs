using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.View;

namespace GlassWorld.Simulation.Tests;

public sealed class DecisionTests
{
    private static readonly ContentSet Content = ContentSet.Load(Path.Combine(TestData.Root, "content"));
    private static readonly DeciderSettings Settings = ContractSerializer.Deserialize<DeciderSettings>(
        File.ReadAllText(Path.Combine(TestData.Root, "content", "decider", "settings.json")));

    [Fact]
    public void UnknownObjectBecomesAValidatedRequest()
    {
        var w = NewWorld(1, new MockDecisionPort());
        w.Objects.Spawn("blue_berry", new Position(2, 0));
        w.World.Step();

        var request = Assert.Single(w.Decisions.Log.Entries, e => e.Kind == DecisionLogKind.Request);
        Assert.Empty(ContractValidator.Validate("decision-request", request.Json));
        var parsed = ContractSerializer.Deserialize<DecisionRequest>(request.Json);
        Assert.Contains(parsed.Perception.Objects, o => o.Type == "blue_berry" && o.KnownProperties.Count == 0);
        Assert.Contains(parsed.Options, o => o.Action == "eat" && o.Target != null);
        Assert.Single(w.Decisions.OpenQuestionIds("inh-000001"));
    }

    [Fact]
    public void AnswerTakesEffectAtTheNextTickBoundary()
    {
        var w = NewWorld(1, new MockDecisionPort());
        w.Objects.Spawn("blue_berry", new Position(2, 0));
        w.World.Step();
        var asked = w.Decisions.Log.Entries.Single(e => e.Kind == DecisionLogKind.Request);
        Assert.DoesNotContain(w.Decisions.Log.Entries, e => e.Kind == DecisionLogKind.Response);

        w.World.Step();
        var answered = w.Decisions.Log.Entries.Single(e => e.Kind == DecisionLogKind.Response);
        Assert.Equal(asked.Tick + 1, answered.Tick);
        Assert.Empty(w.Decisions.OpenQuestionIds("inh-000001"));

        var response = ContractSerializer.Deserialize<DecisionResponse>(answered.Json);
        var request = ContractSerializer.Deserialize<DecisionRequest>(asked.Json);
        var option = request.Options.Single(o => o.OptionId == response.OptionId);
        var activity = w.Inhabitants.Get("inh-000001").Activity;
        Assert.NotNull(activity);
        Assert.Equal(option.Action, activity.Action);
    }

    [Fact]
    public void ReplayFromTheLogGivesTheSameHashes()
    {
        var original = NewWorld(7, new MockDecisionPort());
        Feed(original.Objects);
        var hashes = new List<string>();
        for (var day = 0; day < 3; day++)
        {
            original.World.Run(original.World.Calendar.TicksPerDay);
            hashes.Add(original.World.StateHash());
        }

        Assert.Contains(original.Decisions.Log.Entries, e => e.Kind == DecisionLogKind.Response);
        var replay = NewWorld(7, new ReplayDecisionPort(original.Decisions.Log));
        Feed(replay.Objects);
        for (var day = 0; day < 3; day++)
        {
            replay.World.Run(replay.World.Calendar.TicksPerDay);
            Assert.Equal(hashes[day], replay.World.StateHash());
        }
    }

    [Fact]
    public void OpenQuestionsAreStateAndInTheHash()
    {
        var waiting = NewWorld(3, new SilentPort());
        var none = NewWorld(3, new SilentPort(), triggers: false);
        waiting.Objects.Spawn("blue_berry", new Position(2, 0));
        none.Objects.Spawn("blue_berry", new Position(2, 0));
        waiting.World.Run(10);
        none.World.Run(10);

        Assert.Single(waiting.Decisions.Open);
        Assert.Empty(none.Decisions.Open);
        Assert.NotEqual(waiting.World.StateHash(), none.World.StateHash());
    }

    [Fact]
    public void QuestionsAreLimitedAndNotRepeatedDuringCooldown()
    {
        var w = NewWorld(3, new SilentPort());
        for (var i = 0; i < 5; i++)
        {
            w.Objects.Spawn(i % 2 == 0 ? "blue_berry" : "flint", new Position(2 + i, 0));
        }

        w.World.Run(120);
        Assert.True(w.Decisions.OpenQuestionIds("inh-000001").Count <= Settings.MaxOpenQuestions);
        Assert.Equal(w.Decisions.Log.Entries.Count(e => e.Kind == DecisionLogKind.Request), w.Decisions.Open.Count());
    }

    [Fact]
    public void InvalidAnswerIsLoggedAndClosesTheQuestionWithoutCrash()
    {
        var port = new ScriptedPort(_ => "{ \"not\": \"a response\" }");
        var w = NewWorld(1, port);
        w.Objects.Spawn("blue_berry", new Position(2, 0));
        w.World.Run(3);

        Assert.Contains(w.Decisions.Log.Entries, e => e.Kind == DecisionLogKind.Error && e.Note == ReplayNotes.ContractViolation);
        Assert.Empty(w.Decisions.OpenQuestionIds("inh-000001"));
    }

    [Fact]
    public void AnswerWithAnOptionThatWasNotOfferedIsDiscarded()
    {
        var port = new ScriptedPort(request => ContractSerializer.Serialize(new DecisionResponse
        {
            ContractVersion = "0.1",
            RequestId = request.RequestId,
            Tick = request.Tick,
            OptionId = "o99",
            Rationale = "x",
            Meta = new ResponseMeta { Source = ResponseMetaSource.Mock, Model = "mock", LatencyMs = 0, Attempts = 1 },
        }));
        var w = NewWorld(1, port);
        w.Objects.Spawn("blue_berry", new Position(2, 0));
        w.World.Run(3);

        Assert.Contains(w.Decisions.Log.Entries, e => e.Kind == DecisionLogKind.Discarded && e.Note == ReplayNotes.UnknownOption);
    }

    [Fact]
    public void ClickingAnInhabitantMakesItNoticeThePlayer()
    {
        var w = NewWorld(1, new SilentPort());
        Assert.False(w.Decisions.Submit(new PlayerCommand("throw_stone", "inh-000001")).Accepted);
        Assert.Equal("command.rejected.no_inhabitant", w.Decisions.Submit(new PlayerCommand(DecisionsSystem.InhabitantClicked, "inh-999999")).ReasonKey);
        Assert.True(w.Decisions.Submit(new PlayerCommand(DecisionsSystem.InhabitantClicked, "inh-000002")).Accepted);

        w.World.Step();
        var question = Assert.Single(w.Decisions.Open);
        Assert.Equal((DecisionsSystem.NoticedPlayer, "inh-000002"), (question.Kind, question.InhabitantId));
    }

    [Fact]
    public void SnapshotIsADetachedCopyWithOpenQuestionIds()
    {
        var w = NewWorld(1, new SilentPort());
        w.Objects.Spawn("blue_berry", new Position(2, 0));
        w.World.Step();
        var snapshot = WorldView.Snapshot(w.World, w.Inhabitants, w.Objects, w.Decisions);

        var first = snapshot.Inhabitants[0];
        Assert.Equal("inh-000001", first.Id);
        Assert.Single(first.OpenQuestionIds);
        var hungerBefore = first.Needs["hunger"];
        w.World.Run(600);
        Assert.Equal(hungerBefore, first.Needs["hunger"]);
        Assert.Contains(snapshot.Events, e => e.Kind == "question_opened");
    }

    [Fact]
    public void RequestIdsAreDeterministicUuidV8()
    {
        var a = DecisionIds.RequestId(1, 100, "inh-000001", 1);
        Assert.Equal(a, DecisionIds.RequestId(1, 100, "inh-000001", 1));
        Assert.NotEqual(a, DecisionIds.RequestId(1, 100, "inh-000001", 2));
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-8[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", a);
    }

    private static (World World, WorldObjects Objects, InhabitantsSystem Inhabitants, DecisionsSystem Decisions) NewWorld(ulong seed, IDecisionPort port, bool triggers = true)
    {
        var world = new World(seed, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, objects, world.Calendar.Settings.TicksPerHour);
        var settings = triggers ? Settings : new DeciderSettings
        {
            ContractVersion = Settings.ContractVersion,
            DeadlineMs = Settings.DeadlineMs,
            MaxOpenQuestions = Settings.MaxOpenQuestions,
            AskCooldownHours = Settings.AskCooldownHours,
            Triggers = [],
            FallbackEnvironment = Settings.FallbackEnvironment,
        };
        var decisions = new DecisionsSystem(settings, inhabitants, objects, Content.Actions, Content.Brain, port, "de");
        world.Add(objects).Add(decisions).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [new Position(0, 0), new Position(500, 500)]);
        return (world, objects, inhabitants, decisions);
    }

    private static void Feed(WorldObjects objects)
    {
        for (var i = 0; i < 30; i++)
        {
            objects.Spawn(i % 3 == 0 ? "blue_berry" : "apple", new Position(-5 + (i % 6), 5 + (i / 6)));
        }

        objects.Spawn("spring_water", new Position(6, 6));
        objects.Spawn("red_berry", new Position(2, -2));
    }

    /// <summary>A decider that never answers (slow PC, B2487).</summary>
    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }

    /// <summary>Answers every request at the next tick with a scripted JSON.</summary>
    private sealed class ScriptedPort(Func<DecisionRequest, string> answer) : IDecisionPort
    {
        private readonly Queue<PortMessage> _ready = new();

        public void Send(string requestId, string requestJson) =>
            _ready.Enqueue(new PortMessage(requestId, answer(ContractSerializer.Deserialize<DecisionRequest>(requestJson)), IsError: false));

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            var messages = _ready.ToList();
            _ready.Clear();
            return messages;
        }
    }
}
