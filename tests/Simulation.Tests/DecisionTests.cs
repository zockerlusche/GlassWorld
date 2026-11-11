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
        // An action in progress is never broken off: the decided one then waits as "planned".
        var inhabitant = w.Inhabitants.Get("inh-000001");
        var decided = inhabitant.Activity?.RemainingTicks is not null ? inhabitant.Planned : inhabitant.Activity;
        Assert.NotNull(decided);
        Assert.Equal(option.Action, decided.Action);
    }

    /// <summary>
    /// F5275 A: an action with a need-based consideration is offered only while that need suggests it -
    /// no sleep for someone wide awake, sleep for someone tired (would fail without the check: the
    /// targetless 'sleep' was always offered).
    /// </summary>
    [Fact]
    public void AnActionIsOfferedOnlyWhileItsNeedSuggestsIt()
    {
        string[] Offered(double sleep)
        {
            var w = NewWorld(1, new SilentPort());
            w.Inhabitants.Get("inh-000001").Needs["sleep"] = sleep;
            w.Objects.Spawn("blue_berry", new Position(2, 0));
            w.World.Step();
            var request = ContractSerializer.Deserialize<DecisionRequest>(w.Decisions.Log.Entries.First(e => e.Kind == DecisionLogKind.Request).Json);
            return [.. request.Options.Select(o => o.Action)];
        }

        Assert.DoesNotContain("sleep", Offered(1.0));
        Assert.Contains("sleep", Offered(0.1));

        // Actions without a need-based consideration stay offered either way.
        var needBased = Content.Brain.Weights.Considerations.Where(c => c.Need is not null).Select(c => c.Action).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(Offered(1.0), a => !needBased.Contains(a));
    }

    /// <summary>
    /// F5275 B: a queued answer expires when a need fell below its neglect threshold while it waited; it is
    /// dropped and logged (answer_expired / need_neglected) and the routine decides anew. A queued answer
    /// with every need fine still starts.
    /// </summary>
    [Fact]
    public void AQueuedAnswerExpiresWhenANeedIsNeglected()
    {
        var w = NewWorld(1, new SilentPort(), triggers: false);
        var inhabitant = w.Inhabitants.Get("inh-000001");
        var thirst = Content.InhabitantModel.Needs.Single(n => n.Id == "thirst").NeglectBelow;
        inhabitant.Needs["thirst"] = thirst / 2;
        inhabitant.Activity = null;
        inhabitant.Planned = new Activity("sleep", null, inhabitant.Position, null);
        w.World.Step();
        Assert.Null(inhabitant.Planned);
        Assert.NotEqual("sleep", inhabitant.Activity?.Action);
        var dropped = Assert.Single(w.Inhabitants.Events, e => e.Kind == InhabitantsSystem.AnswerExpired && e.InhabitantId == inhabitant.Id);
        Assert.Equal(InhabitantsSystem.NeedNeglected, dropped.Detail);
        Assert.Equal("sleep", dropped.Action);

        // A queued answer that serves the neglected need stays (review GW3 #137): thirsty with a queued drink.
        var thirsty = NewWorld(1, new SilentPort(), triggers: false);
        var drinker = thirsty.Inhabitants.Get("inh-000001");
        var spring = thirsty.Objects.Spawn("spring_water", drinker.Position);
        drinker.Needs["thirst"] = thirst / 2;
        drinker.Activity = null;
        drinker.Planned = new Activity("drink", spring.Id, spring.Position, null);
        thirsty.World.Step();
        Assert.Equal("drink", drinker.Activity?.Action);
        Assert.DoesNotContain(thirsty.Inhabitants.Events, e => e.Kind == InhabitantsSystem.AnswerExpired);

        // Every need above its threshold: the queued answer starts as before.
        var fine = NewWorld(1, new SilentPort(), triggers: false);
        var rested = fine.Inhabitants.Get("inh-000001");
        foreach (var need in Content.InhabitantModel.Needs)
        {
            rested.Needs[need.Id] = 1.0;
        }

        rested.Activity = null;
        rested.Planned = new Activity("sleep", null, rested.Position, null);
        fine.World.Step();
        Assert.Equal("sleep", rested.Activity?.Action);
    }

    [Fact]
    public void AnAnswerNeverBreaksOffAnActionInProgress()
    {
        var w = NewWorld(1, new SilentPort());
        var sleeper = w.Inhabitants.Get("inh-000001");
        sleeper.Activity = new Activity("sleep", null, sleeper.Position, null);
        w.World.Step();
        Assert.NotNull(sleeper.Activity!.RemainingTicks);

        var port = new MockDecisionPort();
        var answered = NewWorld(1, port);
        var busy = answered.Inhabitants.Get("inh-000001");
        busy.Activity = new Activity("sleep", null, busy.Position, null);
        answered.Objects.Spawn("blue_berry", new Position(2, 0));
        answered.World.Run(3);
        Assert.Equal("sleep", busy.Activity!.Action);
        Assert.NotNull(busy.Planned);
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
            ContractVersion = ContractInfo.Version,
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
        Assert.Equal(DecisionsSystem.ReasonUnknownCommand, w.Decisions.Submit(new PlayerCommand("throw_stone", "inh-000001")).ReasonKey);
        Assert.Equal(DecisionsSystem.ReasonNoInhabitant, w.Decisions.Submit(new PlayerCommand(DecisionsSystem.InhabitantClicked, "inh-999999")).ReasonKey);
        Assert.Equal(DecisionsSystem.ReasonNoInhabitant, w.Decisions.Submit(new PlayerCommand(DecisionsSystem.InhabitantClicked, null)).ReasonKey);
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
    public void RetryableErrorKeepsTheQuestionOpenAndAsksAgain()
    {
        var port = new ScriptedPort(request => ContractSerializer.Serialize(new ServiceError
        {
            ContractVersion = ContractInfo.Version,
            RequestId = request.RequestId,
            Tick = request.Tick,
            Code = ServiceErrorCode.Overloaded,
            Message = "busy",
            Retryable = true,
        }), isError: true);
        var w = NewWorld(1, port);
        w.Objects.Spawn("blue_berry", new Position(2, 0));
        w.World.Run(3);
        var question = Assert.Single(w.Decisions.Open);
        Assert.NotNull(question.RetryAtTick);

        w.World.Run(question.RetryAtTick!.Value - w.World.Tick + 1);
        Assert.Equal(2, w.Decisions.Log.Entries.Count(e => e.Kind == DecisionLogKind.Request));
        Assert.Equal(question.Id, Assert.Single(w.Decisions.Open).Id);
    }

    [Fact]
    public void NonRetryableErrorClosesTheQuestion()
    {
        var port = new ScriptedPort(request => ContractSerializer.Serialize(new ServiceError
        {
            ContractVersion = ContractInfo.Version,
            RequestId = request.RequestId,
            Tick = request.Tick,
            Code = ServiceErrorCode.Bad_request,
            Message = "bad",
            Retryable = false,
        }), isError: true);
        var w = NewWorld(1, port);
        w.Objects.Spawn("blue_berry", new Position(2, 0));
        w.World.Run(3);
        Assert.Empty(w.Decisions.Open);
        Assert.Contains(w.Inhabitants.Events, e => e.Kind == "question_failed");
    }

    [Fact]
    public void PlayerCommandsAreReplayedFromTheLog()
    {
        var original = NewWorld(4, new MockDecisionPort());
        original.World.Run(10);
        original.Decisions.Submit(new PlayerCommand(DecisionsSystem.InhabitantClicked, "inh-000002"));
        original.World.Run(200);
        Assert.Contains(original.Decisions.Log.Entries, e => e.Kind == DecisionLogKind.Command);

        var replay = NewWorld(4, new ReplayDecisionPort(original.Decisions.Log));
        replay.Decisions.LoadReplay(original.Decisions.Log);
        replay.World.Run(210);
        Assert.Equal(original.World.StateHash(), replay.World.StateHash());
    }

    [Fact]
    public void OnlyObjectActionsGetAnObjectTarget()
    {
        var w = NewWorld(1, new SilentPort());
        w.Objects.Spawn("blue_berry", new Position(2, 0));
        w.World.Step();
        var request = ContractSerializer.Deserialize<DecisionRequest>(w.Decisions.Log.Entries.Single(e => e.Kind == DecisionLogKind.Request).Json);
        var targets = Content.Vocabularies[VocabularyKind.Action].Entries.ToDictionary(e => e.Id, e => e.Target);
        Assert.All(request.Options, o => Assert.Equal(targets[o.Action] == VocabularyEntryTarget.Object, o.Target is not null));
        Assert.DoesNotContain(request.Options, o => targets[o.Action] == VocabularyEntryTarget.Inhabitant);
    }

    [Fact]
    public void EventsSinceDeliversEverythingAfterAGivenNumber()
    {
        var w = NewWorld(2, new MockDecisionPort());
        Feed(w.Objects);
        var seen = 0L;
        var all = new List<SequencedEvent>();
        for (var frame = 0; frame < 50; frame++)
        {
            w.World.Run(64);
            var fresh = w.Inhabitants.EventsSince(seen);
            all.AddRange(fresh);
            if (fresh.Count > 0)
            {
                seen = fresh[^1].Seq;
            }
        }

        Assert.NotEmpty(all);
        Assert.Equal(Enumerable.Range(1, all.Count).Select(i => (long)i), all.Select(e => e.Seq));
        Assert.Equal(w.Inhabitants.LastEventSeq, seen);
    }

    [Fact]
    public void ThoughtOfAnAnswerBecomesAnEventWithItsText()
    {
        var port = new ScriptedPort(request => ContractSerializer.Serialize(new DecisionResponse
        {
            ContractVersion = ContractInfo.Version,
            RequestId = request.RequestId,
            Tick = request.Tick,
            OptionId = request.Options.First().OptionId,
            Rationale = "x",
            Thought = new Thought { Locale = Locale.De, Text = "Was ist das?" },
            Meta = new ResponseMeta { Source = ResponseMetaSource.Mock, Model = "mock", LatencyMs = 0, Attempts = 1 },
        }));
        var w = NewWorld(1, port);
        w.Objects.Spawn("blue_berry", new Position(2, 0));
        w.World.Run(3);

        var thought = Assert.Single(w.Inhabitants.EventsSince(0), e => e.Event.Kind == "thought").Event;
        Assert.Equal(("inh-000001", "Was ist das?"), (thought.InhabitantId, thought.Text));
    }

    /// <summary>
    /// Youth protection (B2466; TL #21181): the thought of a decision is the most frequent AI text the
    /// player sees and had no content guard at all, neither in the service nor in the core. A thought the
    /// guard does not allow is not shown; the decision itself is carried out as before.
    /// </summary>
    [Fact]
    public void AThoughtTheContentGuardRefusesIsNotShownButTheDecisionStands()
    {
        using var rules = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData.Root, "content", "safety", "rules.json")));
        var blocked = rules.RootElement.GetProperty("categories")[0].GetProperty("terms_de")[0].GetString()!;
        var text = $"Ich denke an {blocked}.";
        Assert.False(Content.Inventions.Guard.Check(text).Allowed);
        var port = new ScriptedPort(request => ContractSerializer.Serialize(new DecisionResponse
        {
            ContractVersion = ContractInfo.Version,
            RequestId = request.RequestId,
            Tick = request.Tick,
            OptionId = request.Options.First().OptionId,
            Rationale = "x",
            Thought = new Thought { Locale = Locale.De, Text = text },
            Meta = new ResponseMeta { Source = ResponseMetaSource.Mock, Model = "mock", LatencyMs = 0, Attempts = 1 },
        }));
        var w = NewWorld(1, port);
        w.Objects.Spawn("blue_berry", new Position(2, 0));
        w.World.Run(3);

        var events = w.Inhabitants.EventsSince(0);
        Assert.DoesNotContain(events, e => e.Event.Kind == "thought");
        Assert.Contains(events, e => e.Event.Kind == "question_answered");
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
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour);
        var settings = triggers ? Settings : new DeciderSettings
        {
            ContractVersion = Settings.ContractVersion,
            DeadlineMs = Settings.DeadlineMs,
            MaxOpenQuestions = Settings.MaxOpenQuestions,
            AskCooldownHours = Settings.AskCooldownHours,
            RetryAfterMinutes = Settings.RetryAfterMinutes,
            Triggers = [],
            FallbackEnvironment = Settings.FallbackEnvironment,
        };
        // The thought guard as the host sets it (SimulationHost.Assemble).
        var decisions = new DecisionsSystem(settings, inhabitants, objects, Content.Actions, Content.Brain, Content.Vocabularies[VocabularyKind.Action], port, "de")
        {
            ThoughtGuard = Content.Inventions.Guard,
        };
        world.Add(objects).Add(decisions).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [new Position(0, 0), new Position(500, 500), new Position(1000, 0), new Position(0, 1000)]);
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
    private sealed class ScriptedPort(Func<DecisionRequest, string> answer, bool isError = false) : IDecisionPort
    {
        private readonly Queue<PortMessage> _ready = new();

        public void Send(string requestId, string requestJson) =>
            _ready.Enqueue(new PortMessage(requestId, answer(ContractSerializer.Deserialize<DecisionRequest>(requestJson)), isError));

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            var messages = _ready.ToList();
            _ready.Clear();
            return messages;
        }
    }
}
