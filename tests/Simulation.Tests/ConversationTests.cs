using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Player;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>Talking with an inhabitant: command, guard, favour, answers checked by the core (B6 R13, A1342).</summary>
public sealed class ConversationTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gw-conversation-" + Guid.NewGuid().ToString("N"));

    public ConversationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void RefusedEmptyOrUnaffordableTextIsRejectedWithoutCost()
    {
        var (host, id) = Host();
        var favor = host.Player.Favor;
        Assert.Equal(ConversationSystem.ReasonEmpty, Say(host, id, "   ").ReasonKey);
        Assert.Equal(ConversationSystem.ReasonRefused, Say(host, id, "Ich will dich töten").ReasonKey);
        Assert.Equal("msg.command.no_inhabitant", Say(host, "inh-999999", "Hallo").ReasonKey);
        host.World.Run(2);
        Assert.True(host.Player.Favor >= favor - 1e-9, "a refused sentence costs nothing (favour only regrows)");
        Assert.False(host.Conversation.HasState);
    }

    [Fact]
    public void ASentenceCostsFavourWaitsAndOnlyOneAtATime()
    {
        var (host, id) = Host();
        var favor = host.Player.Favor;
        var events = Collect(host);
        Assert.True(Say(host, id, "Hallo, wie geht es dir?").Accepted);
        host.World.Step();
        var cost = host.Player.CostOf(ConversationSystem.Intervention);
        Assert.InRange(host.Player.Favor, favor - cost - 1e-9, favor - cost + 0.1); // minus the cost, plus one tick of regrowth
        Assert.True(host.Conversation.IsWaiting(id));
        Assert.Contains(events, e => e.Kind == "conversation_started" && e.Text == "Hallo, wie geht es dir?");
        Assert.Equal(ConversationSystem.ReasonOpen, Say(host, id, "Noch etwas?").ReasonKey);
        Assert.Single(host.Conversation.Log, e => e.Kind == ConversationLogKind.Request);
    }

    [Fact]
    public void AReplyIsShownAndTheCoreChangesUnderstandingAndRelationByTheData()
    {
        var (host, id) = Host();
        host.Conversation.Port = new FixedPort(ConversationResponseOutcome.Reply, "Mir geht es gut.", "wave");
        var events = Collect(host);
        var rule = host.Content.Conversation.Outcomes.Single(o => o.Outcome == OutcomeChangeOutcome.Reply);
        var start = host.Conversation.MemoryOf(id);
        Say(host, id, "Hallo!");
        host.World.Run(2);

        var said = Assert.Single(events, e => e.Kind == "said");
        Assert.Equal("Mir geht es gut.", said.Text);
        Assert.Equal("wave", said.Detail);
        var memory = host.Conversation.MemoryOf(id);
        Assert.Equal(start.Understanding + rule.Understanding, memory.Understanding, 9);
        Assert.Equal(["Hallo!", "Mir geht es gut."], memory.Lines.Select(l => l.Text));
        var relation = host.Inhabitants.Get(id).Relationships.Get(ConversationSystem.PlayerId, default);
        Assert.Equal(host.Content.Knowledge.RelationshipStart.Trust + rule.Trust, relation.Trust, 9);
        Assert.False(host.Conversation.IsWaiting(id));
    }

    [Fact]
    public void TheLiveServiceAnswerOfMarcelsTestBecomesASaidEvent()
    {
        // W1688: the exact answer the local service sent in the play test (only id and tick follow the
        // request) reaches the presentation as "said" with the text and the wave expression.
        var (host, id) = Host();
        host.Conversation.Port = new RawPort("""
            {"contract_version":"0.2","request_id":"{id}","tick":{tick},"outcome":"reply","reply":{"locale":"de","text":"Hallo!"},"expression":"wave","rationale":"I respond to the greeting with a simple wave and return it in German.","meta":{"source":"model","model":"qwen3.5:9b","latency_ms":1453,"attempts":1}}
            """);
        var events = Collect(host);
        Say(host, id, "Hallo");
        host.World.Run(2);

        var said = Assert.Single(events, e => e.Kind == "said");
        Assert.Equal(id, said.InhabitantId);
        Assert.Equal("Hallo!", said.Text);
        Assert.Equal("wave", said.Detail);
        Assert.DoesNotContain(events, e => e.Kind == "no_answer");
    }

    [Fact]
    public void TheAnswerIsOnlyAProposal()
    {
        // A reply the guard refuses becomes a gesture; an unknown expression is dropped (B2462).
        var (host, id) = Host();
        host.Conversation.Port = new FixedPort(ConversationResponseOutcome.Reply, "Ich will dich töten", "fly_away");
        var events = Collect(host);
        Say(host, id, "Hallo!");
        host.World.Run(2);
        var gesture = Assert.Single(events, e => e.Kind == "gestured");
        Assert.Null(gesture.Detail);
        Assert.DoesNotContain(events, e => e.Kind == "said");
        Assert.DoesNotContain(host.Conversation.MemoryOf(id).Lines, l => !l.FromPlayer);
    }

    [Fact]
    public void NotUnderstoodBecomesAQuestionWithAQuestionMark()
    {
        var (host, id) = Host();
        host.Conversation.Port = new FixedPort(ConversationResponseOutcome.Not_understood, null, null);
        var events = Collect(host);
        Say(host, id, "Kannst du mir das Feuer zeigen?");
        host.World.Run(2);
        Assert.Contains(events, e => e.Kind == "not_understood" && e.InhabitantId == id);
        Assert.Contains(events, e => e.Kind == "question_opened" && e.InhabitantId == id); // B2511: a question with a question mark
    }

    [Fact]
    public void WithoutAServiceACalmNoAnswerGivesTheFavourBack()
    {
        var (host, id) = Host();
        var favor = host.Player.Favor;
        var events = Collect(host);
        Say(host, id, "Hallo?");
        var hours = (long)Math.Ceiling(host.Content.Conversation.NoAnswerAfterHours * host.World.Calendar.Settings.TicksPerHour);
        host.World.Run(hours + 2);
        Assert.Contains(events, e => e.Kind == "no_answer" && e.InhabitantId == id);
        Assert.False(host.Conversation.IsWaiting(id));
        Assert.True(host.Player.Favor >= favor - 1e-9, $"favour {host.Player.Favor} back to at least {favor}");
    }

    [Fact]
    public void RecentLinesAreBoundedByTheData()
    {
        var (host, id) = Host();
        host.Conversation.Port = new FixedPort(ConversationResponseOutcome.Reply, "Ja.", null);
        for (var i = 0; i < 10; i++)
        {
            Say(host, id, $"Satz {i}");
            host.World.Run(2);
        }

        Assert.Equal(host.Content.Conversation.RecentLinesMax, host.Conversation.MemoryOf(id).Lines.Count);
    }

    [Fact]
    public void TheLogIsBoundedAndThePlayerIdIsReserved()
    {
        var (host, id) = Host();
        host.Conversation.Port = new FixedPort(ConversationResponseOutcome.Reply, "Ja.", null);
        var max = host.Content.Conversation.LogMaxEntries;
        for (var i = 0; i < (max / 2) + 10; i++)
        {
            Say(host, id, $"Satz {i}");
            host.World.Step();
        }

        Assert.True(host.Conversation.Log.Count <= max, $"{host.Conversation.Log.Count} log entries above {max}");
        Assert.Equal("msg.command.no_inhabitant", Say(host, ConversationSystem.PlayerId, "Hallo").ReasonKey);
    }

    [Fact]
    public void ANewWorldStaysOutOfTheHashAndAReplayGivesTheSameWorld()
    {
        var fresh = Host().Host;
        Assert.False(fresh.Conversation.HasState);

        var (live, id) = Host();
        live.Conversation.Port = new MockConversationPort();
        var replay = Host().Host;
        replay.Conversation.Port = new ReplayConversationPort([]);
        for (var round = 0; round < 3; round++)
        {
            Say(live, id, $"Hallo {round}");
            live.World.Run(3);
        }

        // The replay gets the commands and the logged answers only.
        var log = live.Conversation.Log;
        replay.Conversation.Port = new ReplayConversationPort(log);
        replay.Decisions.LoadReplay(live.Decisions.Log);
        replay.World.Run(live.World.Tick);
        Assert.Equal(live.World.StateHash(), replay.World.StateHash());
    }

    [Fact]
    public void ConversationsSurviveSaveAndLoad()
    {
        var (host, id) = Host();
        host.Conversation.Port = new MockConversationPort();
        Say(host, id, "Hallo!");
        host.World.Run(3);
        host.Conversation.Port = null; // the second sentence still waits when the game is saved
        Say(host, id, "Und jetzt?");
        host.World.Step();
        var path = Path.Combine(_directory, "talk.sqlite");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        var loaded = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new ReplayDecisionPort(host.Decisions.Log), "de"), SaveGameStore.Load(path));
        Assert.Equal(host.Conversation.MemoryOf(id), loaded.Conversation.MemoryOf(id), new MemoryComparer());
        Assert.True(loaded.Conversation.IsWaiting(id));
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
    }

    private static (SimulationHost Host, string Id) Host()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 1, new MockDecisionPort(), "de");
        return (host, host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).First().Id);
    }

    private static CommandResult Say(SimulationHost host, string id, string text) =>
        host.Decisions.Submit(new PlayerCommand(ConversationSystem.SendText, id) { Text = text });

    private static List<WorldEvent> Collect(SimulationHost host)
    {
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        return events;
    }

    private sealed class FixedPort(ConversationResponseOutcome outcome, string? text, string? expression) : IConversationPort
    {
        private readonly Queue<PortMessage> _ready = new();

        public void Send(string requestId, string requestJson)
        {
            var request = ContractSerializer.Deserialize<ConversationRequest>(requestJson);
            var response = new ConversationResponse
            {
                ContractVersion = request.ContractVersion,
                RequestId = request.RequestId,
                Tick = request.Tick,
                Outcome = outcome,
                Reply = text is null ? null : new GeneratedText { Locale = request.DisplayLocale, Text = text },
                Expression = expression,
                Rationale = "test",
                Meta = new ResponseMeta { Source = ResponseMetaSource.Mock, Model = "test", LatencyMs = 0, Attempts = 1 },
            };
            _ready.Enqueue(new PortMessage(requestId, ContractSerializer.Serialize(response), IsError: false));
        }

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            var messages = _ready.ToList();
            _ready.Clear();
            return messages;
        }
    }

    /// <summary>Answers with a fixed JSON text ({id} and {tick} take the request's values).</summary>
    private sealed class RawPort(string json) : IConversationPort
    {
        private readonly Queue<PortMessage> _ready = new();

        public void Send(string requestId, string requestJson)
        {
            var request = ContractSerializer.Deserialize<ConversationRequest>(requestJson);
            var body = json.Trim().Replace("{id}", request.RequestId, StringComparison.Ordinal).Replace("{tick}", request.Tick.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            _ready.Enqueue(new PortMessage(requestId, body, IsError: false));
        }

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            var messages = _ready.ToList();
            _ready.Clear();
            return messages;
        }
    }

    private sealed class MemoryComparer : IEqualityComparer<ConversationMemory>
    {
        public bool Equals(ConversationMemory? x, ConversationMemory? y) =>
            x is not null && y is not null && x.Understanding == y.Understanding && x.Fear == y.Fear && x.Lines.SequenceEqual(y.Lines);

        public int GetHashCode(ConversationMemory obj) => obj.Lines.Count;
    }
}
