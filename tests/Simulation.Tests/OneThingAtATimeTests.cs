using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;
using Microsoft.Data.Sqlite;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1487, one thing at a time: no new question goes to the decider while a question of the inhabitant is out
/// or a choice the decider made for it is queued or under way; the question opens, waits and is asked later.
/// </summary>
public sealed class OneThingAtATimeTests : IDisposable
{
    private const string First = "blue_berry";
    private const string Second = "flint";

    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public OneThingAtATimeTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void TheDataLetOnlyThePlayerInterrupt()
    {
        var settings = Settings();
        Assert.True(settings.ChoiceProtectionHours > 0);
        Assert.Equal([TriggerKind.Noticed_player], settings.Triggers.Where(t => t.Interrupts == true).Select(t => t.Kind));
    }

    [Fact]
    public void AQuestionWaitsWhileAChoiceIsQueuedOrUnderWayAndIsAskedOnceItHasFinished()
    {
        var w = NewWorld(Settings());
        var a = w.A;
        var queued = w.QueueAChoice();

        // Queued behind the running action, with the decider's mark; what the routine does has none.
        Assert.Equal(w.World.Tick - 1, queued.DecidedTick);
        Assert.Null(a.Activity!.DecidedTick);

        // The next thing it does not understand opens its question with its question mark - nobody is asked.
        w.Objects.Spawn(Second, new Position(0, 10));
        w.World.Step();
        var held = Assert.Single(w.Decisions.Open);
        Assert.Equal((DecisionsSystem.UnknownObject, "type:" + Second, string.Empty), (held.Kind, held.Subject, held.RequestId));
        Assert.Empty(held.Options);
        Assert.Equal(1, w.Requests);
        Assert.Single(w.Events, e => e.Kind == "question_opened" && e.TargetId == held.Id);
        Assert.Equal([held.Id], w.Decisions.OpenQuestionIds(a.Id));
        Assert.Same(queued, a.Planned);

        // The running action ends: the choice is under way (walking there), the question still waits.
        a.Activity = null;
        w.World.Step();
        Assert.Null(a.Planned);
        Assert.Equal((queued.Action, queued.TargetId), (a.Activity?.Action, a.Activity?.TargetId));
        Assert.Null(a.Activity!.RemainingTicks);
        Assert.NotNull(a.Activity.DecidedTick);

        // ... and while it does it. Nothing replaced the choice: it started and finished.
        var ran = false;
        for (var t = 0; t < 300 && a.Activity?.DecidedTick is not null; t++)
        {
            ran |= a.Activity.RemainingTicks is not null;
            Assert.Equal(1, w.Requests);
            Assert.Equal(string.Empty, Assert.Single(w.Decisions.Open).RequestId);
            w.World.Step();
        }

        Assert.True(ran);
        Assert.Single(w.Events, e => e.Kind == InhabitantsSystem.ActionFinished && e.InhabitantId == a.Id && e.Action == queued.Action && e.TargetId == queued.TargetId);

        // Free again: the question that waited is asked, about the thing as it is now.
        w.World.Step();
        Assert.Equal(2, w.Requests);
        var asked = Assert.Single(w.Decisions.Open);
        Assert.Equal((held.Id, held.Subject), (asked.Id, asked.Subject));
        Assert.NotEqual(string.Empty, asked.RequestId);
        Assert.NotEmpty(asked.Options);
        Assert.Equal(w.World.Tick - 1, asked.AskedTick);
        Assert.DoesNotContain(w.Events, e => e.Kind == "question_failed");

        w.World.Step();
        Assert.Empty(w.Decisions.Open);
        Assert.Single(w.Events, e => e.Kind == "question_answered" && e.TargetId == held.Id);
    }

