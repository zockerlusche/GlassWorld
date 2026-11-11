using System.Text.Json;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Night;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B4a R5 and B7 in the compute night: per inhabitant first the memory job (once enough events are not in
/// the summary), then a guess for each open question about an object type. The core checks every answer
/// itself, guesses become beliefs capped by the data, everything goes through the job log (save, load,
/// replay). The answers here are test data built from the contract examples.
/// </summary>
public sealed class NightJobsTests : IDisposable
{
    private const string Thing = "flint";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gw-night-" + Guid.NewGuid().ToString("N"));

    public NightJobsTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static KnowledgeMemory Settings => Content.Value.Knowledge.Memory!;

    [Fact]
    public void BelowTheThresholdTheNightSendsNoMemoryJob()
    {
        var (host, a, port) = World(question: false);
        Remember(a, Settings.CondenseAt - 1);

        RunIntoTheNight(host);
        host.World.Run(10);

        Assert.Empty(port.Submitted);
        Assert.False(host.NightJobs.HasState);
        Assert.Equal(Settings.CondenseAt - 1, a.Memory.Events.Count);
    }

    [Fact]
    public void AtTheThresholdTheNightSendsTheOldestEventsWithTheSummaryAndTheBudget()
    {
        var (host, a, port) = World(question: false);
        a.Memory.Restore("Woke up on a beach.", 0, [], []);
        Remember(a, Settings.JobMaxEvents + 8, formative: i => i is 3 or 7);
        var expected = a.Memory.Events.Take(Settings.JobMaxEvents).ToList();

        // Nothing goes out by day, however full the memory is.
        host.World.Run(60);
        Assert.Empty(port.Submitted);

        RunIntoTheNight(host);

        var job = Assert.Single(port.Submitted);
        Assert.Empty(ContractValidator.Validate("generation-job", port.Json[job.RequestId]));
        Assert.Equal((JobKind.Memory, a.Id, Settings.JobPriority, Locale.De), (job.Kind, job.Inhabitant, job.Priority, job.DisplayLocale));
        Assert.Null(job.Object);
        var payload = job.Payload.Memory!;
        Assert.Equal("Woke up on a beach.", payload.Summary);
        Assert.Equal(Settings.MaxChars, payload.MaxChars);
        Assert.Equal(
            expected.Select(e => (e.Tick, e.Kind, e.AboutKind, e.AboutId, e.Formative)),
            payload.Events.Select(e => (e.Tick, e.Kind, (string?)e.About!.Kind.ToString().ToLowerInvariant(), (string?)e.About.Id, e.Formative)));
        Assert.Equal(2, payload.Events.Count(e => e.Formative));

        var open = Assert.Single(host.NightJobs.MemoryJobs);
        Assert.Equal((job.RequestId, a.Id, Settings.MaxChars), (open.RequestId, open.InhabitantId, open.MaxChars));
        Assert.Equal(expected.Select(e => e.Seq), open.Sent.Select(s => s.Seq));
        Assert.Contains(host.Inventions.Log.Entries, e => e.Kind == DecisionLogKind.JobRequest && e.RequestId == job.RequestId);
        Assert.True(host.NightJobs.HasState);

        // The job is out: the memory itself has not changed, and nothing is sent twice.
        host.World.Run(30);
        Assert.Single(port.Submitted);
        Assert.Equal(Settings.JobMaxEvents + 8, a.Memory.Events.Count);
    }

    [Fact]
    public void AJobEndsBeforeMoreFormativeEventsThanTheDataAllow()
    {
        var (host, a, port) = World(question: false);
        Remember(a, Settings.CondenseAt + 8, formative: _ => true);

        RunIntoTheNight(host);

        var job = Assert.Single(port.Submitted);
        Assert.Equal(Settings.JobMaxFormative, job.Payload.Memory!.Events.Count);
        Assert.Equal(a.Memory.Events.Take(Settings.JobMaxFormative).Select(e => e.Tick), job.Payload.Memory.Events.Select(e => e.Tick));
    }

    [Fact]
    public void AGoodAnswerReplacesTheSummaryKeepsWhatItNamesAndLeavesNewerEventsAlone()
    {
        var (host, a, port) = World(question: false);
        Remember(a, Settings.CondenseAt + 4, formative: i => i is 2 or 5);
        RunIntoTheNight(host);
        var job = Assert.Single(port.Submitted);
        var sent = a.Memory.Events.ToList();

        // Two things happen while the job is out.
        a.Memory.Remember(host.World.Tick, "drink", InhabitantsSystem.AboutType, "spring_water", formative: false, Settings.MaxEvents);
        a.Memory.Remember(host.World.Tick, "first_meeting", InhabitantsSystem.AboutInhabitant, "inh-000002", formative: true, Settings.MaxEvents);
        var newer = a.Memory.Events.Skip(sent.Count).ToList();

        // The answer keeps the formative events and, of its own accord, one everyday event.
        var kept = new[] { sent[2].Tick, sent[5].Tick, sent[9].Tick };
        port.Answer(job, Condensed(job, "Woke up on a beach. Met someone. Red berries made sick.", kept));
        host.World.Step();

        Assert.Equal("Woke up on a beach. Met someone. Red berries made sick.", a.Memory.Summary);
        Assert.Equal(
            new[] { sent[2], sent[5], sent[9] }.Select(e => e with { Condensed = true }).Concat(newer),
            a.Memory.Events);
        Assert.Empty(host.NightJobs.MemoryJobs);
        Assert.Contains(host.Inventions.Log.Entries, e => e.Kind == DecisionLogKind.JobResult && e.RequestId == job.RequestId && e.Note is null);

        // What the summary holds is not sent again, and two new events are no reason for another job.
        host.World.Run(30);
        Assert.Single(port.Submitted);
        Assert.Equal(2, a.Memory.Uncondensed.Count());
    }

    [Fact]
    public void MoreEventsThanOneJobHoldsGoOutInSeveralJobsOldestFirst()
    {
        var (host, a, port) = World(question: false);
        var count = Settings.JobMaxEvents + Settings.CondenseAt + 2;
        Assert.True(count <= Settings.MaxEvents);
        Remember(a, count);
        var all = a.Memory.Events.ToList();
        RunIntoTheNight(host);

        var first = Assert.Single(port.Submitted);
        Assert.Equal(all.Take(Settings.JobMaxEvents).Select(e => e.Tick), first.Payload.Memory!.Events.Select(e => e.Tick));
        port.Answer(first, Condensed(first, "First part.", []));
        host.World.Step();

        // The next batch follows at once and builds on the new summary.
        Assert.Equal(2, port.Submitted.Count);
        var second = port.Submitted[1];
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.Equal("First part.", second.Payload.Memory!.Summary);
        Assert.Equal(all.Skip(Settings.JobMaxEvents).Select(e => e.Tick), second.Payload.Memory.Events.Select(e => e.Tick));
        port.Answer(second, Condensed(second, "First and second part.", []));
        host.World.Run(5);

        Assert.Equal(2, port.Submitted.Count);
        Assert.Equal("First and second part.", a.Memory.Summary);
        Assert.Empty(a.Memory.Events);
        Assert.Empty(host.NightJobs.MemoryJobs);
    }

    [Theory]
    [InlineData("too_long", NightJobsSystem.SummaryTooLong)]
    [InlineData("blank", NightJobsSystem.SummaryEmpty)]
    [InlineData("kept_not_sent", NightJobsSystem.KeptNotSent)]
    [InlineData("formative_dropped", NightJobsSystem.FormativeDropped)]
    public void AMemoryAnswerThatDoesNotFitIsRefusedWithoutChangingAnything(string fault, string reason)
    {
        var (host, a, port) = World(question: false);
        a.Memory.Restore("Old summary.", 0, [], []);
        Remember(a, Settings.CondenseAt + 2, formative: i => i is 1 or 4);
        RunIntoTheNight(host);
        var job = Assert.Single(port.Submitted);
        var before = a.Memory.Events.ToList();
        var formative = before.Where(e => e.Formative).Select(e => e.Tick).ToArray();

        port.Answer(job, fault switch
        {
            // One character over the budget the job asked for.
            "too_long" => Condensed(job, new string('a', Settings.MaxChars + 1), formative),
            "blank" => Condensed(job, "   ", formative),
            "kept_not_sent" => Condensed(job, "Fine.", [.. formative, before[^1].Tick + 1_000]),
            _ => Condensed(job, "Fine.", [formative[0]]),
        });
        host.World.Step();

        Assert.Equal("Old summary.", a.Memory.Summary);
        Assert.Equal(before, a.Memory.Events);
        Assert.Empty(host.NightJobs.MemoryJobs);
        Assert.Contains(host.Inventions.Log.Entries, e => e.Kind == DecisionLogKind.JobResult && e.RequestId == job.RequestId && e.Note == NightJobsSystem.Rejected + reason);

        // Not tonight again (no loop); the next night tries anew with a new id.
        host.World.Run(60);
        Assert.Single(port.Submitted);
        RunIntoTheNight(host);
        var again = port.Submitted.Where(j => j.Kind == JobKind.Memory).ToList();
        Assert.Equal(2, again.Count);
        Assert.NotEqual(job.RequestId, again[1].RequestId);
        Assert.Equal(before.Select(e => e.Tick), again[1].Payload.Memory!.Events.Select(e => e.Tick));
    }

    [Fact]
    public void ASummaryExactlyAtTheBudgetIsTaken()
    {
        var (host, a, port) = World(question: false);
        Remember(a, Settings.CondenseAt);
        RunIntoTheNight(host);
        var job = Assert.Single(port.Submitted);

        port.Answer(job, Condensed(job, new string('a', Settings.MaxChars), []));
        host.World.Step();

        Assert.Equal(Settings.MaxChars, a.Memory.Summary.Length);
        Assert.Empty(a.Memory.Events);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("service_error")]
    [InlineData("no_json")]
    [InlineData("other_kind")]
    public void AFailedMemoryJobChangesNothingAndIsLogged(string fault)
    {
        var (host, a, port) = World(question: false);
        Remember(a, Settings.CondenseAt);
        RunIntoTheNight(host);
        var job = Assert.Single(port.Submitted);
        var before = a.Memory.Events.ToList();

        switch (fault)
        {
            case "failed":
                port.Answer(job, Failed(job));
                break;
            case "service_error":
                port.AnswerError(job.RequestId, ServiceError(job));
                break;
            case "no_json":
                port.AnswerRaw(job.RequestId, "{ \"summary\": \"not a job status\" }");
                break;
            default:
                // A valid status, but of another kind than the job that was sent.
                var guess = Guess(job, understood: true, ("hard", true, 0.5));
                port.Answer(job, guess);
                break;
        }

        host.World.Step();

        Assert.Empty(a.Memory.Summary);
        Assert.Equal(before, a.Memory.Events);
        Assert.Empty(host.NightJobs.MemoryJobs);
        var entry = Assert.Single(host.Inventions.Log.Entries, e => e.RequestId == job.RequestId && e.Kind != DecisionLogKind.JobRequest);
        Assert.Equal(
            fault switch
            {
                "failed" => (DecisionLogKind.JobError, "Model_unavailable"),
                "service_error" => (DecisionLogKind.JobError, ReplayNotes.ServiceError),
                "no_json" => (DecisionLogKind.JobDiscarded, ReplayNotes.ContractViolation),
                _ => (DecisionLogKind.JobDiscarded, NightJobsSystem.NotFinal),
            },
            (entry.Kind, entry.Note));
        host.World.Run(30);
        Assert.Single(port.Submitted);
    }

    [Fact]
    public void AGuessBecomesAGuessedBeliefCappedByTheDataAndSettlesTheQuestion()
    {
        var (host, a, port) = World();
        host.World.Step();
        var question = Assert.Single(host.Decisions.Open);
        Assert.Equal((DecisionsSystem.UnknownObject, "type:" + Thing), (question.Kind, question.Subject));

        // It has looked at the thing since: that is what it notices, and a guess never replaces it.
        a.Beliefs.Set(Thing, "sharp", true, 0.8, KnowledgeSources.Seen, null, host.World.Tick);
        a.Beliefs.Set(Thing, "fruit", false, 0.8, KnowledgeSources.Seen, null, host.World.Tick);
        a.Beliefs.Set("apple", "edible", true, 1.0, KnowledgeSources.Experienced, null, host.World.Tick);
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        RunIntoTheNight(host);

        var job = Assert.Single(port.Submitted);
        Assert.Empty(ContractValidator.Validate("generation-job", port.Json[job.RequestId]));
        var night = Content.Value.Knowledge.NightQuestions!;
        Assert.Equal((JobKind.Question, a.Id, night.JobPriority), (job.Kind, job.Inhabitant, job.Priority));
        var payload = job.Payload.Question!;
        Assert.Equal(QuestionPayloadQuestion.What_is, payload.Question);
        Assert.Equal((SubjectKind.Type, Thing), (payload.Subject.Kind, payload.Subject.Id));
        Assert.Equal(["sharp"], payload.Perception);
        Assert.Equal(a.Id, payload.Inhabitant.Id);

        // What it believes about the thing first, then its other beliefs; never the truth (flint is hard).
        Assert.Equal([(Thing, "sharp"), ("apple", "edible")], payload.Inhabitant.Knowledge.Select(k => (k.Subject.Id, k.Property)));
        var open = Assert.Single(host.NightJobs.QuestionJobs);
        Assert.Equal((job.RequestId, question.Id, Thing), (open.RequestId, open.QuestionId, open.TypeId));

        var cap = Content.Value.Knowledge.GuessedCap;
        port.Answer(job, Guess(job, understood: true, ("hard", true, 0.95), ("edible", false, 0.3), ("sharp", false, 0.5)));
        var tick = host.World.Tick;
        host.World.Step();

        Assert.True(a.Beliefs.TryGet(Thing, "hard", out var hard));
        Assert.Equal((true, cap, KnowledgeSources.Guessed, tick), (hard.Value, hard.Confidence, hard.Source, hard.Tick));
        Assert.True(a.Beliefs.TryGet(Thing, "edible", out var edible));
        Assert.Equal((false, 0.3, KnowledgeSources.Guessed), (edible.Value, edible.Confidence, edible.Source));
        Assert.True(a.Beliefs.TryGet(Thing, "sharp", out var sharp));
        Assert.Equal((true, 0.8, KnowledgeSources.Seen), (sharp.Value, sharp.Confidence, sharp.Source));

        Assert.Empty(host.Decisions.Open);
        Assert.Empty(host.NightJobs.QuestionJobs);
        Assert.Contains(events, e => e.Kind == "question_answered" && e.TargetId == question.Id && e.Detail == NightJobsSystem.Guessed);
        Assert.Equal(
            ["edible/" + KnowledgeSources.Guessed, "hard/" + KnowledgeSources.Guessed],
            events.Where(e => e.Kind == InhabitantsSystem.KnowledgeGained && e.TargetId == Thing).Select(e => e.Detail!).Order(StringComparer.Ordinal));
        Assert.Contains(events, e => e.Kind == "thought" && e.InhabitantId == a.Id && !string.IsNullOrEmpty(e.Text));
        Assert.Contains(host.Inventions.Log.Entries, e => e.Kind == DecisionLogKind.JobResult && e.RequestId == job.RequestId && e.Note is null);

        // The routine may now act on the guess; the answer of the decider that comes later no longer fits.
        Assert.False(host.Decisions.HasOpenQuestion(a.Id, Thing));
    }

    [Fact]
    public void NotUnderstandingLeavesTheQuestionOpenAndTheNextNightAsksAgain()
    {
        var (host, a, port) = World();
        RunIntoTheNight(host);
        var job = Assert.Single(port.Submitted);
        Assert.Empty(job.Payload.Question!.Perception);
        var question = Assert.Single(host.Decisions.Open);

        // Guesses of an answer that did not understand count for nothing.
        var status = Guess(job, understood: false, ("hard", true, 0.5));
        port.Answer(job, status);
        host.World.Step();

        Assert.Empty(a.Beliefs.About(Thing));
        Assert.Equal(question, Assert.Single(host.Decisions.Open));
        Assert.Empty(host.NightJobs.QuestionJobs);
        Assert.Contains(host.Inventions.Log.Entries, e => e.Kind == DecisionLogKind.JobResult && e.RequestId == job.RequestId && e.Note == NightJobsSystem.NotUnderstood);

        host.World.Run(60);
        Assert.Single(port.Submitted);
        RunIntoTheNight(host);
        var again = port.Submitted.Where(j => j.Kind == JobKind.Question).ToList();
        Assert.Equal(2, again.Count);
        Assert.NotEqual(job.RequestId, again[1].RequestId);
        Assert.Equal(Thing, again[1].Payload.Question!.Subject.Id);
    }

    [Theory]
    [InlineData("not_a_property", NightJobsSystem.UnknownProperty)]
    [InlineData("hard", NightJobsSystem.PropertyTwice)]
    public void AGuessThatDoesNotFitIsRefusedAsAWhole(string second, string reason)
    {
        var (host, a, port) = World();
        RunIntoTheNight(host);
        var job = Assert.Single(port.Submitted);

        port.Answer(job, Guess(job, understood: true, ("hard", true, 0.5), (second, true, 0.5)));
        host.World.Step();

        Assert.Empty(a.Beliefs.About(Thing));
        Assert.Single(host.Decisions.Open);
        Assert.Empty(host.NightJobs.QuestionJobs);
        Assert.Contains(host.Inventions.Log.Entries, e => e.Kind == DecisionLogKind.JobResult && e.RequestId == job.RequestId && e.Note == NightJobsSystem.Rejected + reason);
    }

    [Fact]
    public void AThoughtTheContentGuardRefusesIsNotShownButTheGuessCounts()
    {
        var (host, a, port) = World();
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        RunIntoTheNight(host);
        var job = Assert.Single(port.Submitted);

        var status = Guess(job, understood: true, ("hard", true, 0.5));
        status.Result!.Question!.Thought!.Text = BlockedWord();
        port.Answer(job, status);
        host.World.Step();

        Assert.DoesNotContain(events, e => e.Kind == "thought");
        Assert.True(a.Beliefs.TryGet(Thing, "hard", out _));
    }

    [Fact]
    public void AGuessForAQuestionThatWasSettledMeanwhileChangesNothing()
    {
        var (host, a, port) = World();
        RunIntoTheNight(host);
        var job = Assert.Single(port.Submitted);

        Assert.True(host.Decisions.Settle(host.World, Assert.Single(host.Decisions.Open).Id, "test"));
        port.Answer(job, Guess(job, understood: true, ("hard", true, 0.5)));
        host.World.Step();

        Assert.Empty(a.Beliefs.About(Thing));
        Assert.Contains(host.Inventions.Log.Entries, e => e.Kind == DecisionLogKind.JobDiscarded && e.RequestId == job.RequestId && e.Note == ReplayNotes.QuestionClosed);
        Assert.False(host.Decisions.Settle(host.World, "q-999999", "test"));
    }

    [Fact]
    public void OnlyOpenQuestionsAboutATypeThatTheDataListGetAJob()
    {
        var (host, a, port) = World();
        Assert.True(host.Decisions.Submit(new PlayerCommand(DecisionsSystem.InhabitantClicked, a.Id)).Accepted);
        host.World.Run(2);
        Assert.Equal(["player", "type:" + Thing], host.Decisions.Open.Select(q => q.Subject).Order(StringComparer.Ordinal));

        RunIntoTheNight(host);

        var job = Assert.Single(port.Submitted);
        Assert.Equal(Thing, job.Payload.Question!.Subject.Id);
    }

    [Fact]
    public void TheMemoryComesFirstAndTheQuestionReadsTheNewSummary()
    {
        var (host, a, port) = World();
        Remember(a, Settings.CondenseAt);
        RunIntoTheNight(host);

        // Only the memory job is out; the question waits for it.
        var memory = Assert.Single(port.Submitted);
        Assert.Equal(JobKind.Memory, memory.Kind);
        host.World.Run(20);
        Assert.Single(port.Submitted);
        Assert.Empty(host.NightJobs.QuestionJobs);

        port.Answer(memory, Condensed(memory, "Found a strange stone on the beach.", []));
        host.World.Step();

        Assert.Equal(2, port.Submitted.Count);
        var question = port.Submitted[1];
        Assert.Equal(JobKind.Question, question.Kind);
        Assert.Equal("Found a strange stone on the beach.", question.Payload.Question!.Inhabitant.Memory.Summary);
        Assert.Empty(question.Payload.Question.Inhabitant.Memory.Recent);
        Assert.Single(host.NightJobs.QuestionJobs);
    }

    [Fact]
    public void AfterAFailedMemoryJobTheQuestionsStillGoOutWithTheSummaryAsItIs()
    {
        var (host, a, port) = World();
        a.Memory.Restore("Old summary.", 0, [], []);
        Remember(a, Settings.CondenseAt);
        RunIntoTheNight(host);
        var memory = Assert.Single(port.Submitted);

        port.Answer(memory, Failed(memory));
        host.World.Step();

        Assert.Equal(2, port.Submitted.Count);
        var question = port.Submitted[1].Payload.Question!;
        Assert.Equal("Old summary.", question.Inhabitant.Memory.Summary);
        Assert.Equal(InhabitantStates.MaxRecent, question.Inhabitant.Memory.Recent.Count);
        Assert.Empty(ContractValidator.Validate("generation-job", port.Json[port.Submitted[1].RequestId]));
    }

    [Fact]
    public void EveryInhabitantHasItsOwnNightInIdOrder()
    {
        var (host, a, port) = World();
        var b = Person(host, "inh-000002", new Position(200, 0));
        Remember(b, Settings.CondenseAt);
        RunIntoTheNight(host);

        // The first has only a question, the second only its memory (it sees nothing unknown).
        Assert.Equal([(JobKind.Question, a.Id), (JobKind.Memory, b.Id)], port.Submitted.Select(j => (j.Kind, j.Inhabitant!)));
    }

    [Fact]
    public void MemoryAndOpenJobsSurviveSaveAndLoad()
    {
        var (host, a, port) = World();
        a.Memory.Restore("Woke up on a beach.", 0, [], []);
        Remember(a, Settings.CondenseAt + 3, formative: i => i == 2);
        Assert.True(a.Memory.First("eat", InhabitantsSystem.AboutType, "apple"));
        RunIntoTheNight(host);
        var job = Assert.Single(port.Submitted);

        // Something about nothing happens while the job is out (no subject in the save).
        a.Memory.Remember(host.World.Tick, "help_needed", null, null, formative: true, Settings.MaxEvents);

        var path = Path.Combine(_directory, "night.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        Assert.Equal(SaveGameStore.SchemaVersion, SaveGameStore.VersionOf(path));
        var loadedPort = new ScriptedJobPort();
        var loaded = StateCodec.Restore(Factory(loadedPort, new SilentDecisionPort()), SaveGameStore.Load(path));

        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        var b = loaded.Inhabitants.Get(a.Id);
        Assert.Equal((a.Memory.Summary, a.Memory.LastSeq), (b.Memory.Summary, b.Memory.LastSeq));
        Assert.Equal(a.Memory.Events, b.Memory.Events);
        Assert.Equal(a.Memory.Firsts, b.Memory.Firsts);
        var open = Assert.Single(loaded.NightJobs.MemoryJobs);
        Assert.Equal(job.RequestId, open.RequestId);
        Assert.Equal(host.NightJobs.MemoryJobs.Single().Sent, open.Sent);

        // The answer takes effect in the loaded world as in the one that was never saved, the waiting
        // question follows in both.
        var status = Condensed(job, "Woke up on a beach. Found a stone.", [a.Memory.Events[2].Tick]);
        port.Answer(job, status);
        loadedPort.Answer(job, status);
        host.World.Run(5);
        loaded.World.Run(5);

        Assert.Equal("Woke up on a beach. Found a stone.", b.Memory.Summary);
        Assert.Equal(JobKind.Question, Assert.Single(loadedPort.Submitted).Kind);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(host.Inventions.Log.Entries, loaded.Inventions.Log.Entries);

        // And once more with what the condensing left: the kept event, the open question job.
        Assert.Contains(b.Memory.Events, e => e.Condensed);
        Assert.Contains(b.Memory.Events, e => e.AboutKind is null && !e.Condensed);
        SaveGameStore.Save(path, StateCodec.Capture(loaded));
        var again = StateCodec.Restore(Factory(new ScriptedJobPort(), new SilentDecisionPort()), SaveGameStore.Load(path));
        Assert.Equal(loaded.World.StateHash(), again.World.StateHash());
        Assert.Equal(b.Memory.Events, again.Inhabitants.Get(a.Id).Memory.Events);
        Assert.Equal(loaded.NightJobs.QuestionJobs, again.NightJobs.QuestionJobs);
        Assert.Empty(again.NightJobs.MemoryJobs);
    }

    [Fact]
    public void ASaveFromBeforeTheMemoryLoadsWithAnEmptyMemory()
    {
        var (host, a, _) = World();
        Remember(a, 5);
        host.World.Run(10);
        var path = Path.Combine(_directory, "old.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE memories; DROP TABLE memory_events; PRAGMA user_version = 10;";
            command.ExecuteNonQuery();
        }

        var loaded = StateCodec.Restore(Factory(new ScriptedJobPort(), new SilentDecisionPort()), SaveGameStore.Load(path));

        Assert.All(loaded.Inhabitants.All, i => Assert.False(i.Memory.HasState));
        Assert.False(loaded.NightJobs.HasState);
        Assert.Equal(host.Decisions.Open, loaded.Decisions.Open, new QuestionComparer());

        // It remembers from then on.
        var b = loaded.Inhabitants.Get(a.Id);
        b.Memory.Remember(loaded.World.Tick, "drink", InhabitantsSystem.AboutType, "spring_water", formative: false, Settings.MaxEvents);
        Assert.Equal(1, Assert.Single(b.Memory.Events).Seq);
    }

    [Fact]
    public void AReplayWithTheJobLogGivesTheSameState()
    {
        // The whole night: two memory jobs (one refused), a guess, a late outcome nobody waits for.
        static (SimulationHost Host, Inhabitant A) Start(IJobPort jobs)
        {
            var (host, a, _) = World(jobs: jobs);
            a.Memory.Restore("Woke up on a beach.", 0, [], []);
            Remember(a, Settings.JobMaxEvents + Settings.CondenseAt, formative: i => i is 2 or 40);
            return (host, a);
        }

        var port = new ScriptedJobPort();
        var (original, a) = Start(port);
        RunIntoTheNight(original);
        var first = port.Submitted[0];
        port.Answer(first, Condensed(first, "Woke up on a beach. Found a stone.", [a.Memory.Events[2].Tick]));
        original.World.Run(3);
        var second = port.Submitted[1];
        port.Answer(second, Condensed(second, "Dropped a formative event.", []));
        original.World.Run(3);
        var question = port.Submitted[2];
        Assert.Equal(JobKind.Question, question.Kind);
        port.Answer(question, Guess(question, understood: true, ("hard", true, 0.9)));
        port.Answer(first, Condensed(first, "Too late.", []));
        original.World.Run(40);

        Assert.Equal("Woke up on a beach. Found a stone.", a.Memory.Summary);
        Assert.True(a.Beliefs.TryGet(Thing, "hard", out _));
        Assert.Equal(3, port.Submitted.Count);

        var (replay, b) = Start(new ReplayJobPort(original.Inventions.Log));
        replay.World.Run(original.World.Tick);

        Assert.Equal(original.World.StateHash(), replay.World.StateHash());
        Assert.Equal(original.Inventions.Log.Entries, replay.Inventions.Log.Entries);
        Assert.Equal(a.Memory.Events, b.Memory.Events);
        Assert.Equal(a.Memory.Summary, b.Memory.Summary);

        // The log tells the whole story: three requests, the taken and the refused condensing, the guess,
        // and the late outcome, which the invention system notes as nobody's.
        var notes = original.Inventions.Log.Entries.Where(e => e.Kind != DecisionLogKind.JobRequest).Select(e => (e.Kind, e.RequestId, e.Note)).ToList();
        Assert.Equal(
            [
                (DecisionLogKind.JobResult, first.RequestId, null),
                (DecisionLogKind.JobResult, second.RequestId, NightJobsSystem.Rejected + NightJobsSystem.FormativeDropped),
                (DecisionLogKind.JobDiscarded, first.RequestId, ReplayNotes.QuestionClosed),
                (DecisionLogKind.JobResult, question.RequestId, null),
            ],
            notes);
    }

    [Fact]
    public void AfterLoadingWithALiveServiceOpenJobsAreGivenUpAndSentAnew()
    {
        var (host, a, port) = World();
        Remember(a, Settings.CondenseAt);
        RunIntoTheNight(host);
        var lost = Assert.Single(port.Submitted);
        Assert.Equal(JobKind.Memory, lost.Kind);

        host.NightJobs.ResendPendingAfterLoad();
        Assert.Empty(host.NightJobs.MemoryJobs);
        host.World.Step();

        Assert.Equal(2, port.Submitted.Count);
        var anew = port.Submitted[1];
        Assert.Equal(JobKind.Memory, anew.Kind);
        Assert.NotEqual(lost.RequestId, anew.RequestId);
        Assert.Equal(lost.Payload.Memory!.Events.Select(e => e.Tick), anew.Payload.Memory!.Events.Select(e => e.Tick));

        // The old job's answer belongs to nobody any more; the new one counts, and the question follows.
        port.Answer(lost, Condensed(lost, "From the old process.", []));
        port.Answer(anew, Condensed(anew, "From the new process.", []));
        host.World.Step();

        Assert.Equal("From the new process.", a.Memory.Summary);
        Assert.Equal(JobKind.Question, port.Submitted[2].Kind);
    }

    [Fact]
    public void TheJobsAskInThePlayersLanguage()
    {
        var (host, _, port) = World();
        Assert.True(host.Decisions.Submit(new PlayerCommand(DecisionsSystem.SetDisplayLocale, null) { Text = "en" }).Accepted);

        RunIntoTheNight(host);

        Assert.Equal(Locale.En, Assert.Single(port.Submitted).DisplayLocale);
    }

    [Fact]
    public void TheRouterGivesEverySystemItsOwnOutcomesOnce()
    {
        var inner = new ScriptedJobPort();
        var router = new JobRouter(inner);
        var mine = new HashSet<string>(StringComparer.Ordinal) { "a", "c" };
        var claimed = router.Claim(mine.Contains);
        inner.AnswerRaw("a", "1");
        inner.AnswerRaw("b", "2");

        // Whoever asks first: each gets its own, nothing twice in a tick.
        Assert.Equal(["b"], router.Rest.Receive(5).Select(m => m.RequestId));
        Assert.Equal(["a"], claimed.Receive(5).Select(m => m.RequestId));
        Assert.Empty(router.Rest.Receive(5));
        Assert.Empty(claimed.Receive(5));

        // The claim is asked when outcomes arrive: what is no longer waited for goes to the rest.
        inner.AnswerRaw("c", "3");
        inner.AnswerRaw("a", "4");
        mine.Remove("a");
        Assert.Equal(["c"], claimed.Receive(6).Select(m => m.RequestId));
        Assert.Equal(["a"], router.Rest.Receive(6).Select(m => m.RequestId));

        // A replay port hands out the same outcomes on every call of a tick: they still take effect once.
        var log = new DecisionLog();
        log.Add(new DecisionLogEntry(DecisionLogKind.JobResult, "c", 9, "5", null));
        log.Add(new DecisionLogEntry(DecisionLogKind.JobDiscarded, "x", 9, "6", ReplayNotes.QuestionClosed));
        var replay = new JobRouter(new ReplayJobPort(log));
        var replayed = replay.Claim(mine.Contains);
        Assert.Equal(["x"], replay.Rest.Receive(9).Select(m => m.RequestId));
        Assert.Equal(["c"], replayed.Receive(9).Select(m => m.RequestId));
        Assert.Empty(replay.Rest.Receive(9));
        Assert.Empty(replayed.Receive(9));

        // Submitting and the phase signal go straight through.
        claimed.Submit("d", "{}");
        router.Rest.SetPhase(JobPhase.Night, 7, force: true);
        Assert.Equal(["d"], inner.SubmittedIds);
        Assert.Equal([(JobPhase.Night, 7L, true)], inner.Phases);
    }

    /// <summary>One inhabitant that does nothing by itself, with a thing it does not know next to it (an open question).</summary>
    private static (SimulationHost Host, Inhabitant A, ScriptedJobPort Port) World(bool question = true, IJobPort? jobs = null)
    {
        var port = jobs as ScriptedJobPort ?? new ScriptedJobPort();
        var host = SimulationHost.CreateEmpty(ContentDirectory, 21, new SilentDecisionPort(), "de", jobs ?? port);
        var a = Person(host, "inh-000001", new Position(0, 0));
        if (question)
        {
            host.Objects.Spawn(Thing, new Position(1, 0));
        }

        return (host, a, port);
    }

    private static Inhabitant Person(SimulationHost host, string id, Position position)
    {
        var model = Content.Value.InhabitantModel;
        var inhabitant = new Inhabitant(
            id,
            "female",
            1,
            position,
            model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
            model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal),
            model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal),
            model.WillToLive.Start);
        host.Inhabitants.Add(inhabitant);

        // Busy with nothing for the whole test, so only the test fills its memory.
        inhabitant.Activity = new Activity("idle", null, position, 1_000_000);
        return inhabitant;
    }

    /// <summary>Everyday events at ticks of their own (test data), formative where the test says so.</summary>
    private static void Remember(Inhabitant inhabitant, int count, Func<int, bool>? formative = null)
    {
        for (var i = 0; i < count; i++)
        {
            inhabitant.Memory.Remember(100 + (i * 3), i % 2 == 0 ? "drink" : "eat", InhabitantsSystem.AboutType, i % 2 == 0 ? "spring_water" : "apple", formative?.Invoke(i) ?? false, Settings.MaxEvents);
        }
    }

    /// <summary>Steps through the tick with which the next compute night begins (its jobs are out afterwards).</summary>
    private static void RunIntoTheNight(SimulationHost host)
    {
        var limit = host.World.Tick + (2 * host.World.Calendar.TicksPerDay);
        while (!host.NightJobs.NightBegins(host.World))
        {
            Assert.True(host.World.Tick < limit, "no night began within two days");
            host.World.Step();
        }

        host.World.Step();
    }

    private static SimulationHostFactory Factory(IJobPort jobs, IDecisionPort decisions)
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, decisions, "de", jobs);
    }

    private static JobStatus Example(GenerationJob job, string file)
    {
        var status = ContractSerializer.Deserialize<JobStatus>(File.ReadAllText(Path.Combine(TestData.Root, "contracts", "examples", "valid", file)));
        status.JobId = job.RequestId;
        status.Tick = job.Tick;
        return status;
    }

    /// <summary>The contract example of a condensed memory, fitted to the job (test data).</summary>
    private static JobStatus Condensed(GenerationJob job, string summary, long[] kept)
    {
        var status = Example(job, "job-status.memory-condensed.json");
        status.Result!.Memory!.Summary = summary;
        status.Result.Memory.Kept = kept;
        return status;
    }

    /// <summary>The contract example of a guess, fitted to the job (test data).</summary>
    private static JobStatus Guess(GenerationJob job, bool understood, params (string Property, bool Value, double Confidence)[] guesses)
    {
        var status = Example(job, "job-status.question-guess.json");
        status.Result!.Question!.Understood = understood;
        status.Result.Question.Guesses = [.. guesses.Select(g => new Guess { Property = g.Property, Value = g.Value, Confidence = g.Confidence })];
        status.Result.Question.Idea = null;
        return status;
    }

    private static JobStatus Failed(GenerationJob job)
    {
        var status = Example(job, "job-status.failed.json");
        status.Kind = job.Kind;
        return status;
    }

    private static string ServiceError(GenerationJob job)
    {
        var error = ContractSerializer.Deserialize<ServiceError>(File.ReadAllText(Path.Combine(TestData.Root, "contracts", "examples", "valid", "service-error.timeout.json")));
        error.RequestId = job.RequestId;
        error.Tick = job.Tick;
        return ContractSerializer.Serialize(error);
    }

    /// <summary>A term the content guard blocks, taken from the safety data (no literal here).</summary>
    private static string BlockedWord()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContentDirectory, "safety", "rules.json")));
        return doc.RootElement.GetProperty("categories")[0].GetProperty("terms_en")[0].GetString()!;
    }

    private sealed class QuestionComparer : IEqualityComparer<OpenQuestion>
    {
        public bool Equals(OpenQuestion? x, OpenQuestion? y) =>
            x is not null && y is not null && x.Id == y.Id && x.InhabitantId == y.InhabitantId && x.Subject == y.Subject && x.RequestId == y.RequestId;

        public int GetHashCode(OpenQuestion obj) => StringComparer.Ordinal.GetHashCode(obj.Id);
    }

    /// <summary>Keeps the requests and never answers: the questions stay open.</summary>
    private sealed class SilentDecisionPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }

    /// <summary>Records submitted jobs; outcomes are handed in by the test.</summary>
    private sealed class ScriptedJobPort : IJobPort
    {
        private readonly Queue<PortMessage> _ready = new();

        /// <summary>Night jobs (memory, question) in the order they were sent; other kinds are left out.</summary>
        public List<GenerationJob> Submitted { get; } = [];

        public List<string> SubmittedIds { get; } = [];

        public Dictionary<string, string> Json { get; } = new(StringComparer.Ordinal);

        public List<(string Phase, long Tick, bool Force)> Phases { get; } = [];

        public void Submit(string requestId, string jobJson)
        {
            SubmittedIds.Add(requestId);
            Json[requestId] = jobJson;
            if (jobJson.Contains("\"kind\"", StringComparison.Ordinal) && ContractSerializer.Deserialize<GenerationJob>(jobJson) is { Kind: JobKind.Memory or JobKind.Question } job)
            {
                Submitted.Add(job);
            }
        }

        public void Answer(GenerationJob job, JobStatus status) => _ready.Enqueue(new PortMessage(job.RequestId, ContractSerializer.Serialize(status), IsError: false));

        public void AnswerRaw(string requestId, string json) => _ready.Enqueue(new PortMessage(requestId, json, IsError: false));

        public void AnswerError(string requestId, string json) => _ready.Enqueue(new PortMessage(requestId, json, IsError: true));

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            var ready = _ready.ToList();
            _ready.Clear();
            return ready;
        }

        public void SetPhase(string phase, long tick, bool force = false) => Phases.Add((phase, tick, force));
    }
}
