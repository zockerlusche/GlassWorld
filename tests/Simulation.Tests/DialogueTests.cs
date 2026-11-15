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
/// A1735 (B2782, D1119 step 3): when one inhabitant starts talking to another, the core sends one dialogue
/// job; the turns that pass the core's checks become said events of both, with target, the talk's id, turn
/// and turns. At most one job per pair and the data's hours, a job without an answer in time is dropped, and
/// nothing of it changes the world. Saved, loaded and hashed like the other job state.
/// </summary>
public sealed class DialogueTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gw-dialogue-" + Guid.NewGuid().ToString("N"));

    public DialogueTests() => Directory.CreateDirectory(_directory);

    private static DialogueSettings Settings => Content.Value.Conversation.Dialogue!;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void StartingToTalkSendsOneDialogueJobWithBothSpeakersTheOneWhoBeganFirst()
    {
        var (host, port) = World();
        host.Inhabitants.Get("inh-000001").KnownNames.Add("inh-000002");

        Talk(host, "inh-000001", "inh-000002");

        var job = Assert.Single(port.Dialogue);
        Assert.Empty(ContractValidator.Validate("generation-job", port.Json[job.RequestId]));
        Assert.Equal((JobKind.Dialogue, Settings.JobPriority, Locale.De), (job.Kind, job.Priority, job.DisplayLocale));
        var payload = job.Payload.Dialogue!;
        Assert.Equal((Settings.MaxTurns, Settings.MaxWords), (payload.MaxTurns, payload.MaxWords));
        Assert.Collection(
            payload.Speakers,
            s => Assert.Equal(("inh-000001", "Rena"), (s.Inhabitant.Id, s.CallsOther?.De)),
            s => Assert.Equal(("inh-000002", (string?)null), (s.Inhabitant.Id, s.CallsOther?.De)));
        Assert.Contains(host.Inventions.Log.Entries, e => e.Kind == DecisionLogKind.JobRequest && e.RequestId == job.RequestId);
        Assert.Single(host.Dialogue.Jobs);
    }

    [Fact]
    public void ASpeakerCarriesWhatItKnowsAboutWhatItSeesAsTheDeciderSeesIt()
    {
        // Review GW2 #483: without its knowledge the two would have nothing they learned to talk about.
        var (host, port) = World();
        var joro = host.Inhabitants.Get("inh-000001");
        host.Objects.Spawn("red_berry", new Position(joro.Position.X + 1, joro.Position.Z));
        joro.Beliefs.Set("red_berry", "edible", true, 0.8, KnowledgeSources.Experienced, null, host.World.Tick);

        Talk(host, "inh-000001", "inh-000002");

        var job = Assert.Single(port.Dialogue);
        var joroState = job.Payload.Dialogue!.Speakers.First().Inhabitant;
        Assert.Contains(joroState.Knowledge, k => k.Subject.Id == "red_berry" && k.Property == "edible");
        Assert.Empty(ContractValidator.Validate("generation-job", port.Json[job.RequestId]));
    }

    [Fact]
    public void OnlyOneTalkWithWordsPerPairWithinTheDataHours()
    {
        var (host, port) = World();
        Talk(host, "inh-000001", "inh-000002");
        host.World.Step();

        // The other one begins, within the hour: the same pair, no second job.
        Talk(host, "inh-000002", "inh-000001");
        Assert.Single(port.Dialogue);

        host.World.Run((int)Math.Ceiling(Settings.PerPairHours * host.World.Calendar.Settings.TicksPerHour));
        Talk(host, "inh-000002", "inh-000001");
        Assert.Equal(2, port.Dialogue.Count);
        Assert.Equal("inh-000002", port.Dialogue[1].Payload.Dialogue!.Speakers.First().Inhabitant.Id);
    }

    [Theory]
    [InlineData("idle", "inh-000002")]
    [InlineData("talk_to", "obj-000001")]
    [InlineData("talk_to", null)]
    public void OtherActionsAndTargetsThatAreNoInhabitantSendNothing(string action, string? target)
    {
        var (host, port) = World();
        host.Inhabitants.Report(new WorldEvent(InhabitantsSystem.ActionStarted, host.World.Tick, "inh-000001", action, target));

        Assert.Empty(port.Dialogue);
        Assert.False(host.Dialogue.HasState);
    }

    [Fact]
    public void TheTurnsBecomeSaidEventsInOrderWithTargetTalkTurnAndTurns()
    {
        var (host, port) = World();
        var said = Said(host);
        Talk(host, "inh-000001", "inh-000002");
        var job = Assert.Single(port.Dialogue);

        port.Answer(job, Turns(job, ("inh-000001", "Rena, hast du das gesehen?"), ("inh-000002", "Ja! Schön, nicht?"), ("inh-000001", "Morgen schauen wir zusammen.")));
        host.World.Step();

        Assert.Collection(
            said,
            e => Assert.Equal(("inh-000001", "inh-000002", "Rena, hast du das gesehen?", 1, 3), (e.InhabitantId, e.TargetId, e.Text, e.Turn, e.Turns)),
            e => Assert.Equal(("inh-000002", "inh-000001", "Ja! Schön, nicht?", 2, 3), (e.InhabitantId, e.TargetId, e.Text, e.Turn, e.Turns)),
            e => Assert.Equal(("inh-000001", "inh-000002", "Morgen schauen wir zusammen.", 3, 3), (e.InhabitantId, e.TargetId, e.Text, e.Turn, e.Turns)));
        Assert.All(said, e => Assert.Equal((job.RequestId, "talk_to"), (e.Conversation, e.Action)));
        Assert.Empty(host.Dialogue.Jobs);
        Assert.Contains(host.Inventions.Log.Entries, e => e.Kind == DecisionLogKind.JobResult && e.RequestId == job.RequestId && e.Note is null);
    }

    [Theory]
    [InlineData("out_of_turn", DialogueSystem.OutOfTurn)]
    [InlineData("stranger", DialogueSystem.OutOfTurn)]
    [InlineData("too_long", DialogueSystem.TooLong)]
    [InlineData("wrong_language", DialogueSystem.WrongLanguage)]
    public void ATurnThatDoesNotPassEndsTheTalkThereTheTurnsBeforeStay(string fault, string reason)
    {
        var (host, port) = World();
        var said = Said(host);
        Talk(host, "inh-000001", "inh-000002");
        var job = Assert.Single(port.Dialogue);

        var second = fault switch
        {
            "out_of_turn" => ("inh-000001", "Und noch etwas."),
            "stranger" => ("inh-000003", "Ich bin auch da."),
            "too_long" => ("inh-000002", string.Join(' ', Enumerable.Repeat("Wort", Settings.MaxWords + 1))),
            _ => ("inh-000002", "Yes, I saw it."),
        };
        var status = Turns(job, ("inh-000001", "Hallo Rena!"), second, ("inh-000001", "Schön."));
        if (fault == "wrong_language")
        {
            status.Result!.Dialogue!.Turns.ElementAt(1).Text.Locale = Locale.En;
        }

        port.AnswerUnchecked(job, status);
        host.World.Step();

        var only = Assert.Single(said);
        Assert.Equal(("inh-000001", "Hallo Rena!", 1, 1), (only.InhabitantId, only.Text, only.Turn, only.Turns));
        Assert.Contains(host.Inventions.Log.Entries, e => e.Kind == DecisionLogKind.JobResult && e.RequestId == job.RequestId && e.Note == "rejected:" + reason);
    }

    [Fact]
    public void AnExpressionOutsideTheVocabularyIsLeftOutTheWordsStay()
    {
        var (host, port) = World();
        var said = Said(host);
        Talk(host, "inh-000001", "inh-000002");
        var job = Assert.Single(port.Dialogue);
        var status = Turns(job, ("inh-000001", "Hallo!"), ("inh-000002", "Hallo!"));
        status.Result!.Dialogue!.Turns.ElementAt(0).Expression = "no_such_expression";
        status.Result.Dialogue.Turns.ElementAt(1).Expression = job.Payload.Dialogue!.Expressions.First();

        port.AnswerUnchecked(job, status);
        host.World.Step();

        Assert.Collection(said, e => Assert.Null(e.Detail), e => Assert.Equal(job.Payload.Dialogue.Expressions.First(), e.Detail));
    }

    [Fact]
    public void AJobWithoutAnAnswerInTimeIsDroppedAndALateAnswerChangesNothing()
    {
        var (host, port) = World();
        var said = Said(host);
        Talk(host, "inh-000001", "inh-000002");
        var job = Assert.Single(port.Dialogue);

        // The systems see the tick before it rises: the job is older than the window in the second tick after it.
        host.World.Run((int)Math.Ceiling(Settings.AnswerWithinHours * host.World.Calendar.Settings.TicksPerHour) + 2);
        Assert.Empty(host.Dialogue.Jobs);
        Assert.Contains(host.Inventions.Log.Entries, e => e.Kind == DecisionLogKind.JobDiscarded && e.RequestId == job.RequestId && e.Note == DialogueSystem.TooLate);

        port.Answer(job, Turns(job, ("inh-000001", "Hallo!"), ("inh-000002", "Hallo!")));
        host.World.Step();
        Assert.Empty(said);
    }

    [Fact]
    public void ATalkChangesNothingInTheWorldButTheEvents()
    {
        var (host, port) = World();
        Talk(host, "inh-000001", "inh-000002");
        var job = Assert.Single(port.Dialogue);
        var (silentHost, _) = World();
        Talk(silentHost, "inh-000001", "inh-000002");

        port.Answer(job, Turns(job, ("inh-000001", "Hallo!"), ("inh-000002", "Hallo!")));
        host.World.Step();
        silentHost.World.Run(1);

        // The same world without the answer, apart from the open job the other one still waits for.
        var a = host.Inhabitants.Get("inh-000002");
        var b = silentHost.Inhabitants.Get("inh-000002");
        Assert.Equal(a.Needs, b.Needs);
        Assert.Equal(a.Moods, b.Moods);
        Assert.Equal(a.Beliefs.Count, b.Beliefs.Count);
    }

    [Fact]
    public void AnOpenTalkSurvivesSaveAndLoadAndItsAnswerStillComes()
    {
        var (host, port) = World();
        Talk(host, "inh-000001", "inh-000002");
        var job = Assert.Single(port.Dialogue);
        host.World.Run(3);

        var path = Path.Combine(_directory, "dialogue.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loadedPort = new ScriptedJobPort();
        var loaded = StateCodec.Restore(Factory(loadedPort), SaveGameStore.Load(path));
        Assert.Equal(host.Dialogue.Jobs, loaded.Dialogue.Jobs);
        Assert.Equal(host.Dialogue.Capture().LastTalk, loaded.Dialogue.Capture().LastTalk);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());

        var said = Said(loaded);
        loadedPort.Answer(job, Turns(job, ("inh-000001", "Hallo!"), ("inh-000002", "Hallo!")));
        loaded.World.Step();
        Assert.Equal(2, said.Count);
    }

    [Fact]
    public void WithoutTheAiNothingIsSentAndTheHashStaysAsBefore()
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 21, new SilentDecisionPort(), "de");
        Name(host);
        Talk(host, "inh-000001", "inh-000002");
        host.World.Step();

        Assert.False(host.Dialogue.HasState);
    }

    private static void Talk(SimulationHost host, string speaker, string listener) =>
        host.Inhabitants.Report(new WorldEvent(InhabitantsSystem.ActionStarted, host.World.Tick, speaker, "talk_to", listener));

    private static List<WorldEvent> Said(SimulationHost host)
    {
        var said = new List<WorldEvent>();
        host.Inhabitants.Reported += e =>
        {
            if (e.Kind == DialogueSystem.SaidKind)
            {
                said.Add(e);
            }
        };
        return said;
    }

    private static (SimulationHost Host, ScriptedJobPort Port) World()
    {
        var port = new ScriptedJobPort();
        var host = SimulationHost.CreateEmpty(ContentDirectory, 21, new SilentDecisionPort(), "de", port);
        Name(host);
        return (host, port);
    }

    private static void Name(SimulationHost host)
    {
        foreach (var (id, name) in new[] { ("inh-000001", "Joro"), ("inh-000002", "Rena") })
        {
            var model = Content.Value.InhabitantModel;
            var inhabitant = new Inhabitant(
                id,
                "female",
                1,
                new Position(id == "inh-000001" ? 0 : 100, 0),
                model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
                model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal),
                model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal),
                model.WillToLive.Start);
            host.Inhabitants.Add(inhabitant);
            inhabitant.Name = name;
            inhabitant.Activity = new Activity("idle", null, inhabitant.Position, 1_000_000);
        }

        // One start group: they have met, as the core has it after loading too (RememberStartGroups).
        host.Inhabitants.Get("inh-000001").Met.Add("inh-000002");
        host.Inhabitants.Get("inh-000002").Met.Add("inh-000001");
    }

    private static SimulationHostFactory Factory(IJobPort jobs)
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return new SimulationHostFactory(Content.Value, settings, new SilentDecisionPort(), "de", jobs);
    }

    /// <summary>The contract example of a talk, with the given turns (test data).</summary>
    private static JobStatus Turns(GenerationJob job, params (string Speaker, string Text)[] turns)
    {
        var status = ContractSerializer.Deserialize<JobStatus>(File.ReadAllText(Path.Combine(TestData.Root, "contracts", "examples", "valid", "job-status.dialogue-elin-joro.json")));
        status.JobId = job.RequestId;
        status.Tick = job.Tick;
        var list = status.Result!.Dialogue!.Turns;
        list.Clear();
        foreach (var (speaker, text) in turns)
        {
            list.Add(new DialogueTurn { Speaker = speaker, Text = new GeneratedText { Locale = Locale.De, Text = text }, Act = DialogueTurnAct.Tell, Expression = null });
        }

        return status;
    }

    private sealed class SilentDecisionPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }

    private sealed class ScriptedJobPort : IJobPort
    {
        private readonly Queue<PortMessage> _ready = new();

        /// <summary>Dialogue jobs in the order they were sent; other kinds are left out.</summary>
        public List<GenerationJob> Dialogue { get; } = [];

        public Dictionary<string, string> Json { get; } = new(StringComparer.Ordinal);

        public void Submit(string requestId, string jobJson)
        {
            Json[requestId] = jobJson;
            if (ContractSerializer.Deserialize<GenerationJob>(jobJson) is { Kind: JobKind.Dialogue } job)
            {
                Dialogue.Add(job);
            }
        }

        public void Answer(GenerationJob job, JobStatus status) => _ready.Enqueue(new PortMessage(job.RequestId, ContractSerializer.Serialize(status), IsError: false));

        public void AnswerUnchecked(GenerationJob job, JobStatus status) => _ready.Enqueue(new PortMessage(job.RequestId, ContractSerializer.Serialize(status, validate: false), IsError: false));

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            var ready = _ready.ToList();
            _ready.Clear();
            return ready;
        }

        public void SetPhase(string phase, long tick, bool force = false)
        {
        }
    }
}