    [Fact]
    public void AQuestionThatWaitedLapsesWhenItsThingIsGoneAndIsRaisedAnewWithoutCooldown()
    {
        var w = NewWorld(Settings());
        var a = w.A;
        w.QueueAChoice();
        var thing = w.Objects.Spawn(Second, new Position(0, 10));
        w.World.Step();
        var held = Assert.Single(w.Decisions.Open);
        Assert.Equal(string.Empty, held.RequestId);

        // While the inhabitant is busy the question stays, whatever happens to the thing.
        Assert.True(w.Objects.Remove(thing.Id));
        w.World.Run(3);
        Assert.Equal([held.Id], w.Decisions.OpenQuestionIds(a.Id));

        w.FinishTheChoice();
        w.World.Step();

        // Free again and nothing of the kind in sight: no request without its subject; the question lapses.
        Assert.Empty(w.Decisions.Open);
        Assert.Equal(1, w.Requests);
        var lapsed = Assert.Single(w.Events, e => e.Kind == "question_failed");
        Assert.Equal((held.Id, DecisionsSystem.Lapsed), (lapsed.TargetId, lapsed.Detail));

        // No cooldown began: the moment such a thing is in sight again, the question is raised and asked.
        w.Objects.Spawn(Second, a.Position);
        w.World.Step();
        Assert.Equal(2, w.Requests);
        Assert.NotEqual(string.Empty, Assert.Single(w.Decisions.Open, q => q.Subject == "type:" + Second).RequestId);
    }

    [Fact]
    public void ATriggerThatInterruptsIsAskedAtOnceAndItsAnswerReplacesTheChoice()
    {
        // The room for open questions is taken by a waiting one: the player still goes first.
        var w = NewWorld(Settings(s => s.MaxOpenQuestions = 1));
        var a = w.A;
        var queued = w.QueueAChoice();
        w.Objects.Spawn(Second, new Position(0, 10));
        w.World.Step();
        Assert.Equal(string.Empty, Assert.Single(w.Decisions.Open).RequestId);

        Assert.True(w.Decisions.Submit(new PlayerCommand(DecisionsSystem.InhabitantClicked, a.Id)).Accepted);
        w.World.Step();
        var click = Assert.Single(w.Decisions.Open, q => q.Kind == DecisionsSystem.NoticedPlayer);
        Assert.NotEqual(string.Empty, click.RequestId);
        Assert.Equal(2, w.Requests);

        // Its answer replaces the queued choice, as every answer did before.
        w.World.Step();
        Assert.NotSame(queued, a.Planned);
        Assert.Null(a.Planned!.TargetId);
        Assert.Equal(w.World.Tick - 1, a.Planned.DecidedTick);
        Assert.Equal(string.Empty, Assert.Single(w.Decisions.Open).RequestId);
    }

    [Fact]
    public void WithoutTheFlagEvenThePlayerWaitsAndIsAskedLater()
    {
        var w = NewWorld(Settings(s => s.Triggers.Single(t => t.Kind == TriggerKind.Noticed_player).Interrupts = null));
        var a = w.A;
        var queued = w.QueueAChoice();
        Assert.True(w.Decisions.Submit(new PlayerCommand(DecisionsSystem.InhabitantClicked, a.Id)).Accepted);
        w.World.Step();
        var held = Assert.Single(w.Decisions.Open);
        Assert.Equal((DecisionsSystem.NoticedPlayer, string.Empty), (held.Kind, held.RequestId));
        Assert.Equal(1, w.Requests);
        Assert.Same(queued, a.Planned);

        // A question about nothing in the world does not lapse: it is asked once the choice has finished.
        w.FinishTheChoice();
        w.World.Step();
        Assert.Equal(2, w.Requests);
        Assert.NotEqual(string.Empty, Assert.Single(w.Decisions.Open).RequestId);
    }

    [Fact]
    public void ANeglectedNeedTheChoiceDoesNotServeLetsQuestionsThroughAsBefore()
    {
        var thirst = Content.InhabitantModel.Needs.Single(n => n.Id == "thirst").NeglectBelow;

        // On its way to a choice that quenches no thirst.
        var w = NewWorld(Settings());
        var a = w.A;
        a.Activity = new Activity("idle", null, new Position(5000, 0), null, DecidedTick: w.World.Tick);
        w.Objects.Spawn(Second, new Position(3, 0));
        w.World.Step();
        Assert.Equal(string.Empty, Assert.Single(w.Decisions.Open).RequestId);
        Assert.False(w.Inhabitants.StaleByNeglect(a, "idle"));

        // The one definition of a stale answer (neglect_below, F5275 B): from now on the choice protects nothing.
        a.Needs["thirst"] = thirst / 2;
        Assert.True(w.Inhabitants.StaleByNeglect(a, "idle"));
        w.World.Step();
        Assert.Equal(1, w.Requests);
        Assert.NotEqual(string.Empty, Assert.Single(w.Decisions.Open).RequestId);

        // A choice that serves the neglected need keeps its protection: thirsty and on the way to a drink.
        var thirsty = NewWorld(Settings());
        var drinker = thirsty.A;
        var spring = thirsty.Objects.Spawn("spring_water", new Position(50, 0));
        drinker.Needs["thirst"] = thirst / 2;
        drinker.Activity = new Activity("drink", spring.Id, spring.Position, null, DecidedTick: thirsty.World.Tick);
        Assert.False(thirsty.Inhabitants.StaleByNeglect(drinker, "drink"));
        thirsty.Objects.Spawn(Second, new Position(3, 0));
        thirsty.World.Run(5);
        Assert.Equal(0, thirsty.Requests);
        Assert.Equal(string.Empty, Assert.Single(thirsty.Decisions.Open).RequestId);
    }

