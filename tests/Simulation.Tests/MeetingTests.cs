using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B4a R7/R8 (F5249): the start pairs know each other, the other pair are strangers; two inhabitants who have
/// not met see each other once (first_meeting); shared experiences and time together change the relationship
/// as the data says and closeness fades slowly; approach and talk_to are offered under the data's conditions.
/// </summary>
public sealed class MeetingTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);
    private static readonly KnowledgeSettings Knowledge = Content.Knowledge;
    private static readonly KnowledgeStartRelationships Starts = Knowledge.StartRelationships!;
    private static readonly KnowledgeRelationshipChanges Changes = Knowledge.RelationshipChanges!;
    private static readonly DeciderSettings Settings = ContractSerializer.Deserialize<DeciderSettings>(
        File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));

    [Fact]
    public void PartnersStartAsKnownAndTheOtherPairAsStrangersBelowTheTogethernessThresholds()
    {
        var (_, inhabitants, _) = NewWorld();
        var (a, b, c) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"), inhabitants.Get("inh-000003"));
        Assert.Equal(a.Group, b.Group);
        Assert.NotEqual(a.Group, c.Group);

        Assert.Equal(Of(Starts.Partners), a.Relationships.Get(b.Id, default));
        Assert.Equal(Of(Starts.Strangers), a.Relationships.Get(c.Id, default));
        Assert.Equal(Of(Starts.Strangers), c.Relationships.Get(a.Id, default));
        Assert.Contains(b.Id, a.Met);
        Assert.DoesNotContain(c.Id, a.Met);

        var together = Content.InhabitantModel.Togetherness!;
        Assert.True(Starts.Strangers.Trust < together.MinTrust || Starts.Strangers.Affection < together.MinAffection, "an unknown human is not company");
        Assert.True(Starts.Partners.Trust >= together.MinTrust && Starts.Partners.Affection >= together.MinAffection);
        Assert.True(Changes.FadeTo.Trust >= together.MinTrust && Changes.FadeTo.Affection >= together.MinAffection, "partners stay familiar as closeness fades");
    }

    [Fact]
    public void TheTwoPairsOfTheStartIslandStartApartMeetOnceAndTheirRelationshipsChange()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 1, new MockDecisionPort(), "de", "start_island");
        var all = host.Inhabitants.All.ToList();
        var sight = host.Inhabitants.SightRadiusM;
        foreach (var x in all)
        {
            foreach (var y in all.Where(y => y.Group != x.Group))
            {
                Assert.DoesNotContain(y.Id, x.Met);
                Assert.True(Distance(x.Position, y.Position) > sight, "the pairs wake up apart");
            }
        }

        var meetings = new List<WorldEvent>();
        host.Inhabitants.Reported += e =>
        {
            if (e.Kind == InhabitantsSystem.FirstMeeting)
            {
                meetings.Add(e);
            }
        };
        var ticksPerDay = host.World.Calendar.TicksPerDay;
        for (var t = 0; t < 3 * ticksPerDay && meetings.Count == 0; t++)
        {
            host.World.Step();
        }

        Assert.NotEmpty(meetings);
        var first = meetings[0];
        var (one, other) = (host.Inhabitants.Get(first.InhabitantId), host.Inhabitants.Get(first.TargetId!));
        Assert.NotEqual(one.Group, other.Group);
        Assert.Contains(other.Id, one.Met);
        Assert.Contains(one.Id, other.Met);
        Assert.Contains(host.Chronicle.All, r => r.Kind == InhabitantsSystem.FirstMeeting && r.InhabitantId == one.Id && r.TargetId == other.Id);

        host.World.Run(ticksPerDay);
        Assert.Single(meetings, e => e.InhabitantId == one.Id && e.TargetId == other.Id);
        var strangers = Of(Starts.Strangers);

        // Within the day after the meeting a relationship between the pairs has changed. (For a while this
        // needed two more days: the decider's first choice towards a stranger, queued behind a running talk,
        // was replaced by its own answer to the next question. A1487 ended that: one thing at a time.)
        Assert.Contains(all.SelectMany(x => all.Where(y => y.Group != x.Group).Select(y => x.Relationships.Get(y.Id, strangers))), r => r != strangers);
    }

    [Fact]
    public void FirstMeetingHappensOnceWhenTwoStrangersSeeEachOther()
    {
        var (world, inhabitants, events) = NewWorld();
        var (a, c) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000003"));
        world.Step();
        Assert.DoesNotContain(events, e => e.Kind == InhabitantsSystem.FirstMeeting);

        c.Position = new Position(a.Position.X + (inhabitants.SightRadiusM * 0.5), a.Position.Z);
        world.Step();
        world.Step();

        var met = Assert.Single(events, e => e.Kind == InhabitantsSystem.FirstMeeting && e.InhabitantId == a.Id);
        Assert.Equal((a.Id, c.Id), (met.InhabitantId, met.TargetId));
        Assert.Contains(c.Id, a.Met);
        Assert.Contains(a.Id, c.Met);
    }

    [Fact]
    public void TimeTogetherAndActionsChangeTheRelationshipByTheDataAndClosenessFades()
    {
        var (world, inhabitants, _) = NewWorld();
        var (a, c) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000003"));
        c.Position = new Position(a.Position.X + (Changes.TogetherDistanceM * 0.5), a.Position.Z);
        var perTick = 1.0 / world.Calendar.Settings.TicksPerHour;
        var before = a.Relationships.Get(c.Id, default);
        world.Step();
        var after = a.Relationships.Get(c.Id, default);
        Assert.Equal(before.Affection + (Changes.TogetherPerHour.Affection * perTick), after.Affection, 12);
        Assert.Equal(before.Trust + (Changes.TogetherPerHour.Trust * perTick), after.Trust, 12);

        // Talking: both change by the action's data when it ends.
        var talk = Changes.Actions.Single(r => r.Action == "talk_to");
        var (mine, theirs) = (a.Relationships.Get(c.Id, default), c.Relationships.Get(a.Id, default));
        a.Activity = new Activity("talk_to", c.Id, c.Position, 1);
        world.Step();
        var together = Changes.TogetherPerHour.Affection * perTick;
        Assert.Equal(mine.Affection + together + talk.Actor.Affection, a.Relationships.Get(c.Id, default).Affection, 9);
        Assert.Equal(theirs.Affection + together + talk.Listener.Affection, c.Relationships.Get(a.Id, default).Affection, 9);

        // Far apart, closeness above fade_to sinks hour by hour, never below it; what lies below stays.
        c.Position = new Position(500, 500);
        a.Relationships.Set(c.Id, new Relation(Changes.FadeTo.Affection + 0.001, Changes.FadeTo.Trust - 0.2, 0.9));
        world.Run(world.Calendar.Settings.TicksPerHour * 2);
        var faded = a.Relationships.Get(c.Id, default);
        Assert.Equal(Changes.FadeTo.Affection, faded.Affection, 12);
        Assert.Equal(Changes.FadeTo.Trust - 0.2, faded.Trust, 12);
        Assert.Equal(0.9 - (2 * Changes.FadePerHour), faded.Respect, 9);
    }

    [Fact]
    public void ApproachAndTalkToAreOfferedOnlyUnderTheirDataConditions()
    {
        var (_, inhabitants, _) = NewWorld();
        var (a, b, c) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"), inhabitants.Get("inh-000003"));
        var approach = Knowledge.Meeting!.Options.Single(o => o.Action == "approach");
        var talk = Knowledge.Meeting.Options.Single(o => o.Action == "talk_to");

        c.Position = new Position(a.Position.X + approach.MinDistanceM!.Value + 1, a.Position.Z);
        Assert.True(InhabitantsSystem.HasPurpose(Knowledge, a, c, "approach"), "a stranger farther away");
        b.Position = c.Position;
        Assert.False(InhabitantsSystem.HasPurpose(Knowledge, a, b, "approach"), "the partner is trusted");
        c.Position = a.Position;
        Assert.False(InhabitantsSystem.HasPurpose(Knowledge, a, c, "approach"), "already close");

        a.Needs[talk.Need!] = talk.NeedBelow!.Value - 0.01;
        Assert.True(InhabitantsSystem.HasPurpose(Knowledge, a, c, "talk_to"));
        a.Needs[talk.Need!] = talk.NeedBelow!.Value;
        Assert.False(InhabitantsSystem.HasPurpose(Knowledge, a, c, "talk_to"));
    }

    [Fact]
    public void AnInhabitantWhoseNameIsUnknownBecomesAQuestionWithMeetingOptions()
    {
        var port = new SilentPort();
        var world = new World(3, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Knowledge,
            Names = Content.InhabitantNames,
        };
        var decisions = new DecisionsSystem(Settings, inhabitants, objects, Content.Actions, Content.Brain, Content.Vocabularies[VocabularyKind.Action], port, Content.Inventions.Guard, "de")
        {
            Knowledge = Knowledge,
        };
        world.Add(objects).Add(decisions).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [new(0, 0), new(500, 500), new(10, 0), new(-500, -500)]);
        inhabitants.Get("inh-000001").Needs["company"] = 0.1;
        world.Step();

        var request = Assert.Single(port.Requests, r => r.Inhabitant.Id == "inh-000001");
        Assert.Empty(ContractValidator.Validate("decision-request", ContractSerializer.Serialize(request)));
        Assert.Contains(decisions.Open, q => q.InhabitantId == "inh-000001" && q.Kind == DecisionsSystem.UnknownInhabitant && q.Subject == "inhabitant:inh-000003");
        Assert.Contains(request.Options, o => o.Action == "approach" && o.Target == "inh-000003");
        Assert.Contains(request.Options, o => o.Action == "talk_to" && o.Target == "inh-000003");
        Assert.Contains(request.Options, o => o.Action == "introduce" && o.Target == "inh-000003");
        var seen = Assert.Single(request.Inhabitant.Relationships, r => r.Other == "inh-000003");
        Assert.Equal(Starts.Strangers.Trust, seen.Trust);
    }

    [Fact]
    public void ASleeperIsNeitherAskedAboutNorOfferedAsATarget()
    {
        // W1744 (review GW2 #228): an answer aimed at a sleeper is cancelled by the rule checker, so offering
        // it would be an AI call for nothing. The sleeper is still seen; it is asked about once it is awake.
        // This is about whom a question names, not about when it goes out: the decider never answers here, so
        // the second question would wait behind the first (A1487, OneThingAtATimeTests) - switched off.
        var port = new SilentPort();
        var settings = ContractSerializer.Deserialize<DeciderSettings>(ContractSerializer.Serialize(Settings));
        settings.ChoiceProtectionHours = null;
        var (world, inhabitants, decisions) = AskingWorld(settings, port, [new(0, 0), new(5, 0), new(10, 0), new(-500, -500)]);
        var (asker, partner, stranger) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"), inhabitants.Get("inh-000003"));
        partner.Activity = new Activity(Content.InhabitantModel.Togetherness!.SleepAction, null, partner.Position, 100_000);
        Assert.True(inhabitants.IsAsleep(partner));
        Assert.False(inhabitants.IsAsleep(stranger));
        asker.Needs["company"] = 0.1;
        world.Step();

        // The nearer one sleeps: the question is about the waking stranger, and no option is aimed at the sleeper.
        Assert.Contains(decisions.Open, q => q.InhabitantId == asker.Id && q.Subject == "inhabitant:" + stranger.Id);
        Assert.DoesNotContain(decisions.Open, q => q.InhabitantId == asker.Id && q.Subject == "inhabitant:" + partner.Id);
        var asked = port.Requests.Where(r => r.Inhabitant.Id == asker.Id).ToList();
        Assert.NotEmpty(asked);
        Assert.All(asked, r => Assert.DoesNotContain(r.Options, o => o.Target == partner.Id));
        Assert.All(asked, r => Assert.Contains(r.Perception.Inhabitants, i => i.Id == partner.Id));
        Assert.Contains(asked.SelectMany(r => r.Options), o => o.Target == stranger.Id);

        // On its way to bed it is still awake; once awake and seen again the question comes, with its options.
        partner.Activity = new Activity("idle", null, partner.Position, 100_000);
        world.Step();
        Assert.Contains(decisions.Open, q => q.InhabitantId == asker.Id && q.Subject == "inhabitant:" + partner.Id);
        Assert.Contains(port.Requests.Last(r => r.Inhabitant.Id == asker.Id).Options, o => o.Target == partner.Id);
    }

    [Fact]
    public void LoadingAStateWithoutMeetingsNeverLetsPartnersMeetAgain()
    {
        // Review GW3 #207: a state from a path without meetings (older saves, snapshots) keeps the start groups met.
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), "de");
        host.World.Run(10);
        var state = StateCodec.Capture(host);
        state = state with { Inhabitants = [.. state.Inhabitants.Select(i => i with { Met = null })] };

        var loaded = StateCodec.Restore(new SimulationHostFactory(Content, Settings, new MockDecisionPort(), "de"), state);
        var meetings = new List<WorldEvent>();
        loaded.Inhabitants.Reported += e =>
        {
            if (e.Kind == InhabitantsSystem.FirstMeeting)
            {
                meetings.Add(e);
            }
        };
        loaded.World.Run(loaded.World.Calendar.Settings.TicksPerHour * 6);

        Assert.Equal(host.World.Tick, state.Tick);
        foreach (var i in loaded.Inhabitants.All)
        {
            Assert.All(loaded.Inhabitants.All.Where(o => o != i && o.Group == i.Group), o => Assert.Contains(o.Id, i.Met));
        }

        Assert.DoesNotContain(meetings, e => loaded.Inhabitants.Get(e.InhabitantId).Group == loaded.Inhabitants.Get(e.TargetId!).Group);
    }

    [Fact]
    public void AStrangerCoolingDownDoesNotHideASecondStranger()
    {
        var port = new SilentPort();
        var (world, _, decisions) = AskingWorld(Settings, port, [new(0, 0), new(500, 500), new(6, 0), new(9, 0)]);
        world.Step();
        world.Step();

        Assert.Contains(decisions.Open, q => q.InhabitantId == "inh-000001" && q.Subject == "inhabitant:inh-000003");
        Assert.Contains(decisions.Open, q => q.InhabitantId == "inh-000001" && q.Subject == "inhabitant:inh-000004");
    }

    [Fact]
    public void WhoTheOtherIsIsAskedAtMostTheDataTimesPerPair()
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(ContractSerializer.Serialize(Settings));
        settings.AskCooldownHours = 0;
        settings.UnknownInhabitantMaxAsks = 2;
        var (world, inhabitants, _) = AskingWorld(settings, new NoTargetPort(), [new(0, 0), new(500, 500), new(6, 0), new(-500, -500)]);
        var asked = new List<WorldEvent>();
        inhabitants.Reported += e =>
        {
            if (e.Kind == "question_opened" && e.InhabitantId == "inh-000001")
            {
                asked.Add(e);
            }
        };
        world.Run(200);

        Assert.DoesNotContain("inh-000003", inhabitants.Get("inh-000001").KnownNames);
        Assert.Equal(settings.UnknownInhabitantMaxAsks, asked.Count);
    }

    /// <summary>The start inhabitants at the given spots with names, knowledge and decisions; inh-000001 idles, needs met.</summary>
    private static (World World, InhabitantsSystem Inhabitants, DecisionsSystem Decisions) AskingWorld(DeciderSettings settings, IDecisionPort port, Position[] at)
    {
        var world = new World(3, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Knowledge,
            Names = Content.InhabitantNames,
        };
        var decisions = new DecisionsSystem(settings, inhabitants, objects, Content.Actions, Content.Brain, Content.Vocabularies[VocabularyKind.Action], port, Content.Inventions.Guard, "de")
        {
            Knowledge = Knowledge,
        };
        world.Add(objects).Add(decisions).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, at);
        foreach (var inhabitant in inhabitants.All)
        {
            inhabitant.Activity = new Activity("idle", null, inhabitant.Position, 100_000);
        }

        return (world, inhabitants, decisions);
    }

    private static Relation Of(KnowledgeRelationship r) => new(r.Affection, r.Trust, r.Respect);

    private static double Distance(Position a, Position b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    /// <summary>Pair 1 at the origin, pair 2 far away; everyone idles until told otherwise.</summary>
    private static (World World, InhabitantsSystem Inhabitants, List<WorldEvent> Events) NewWorld()
    {
        var world = new World(3, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Knowledge,
            Names = Content.InhabitantNames,
            EventKinds = Content.EventKinds,
        };
        world.Add(objects).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [new(0, 0), new(1.5, 0), new(400, 400), new(401.5, 400)]);
        foreach (var inhabitant in inhabitants.All)
        {
            inhabitant.Activity = new Activity("idle", null, inhabitant.Position, 100_000);
        }

        var events = new List<WorldEvent>();
        inhabitants.Reported += events.Add;
        return (world, inhabitants, events);
    }

    /// <summary>Answers every request at the next tick with its first option without a target (nobody learns a name).</summary>
    private sealed class NoTargetPort : IDecisionPort
    {
        private readonly List<PortMessage> _ready = [];

        public void Send(string requestId, string requestJson)
        {
            var request = ContractSerializer.Deserialize<DecisionRequest>(requestJson);
            var option = request.Options.First(o => o.Target is null);
            _ready.Add(new PortMessage(requestId, ContractSerializer.Serialize(new DecisionResponse
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

    /// <summary>Keeps every request, never answers.</summary>
    private sealed class SilentPort : IDecisionPort
    {
        public List<DecisionRequest> Requests { get; } = [];

        public void Send(string requestId, string requestJson) => Requests.Add(ContractSerializer.Deserialize<DecisionRequest>(requestJson));

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
