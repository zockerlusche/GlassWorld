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
    public void PraiseLiftsTheMoodAndTheRelationOnceAnHourOnlyWhenUnderstood()
    {
        // A1538 (B2678): what praise does is data, on top of the reply's own changes; at most once per every_minutes.
        var (host, id) = Host();
        var praise = host.Content.Conversation.Praise!;
        var rule = host.Content.Conversation.Outcomes.Single(o => o.Outcome == OutcomeChangeOutcome.Reply);
        host.Conversation.Port = new FixedPort(ConversationResponseOutcome.Reply, "Danke!", null, ConversationSystem.PraiseKind);
        var events = Collect(host);
        var inhabitant = host.Inhabitants.Get(id);
        var joy = inhabitant.Moods[praise.Mood];
        var trust = inhabitant.Relationships.Get(ConversationSystem.PlayerId, default).Trust;
        if (trust == 0)
        {
            trust = host.Content.Knowledge.RelationshipStart.Trust;
        }

        Say(host, id, "Das hast du gut gemacht!");
        host.World.Run(2);
        Assert.Single(events, e => e.Kind == ConversationSystem.Praised);
        // Moods fade a little every tick (mood dynamics), so within a small tolerance.
        Assert.InRange(inhabitant.Moods[praise.Mood], Math.Min(1, joy + praise.MoodDelta) - 0.005, Math.Min(1, joy + praise.MoodDelta));
        Assert.Equal(trust + rule.Trust + praise.Trust, inhabitant.Relationships.Get(ConversationSystem.PlayerId, default).Trust, 6);

        // Again within the hour: the reply counts, the praise does not.
        host.World.Run(2);
        Say(host, id, "Toll!");
        host.World.Run(2);
        Assert.Single(events, e => e.Kind == ConversationSystem.Praised);

        // Not understood: no praise either.
        host.World.Run(praise.EveryMinutes * host.World.Calendar.Settings.TicksPerHour / 60);
        host.Conversation.Port = new FixedPort(ConversationResponseOutcome.Not_understood, "Was?", null, ConversationSystem.PraiseKind);
        Say(host, id, "Super!");
        host.World.Run(2);
        Assert.Single(events, e => e.Kind == ConversationSystem.Praised);
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

    /// <summary>W1701: asking back is said like a decline - the guarded sentence and the expression go with the event.</summary>
    [Theory]
    [InlineData("not_understood")]
    [InlineData("declined")]
    public void NotUnderstoodAndDeclineCarryTheirGuardedSentence(string kind)
    {
        var outcome = kind == "declined" ? ConversationResponseOutcome.Decline : ConversationResponseOutcome.Not_understood;
        foreach (var (text, shown) in new[] { ("Feuer? Was ist das?", "Feuer? Was ist das?"), ("Ich will dich töten", null) })
        {
            var (host, id) = Host();
            host.Conversation.Port = new FixedPort(outcome, text, "wave");
            var events = Collect(host);
            Say(host, id, "Kannst du mir das Feuer zeigen?");
            host.World.Run(2);
            var e = Assert.Single(events, e => e.Kind == kind);
            Assert.Equal(shown, e.Text); // a sentence the guard refuses is never shown
            Assert.Equal("wave", e.Detail);
        }
    }

    [Fact]
    public void AReplayOfALoggedNotUnderstoodGivesTheSameSentenceAndWorld()
    {
        var (live, id) = Host();
        live.Conversation.Port = new FixedPort(ConversationResponseOutcome.Not_understood, "Glas? Welches Glas meinst du?", null);
        var said = Collect(live);
        Say(live, id, "Bring mir das Glas.");
        live.World.Run(3);

        var replay = Host().Host;
        replay.Conversation.Port = new ReplayConversationPort(live.Conversation.Log);
        replay.Decisions.LoadReplay(live.Decisions.Log);
        var replayed = Collect(replay);
        replay.World.Run(live.World.Tick);
        Assert.Equal(live.World.StateHash(), replay.World.StateHash());
        Assert.Equal(
            said.Where(e => e.Kind == "not_understood").Select(e => e.Text),
            replayed.Where(e => e.Kind == "not_understood").Select(e => e.Text));
        Assert.Contains(replayed, e => e.Kind == "not_understood" && e.Text == "Glas? Welches Glas meinst du?");
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
        // W1701: a calm line (text key, the game translates it) instead of nothing.
        Assert.Contains(events, e => e.Kind == "no_answer" && e.InhabitantId == id && e.Text == ConversationSystem.NoAnswerKey);
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

    [Fact]
    public void TheHistoryKeepsMoreThanTheRecentLinesAndSurvivesSaveAndLoad()
    {
        // B8 R16: the memory page shows old conversations - more than the few lines sent as context, bounded by data.
        var (host, id) = Host();
        host.Conversation.Port = new MockConversationPort();
        var settings = ContentSet.Load(ContentDirectory).Conversation;
        var sentences = settings.RecentLinesMax + 3;
        for (var i = 0; i < sentences; i++)
        {
            Say(host, id, $"Satz {i}");
            host.World.Run(3);
        }

        var history = host.Conversation.HistoryOf(id);
        Assert.True(history.Count > host.Conversation.MemoryOf(id).Lines.Count);
        Assert.True(history.Count <= settings.HistoryLinesMax!.Value);
        Assert.Equal("Satz 0", history.First(l => l.FromPlayer).Text);
        Assert.Equal(history.OrderBy(l => l.Tick).Select(l => l.Tick), history.Select(l => l.Tick));
        Assert.Empty(host.Conversation.HistoryOf("inh-999999"));

        var path = Path.Combine(_directory, "history.sqlite");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var decider = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        var loaded = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), decider, new ReplayDecisionPort(host.Decisions.Log), "de"), SaveGameStore.Load(path));
        Assert.Equal(history, loaded.Conversation.HistoryOf(id));
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
    }

    [Fact]
    public void TheRequestSaysWhatTheSpeakerDoesKnowsAndSees()
    {
        // W1737 (Marcel: inhabitants invented activities like reading a book): the request carries the
        // running action with its thing and start, the beliefs and the types in sight - beliefs, never the truth.
        // The action looked for is one on a thing: since W1744 the routine also talks to a partner.
        var (host, _) = Host();
        var port = new CapturePort();
        host.Conversation.Port = port;
        Inhabitant? busy = null;
        for (var i = 0; i < 2000 && busy is null; i++)
        {
            host.World.Run(1);
            busy = host.Inhabitants.All.OrderBy(x => x.Id, StringComparer.Ordinal)
                .FirstOrDefault(x => x.Activity is { RemainingTicks: >= 5, TargetId: { } target } && host.Objects.TryGet(target, out _));
        }

        Assert.NotNull(busy);
        var activity = busy.Activity!;
        var entry = host.Diary.Of(busy.Id)[^1];
        Say(host, busy.Id, "Was machst du?");
        host.World.Run(1); // the sentence is taken up in the next tick; the action is still running then
        var json = Assert.Single(port.Requests);
        Assert.Empty(ContractValidator.Validate("conversation-request", json));
        var speaker = ContractSerializer.Deserialize<ConversationRequest>(json).Speaker;

        Assert.NotNull(speaker.Doing);
        Assert.Equal(activity.Action, speaker.Doing.Action);
        Assert.Equal(entry.Tick, speaker.Doing.SinceTick);
        Assert.True(speaker.Doing.SinceTick <= host.World.Tick);
        Assert.Equal(SubjectKind.Type, speaker.Doing.Target!.Kind);
        Assert.Equal(entry.TargetType, speaker.Doing.Target.Id);

        var sees = speaker.Sees!.ToList();
        Assert.NotEmpty(sees);
        Assert.Equal(sees.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), sees);
        Assert.True(sees.Count <= 16);

        var known = speaker.Knowledge!.ToList();
        Assert.True(known.Count <= 24);
        Assert.All(known, k =>
        {
            Assert.True(busy.Beliefs.TryGet(k.Subject.Id, k.Property, out var belief));
            Assert.Equal(belief.Value, k.Value);
            Assert.Equal(belief.Confidence, k.Confidence, 9);
        });

        // First what it sees, then the rest, newest first.
        var seenTypes = sees.ToHashSet(StringComparer.Ordinal);
        var rest = known.SkipWhile(k => seenTypes.Contains(k.Subject.Id)).ToList();
        Assert.DoesNotContain(rest, k => seenTypes.Contains(k.Subject.Id));
        Assert.Equal(rest.OrderByDescending(k => k.Tick).Select(k => k.Tick), rest.Select(k => k.Tick));
    }

    [Fact]
    public void ASpeakerDoingNothingSaysSoAndOneOnItsWayHasJustSetOut()
    {
        // The sentence is taken up one tick after it was sent, so each case is only judged when the speaker
        // is still in the same state then.
        var (host, _) = Host();
        var port = new CapturePort();
        host.Conversation.Port = port;
        var sawIdle = false;
        var sawWalking = false;
        for (var i = 0; i < 6000 && !(sawIdle && sawWalking); i++)
        {
            host.World.Run(1);
            var all = host.Inhabitants.All.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
            var idle = sawIdle ? null : all.FirstOrDefault(x => x.Activity is null && !host.Conversation.IsWaiting(x.Id));
            var walking = sawWalking ? null : all.FirstOrDefault(x => x != idle && x.Activity is { RemainingTicks: null } && !host.Conversation.IsWaiting(x.Id));
            var asked = host.World.Tick;
            var action = walking?.Activity!.Action;
            var sent = port.Requests.Count;
            if (idle is not null)
            {
                Say(host, idle.Id, "Hallo");
            }

            if (walking is not null)
            {
                Say(host, walking.Id, "Wohin gehst du?");
            }

            if (idle is null && walking is null)
            {
                continue;
            }

            host.World.Run(1);
            foreach (var json in port.Requests.Skip(sent))
            {
                var speaker = ContractSerializer.Deserialize<ConversationRequest>(json).Speaker;
                if (idle is not null && speaker.Id == idle.Id && idle.Activity is null)
                {
                    Assert.Null(speaker.Doing);
                    sawIdle = true;
                }

                if (walking is not null && speaker.Id == walking.Id && walking.Activity is { RemainingTicks: null } still && still.Action == action)
                {
                    Assert.NotNull(speaker.Doing);
                    Assert.Equal(action, speaker.Doing.Action);
                    Assert.True(speaker.Doing.SinceTick >= asked, $"since {speaker.Doing.SinceTick}, asked at {asked}");
                    sawWalking = true;
                }
            }
        }

        Assert.True(sawWalking, "no inhabitant was asked on its way");
    }

    /// <summary>Keeps the requests and never answers.</summary>
    private sealed class CapturePort : IConversationPort
    {
        public List<string> Requests { get; } = [];

        public void Send(string requestId, string requestJson) => Requests.Add(requestJson);

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }

    private static (SimulationHost Host, string Id) Host()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 1, new MockDecisionPort(), "de");
        return (host, host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).First().Id);
    }

    [Fact]
    public void WhatThePlayerPointsAtGoesWithTheSentenceOnlyWhenTheSpeakerSeesIt()
    {
        // A1617 (B2678): the click on a thing gives the sentence its type - only one the speaker sees.
        var (host, id) = Host();
        var port = new CapturePort();
        host.Conversation.Port = port;
        host.World.Run(1);
        var speaker = host.Inhabitants.Get(id);
        // The last of what it sees: even beyond the request's limit of listed sights it is listed (review of GW2 #341).
        var seen = host.Decisions.PerceivedTypes(speaker)[^1];
        Assert.True(host.Decisions.Submit(new PlayerCommand(ConversationSystem.SendText, id) { Text = "Was ist das?", TypeId = seen }).Accepted);
        host.World.Run(1);
        var pointed = ContractSerializer.Deserialize<ConversationRequest>(Assert.Single(port.Requests));
        Assert.Equal(seen, pointed.PointedAt);
        Assert.Contains(seen, pointed.Speaker.Sees!);

        var (other, otherId) = Host();
        var otherPort = new CapturePort();
        other.Conversation.Port = otherPort;
        other.World.Run(1);
        Assert.True(other.Decisions.Submit(new PlayerCommand(ConversationSystem.SendText, otherId) { Text = "Was ist das?", TypeId = "rock_niche_never_seen_here" }).Accepted);
        other.World.Run(1);
        var json = Assert.Single(otherPort.Requests);
        Assert.Empty(ContractValidator.Validate("conversation-request", json));
        Assert.Null(ContractSerializer.Deserialize<ConversationRequest>(json).PointedAt);
    }

    private static CommandResult Say(SimulationHost host, string id, string text) =>
        host.Decisions.Submit(new PlayerCommand(ConversationSystem.SendText, id) { Text = text });

    private static List<WorldEvent> Collect(SimulationHost host)
    {
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        return events;
    }

    private sealed class FixedPort(ConversationResponseOutcome outcome, string? text, string? expression, string? voice = null) : IConversationPort
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
                Voice = voice is null ? null : new VoiceClass { Kind = voice },
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