    [Fact]
    public void WhatHasNoEndOfItsOwnProtectsOnlyForTheDataHours()
    {
        var settings = Settings(s => s.ChoiceProtectionHours = 0.1);
        var limit = (int)Math.Round(0.1 * Content.Calendar.Settings.TicksPerHour);
        Assert.True(limit >= 2);

        // A walk that does not arrive: after the limit the question goes out, and its answer replaces the walk.
        var w = NewWorld(settings);
        var a = w.A;
        a.Activity = new Activity("idle", null, new Position(5000, 0), null, DecidedTick: w.World.Tick);
        w.Objects.Spawn(Second, new Position(3, 0));
        w.World.Run(limit);
        Assert.Equal(0, w.Requests);
        Assert.Equal(string.Empty, Assert.Single(w.Decisions.Open).RequestId);
        w.World.Step();
        Assert.Equal(1, w.Requests);
        w.World.Step();
        Assert.Equal(w.World.Tick - 1, a.Activity!.DecidedTick);
        Assert.NotEqual(new Position(5000, 0), a.Activity.Destination);

        // A question that is out and never answered keeps the next one back for the same time only.
        var silent = NewWorld(settings, new SilentPort());
        silent.A.Activity = new Activity("idle", null, silent.A.Position, 100_000);
        silent.Objects.Spawn(First, new Position(5, 0));
        silent.World.Step();
        silent.Objects.Spawn(Second, new Position(3, 0));
        silent.World.Run(limit - 1);
        Assert.Equal(1, silent.Requests);
        Assert.Equal(2, silent.Decisions.Open.Count());
        silent.World.Step();
        Assert.Equal(2, silent.Requests);

        // What ends by itself needs no limit: an action that runs ...
        var running = NewWorld(settings);
        running.A.Activity = new Activity("idle", null, running.A.Position, 100_000, DecidedTick: 0);
        running.Objects.Spawn(Second, new Position(3, 0));
        running.World.Run(limit * 5);
        Assert.Equal(0, running.Requests);
        Assert.Equal(string.Empty, Assert.Single(running.Decisions.Open).RequestId);

        // ... and a choice queued behind a running action (its turn comes when that action ends).
        var queued = NewWorld(settings);
        queued.A.Activity = new Activity("idle", null, queued.A.Position, 100_000);
        queued.A.Planned = new Activity("idle", null, queued.A.Position, null, DecidedTick: 0);
        queued.Objects.Spawn(Second, new Position(3, 0));
        queued.World.Run(limit * 5);
        Assert.Equal(0, queued.Requests);
        Assert.Equal(string.Empty, Assert.Single(queued.Decisions.Open).RequestId);

        // When its turn comes, its walk has the whole limit again.
        queued.A.Activity = null;
        queued.A.Planned = new Activity("idle", null, new Position(5000, 0), null, DecidedTick: 0);
        queued.World.Step();
        Assert.Equal(queued.World.Tick - 1, queued.A.Activity!.DecidedTick);
        queued.World.Run(limit - 1);
        Assert.Equal(0, queued.Requests);
        queued.World.Run(2);
        Assert.Equal(1, queued.Requests);
    }

    [Fact]
    public void WithoutTheHoursQuestionsAreNeverHeldBack()
    {
        // As before the rule: the second question goes out at once and its answer replaces the queued choice.
        var w = NewWorld(Settings(s => s.ChoiceProtectionHours = null));
        var a = w.A;
        var queued = w.QueueAChoice();
        var thing = w.Objects.Spawn(Second, new Position(0, 10));
        w.World.Step();
        Assert.Equal(2, w.Requests);
        Assert.NotEqual(string.Empty, Assert.Single(w.Decisions.Open).RequestId);
        w.World.Step();
        Assert.NotSame(queued, a.Planned);
        Assert.Equal(thing.Id, a.Planned!.TargetId);
    }

    [Fact]
    public void TheMarkIsHashedAndSurvivesSaveAndLoad()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 3, new MockDecisionPort(), "de");
        var a = host.Inhabitants.Get("inh-000001");
        a.Activity = new Activity("idle", null, a.Position, 50);
        a.Planned = new Activity("wander", null, new Position(3, 4), null);
        var unmarked = host.World.StateHash();
        a.Activity = a.Activity with { DecidedTick = 7 };
        var one = host.World.StateHash();
        a.Planned = a.Planned with { DecidedTick = 9 };
        var both = host.World.StateHash();
        Assert.Equal(3, new[] { unmarked, one, both }.Distinct().Count());

        var path = Path.Combine(_directory, "marks.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        Assert.Equal(SaveGameStore.SchemaVersion, SaveGameStore.VersionOf(path));
        var loaded = Restore(SaveGameStore.Load(path));
        Assert.Equal(both, loaded.World.StateHash());
        Assert.Equal(a.Activity, loaded.Inhabitants.Get(a.Id).Activity);
        Assert.Equal(a.Planned, loaded.Inhabitants.Get(a.Id).Planned);

        // A file from before the mark (the activity records carry no such member) loads without it.
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var command = c.CreateCommand();
            command.CommandText = "UPDATE inhabitants SET activity = json_remove(activity, '$.DecidedTick'), planned = json_remove(planned, '$.DecidedTick');";
            Assert.True(command.ExecuteNonQuery() > 0);
        }

        var before = Restore(SaveGameStore.Load(path));
        Assert.Null(before.Inhabitants.Get(a.Id).Activity!.DecidedTick);
        Assert.Null(before.Inhabitants.Get(a.Id).Planned!.DecidedTick);
        Assert.Equal(unmarked, before.World.StateHash());
    }

    [Fact]
    public void TheSameSeedGivesTheSameHashesAndAReplayFromTheLogToo()
    {
        var (hashes, log, held, later) = TwoDays(MockDecisionPort.FromContent(ContentDirectory), null);

        // The rule was at work in this run: questions waited and were asked later.
        Assert.True(held > 0, "a question was held back");
        Assert.True(later > 0, "a held question was asked later");

        var (again, _, heldAgain, laterAgain) = TwoDays(MockDecisionPort.FromContent(ContentDirectory), null);
        Assert.Equal(hashes, again);
        Assert.Equal((held, later), (heldAgain, laterAgain));

        var (replayed, _, _, _) = TwoDays(new ReplayDecisionPort(log), log);
        Assert.Equal(hashes, replayed);
    }

    [Fact]
    public void AWorldWithAHeldQuestionSavesLoadsAndGoesOnAlike()
    {
        var original = SimulationHost.CreateGeneratedWorld(ContentDirectory, 1, MockDecisionPort.FromContent(ContentDirectory), "de", "start_island");
        var limit = 2 * original.World.Calendar.TicksPerDay;
        while (original.World.Tick < limit && !(original.Decisions.Open.Any() && original.Decisions.Open.All(q => q.RequestId.Length == 0)))
        {
            original.World.Step();
        }

        Assert.Contains(original.Decisions.Open, q => q.RequestId.Length == 0);
        Assert.Contains(original.Inhabitants.All, i => i.Activity?.DecidedTick is not null || i.Planned?.DecidedTick is not null);
        var path = Path.Combine(_directory, "held.db");
        SaveGameStore.Save(path, StateCodec.Capture(original));
        var loaded = Restore(SaveGameStore.Load(path), MockDecisionPort.FromContent(ContentDirectory));
        Assert.Equal(original.World.StateHash(), loaded.World.StateHash());

        // Nothing was out with the mock when it was saved, so both go on with their own mock, tick for tick.
        Assert.DoesNotContain(original.Decisions.Open, q => q.RequestId.Length > 0);
        var ticks = original.World.Calendar.TicksPerDay / 4;
        original.World.Run(ticks);
        loaded.World.Run(ticks);
        Assert.Equal(original.World.StateHash(), loaded.World.StateHash());
    }

    /// <summary>Two days on the start island; the hash after each day, the log, and how many questions waited and were asked later.</summary>
    private static (List<string> Hashes, DecisionLog Log, int Held, int Later) TwoDays(IDecisionPort port, DecisionLog? replay)
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 1, port, "de", "start_island");
        if (replay is not null)
        {
            host.Decisions.LoadReplay(replay);
        }

        var waiting = new HashSet<string>(StringComparer.Ordinal);
        var (held, later) = (0, 0);
        var hashes = new List<string>();
        for (var day = 0; day < 2; day++)
        {
            for (var t = 0; t < host.World.Calendar.TicksPerDay; t++)
            {
                host.World.Step();
                foreach (var question in host.Decisions.Open)
                {
                    if (question.RequestId.Length == 0)
                    {
                        held += waiting.Add(question.Id) ? 1 : 0;
                    }
                    else if (waiting.Remove(question.Id))
                    {
                        later++;
                    }
                }
            }

            hashes.Add(host.World.StateHash());
        }

        return (hashes, host.Decisions.Log, held, later);
    }

    private static SimulationHost Restore(SaveState state, IDecisionPort? port = null) =>
        StateCodec.Restore(new SimulationHostFactory(Content, Settings(), port ?? new MockDecisionPort(), "de"), state);

    private static DeciderSettings Settings(Action<DeciderSettings>? change = null)
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        change?.Invoke(settings);
        return settings;
    }

    /// <summary>The start inhabitants far apart on an empty plain; inh-000001 is the one the tests watch.</summary>
    private static TestWorld NewWorld(DeciderSettings settings, IDecisionPort? port = null)
    {
        var world = new World(1, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour);
        var decisions = new DecisionsSystem(settings, inhabitants, objects, Content.Actions, Content.Brain, Content.Vocabularies[VocabularyKind.Action], port ?? new FirstTargetPort(), Content.Inventions.Guard, "de");
        world.Add(objects).Add(decisions).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [new Position(0, 0), new Position(5000, 5000), new Position(10000, 0), new Position(0, 10000)]);
        var events = new List<WorldEvent>();
        inhabitants.Reported += events.Add;
        return new TestWorld(world, objects, inhabitants, decisions, events);
    }

    private sealed record TestWorld(World World, WorldObjects Objects, InhabitantsSystem Inhabitants, DecisionsSystem Decisions, List<WorldEvent> Events)
    {
        public Inhabitant A => Inhabitants.Get("inh-000001");

        public int Requests => Decisions.Log.Entries.Count(e => e.Kind == DecisionLogKind.Request);

        /// <summary>
        /// The watched inhabitant does something that runs for long; a thing it does not understand, 20 m
        /// away, becomes a question, and the answer (an action on that thing) is accepted and queued.
        /// </summary>
        public Activity QueueAChoice()
        {
            A.Activity = new Activity("idle", null, A.Position, 100_000);
            var thing = Objects.Spawn(First, new Position(20, 0));
            World.Step();
            Assert.Equal(1, Requests);
            World.Step();
            Assert.Empty(Decisions.Open);
            var queued = A.Planned;
            Assert.NotNull(queued);
            Assert.Equal(thing.Id, queued.TargetId);
            return queued;
        }

        /// <summary>Ends the running action, so the queued choice starts, and runs until it has finished.</summary>
        public void FinishTheChoice()
        {
            A.Activity = null;
            World.Step();
            Assert.NotNull(A.Activity?.DecidedTick);
            for (var t = 0; t < 300 && A.Activity?.DecidedTick is not null; t++)
            {
                World.Step();
            }

            Assert.Null(A.Activity?.DecidedTick);
            Assert.Contains(Events, e => e.Kind == InhabitantsSystem.ActionFinished && e.InhabitantId == A.Id);
        }
    }

    /// <summary>Answers every request at the next tick with the first option aimed at something, else the first option.</summary>
    private sealed class FirstTargetPort : IDecisionPort
    {
        private readonly Queue<PortMessage> _ready = new();

        public void Send(string requestId, string requestJson)
        {
            var request = ContractSerializer.Deserialize<DecisionRequest>(requestJson);
            var option = request.Options.FirstOrDefault(o => o.Target is not null) ?? request.Options.First();
            _ready.Enqueue(new PortMessage(requestId, ContractSerializer.Serialize(new DecisionResponse
            {
                ContractVersion = request.ContractVersion,
                RequestId = request.RequestId,
                Tick = request.Tick,
                OptionId = option.OptionId,
                Rationale = "test",
                Meta = new ResponseMeta { Source = ResponseMetaSource.Mock, Model = "mock", LatencyMs = 0, Attempts = 1 },
            }), IsError: false));
        }

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            var messages = _ready.ToList();
            _ready.Clear();
            return messages;
        }
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
