using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Player;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// The AI never has to read a runtime type id (review GW2 #245): wherever a request or job names a kind of
/// thing as an inhabitant sees it, an invention carries the optional <c>label</c> - the name the inhabitant
/// itself knows it by (<see cref="InventionSystem.NameKnownTo"/>: its inventor, or whoever believes anything
/// about the type). An inhabitant that knows nothing of the thing gets no label, and what the data name never
/// gets one. Checked on the real messages: decision request, conversation request, question job and
/// invention job.
/// </summary>
public sealed class InventionNameInRequestsTests
{
    private const string Need = "thirst";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);

    // Test data from the start catalogue: the invention is made of the first, its step of the last.
    private static readonly string[] NearbyTypes = ["flint", "dry_grass", "pyrite"];

    [Fact]
    public void TheDecisionRequestNamesAnInventionOnlyForWhoKnowsSomethingAboutIt()
    {
        var (host, decisions, _, typeId, thing) = WorldWithAnInvention(51);
        var name = host.Inventions.Invention(typeId)!.Name;
        var stranger = Person(host, "inh-000002", new Position(thing.Position.X + 1, 0));
        var knowing = Person(host, "inh-000003", new Position(thing.Position.X + 1, 1));
        knowing.Beliefs.Set(typeId, "container", true, 0.8, KnowledgeSources.Seen, null, host.World.Tick);
        host.World.Step();

        // Somebody who knows nothing of the thing sees its kind without a name, like any unknown thing.
        var asked = Request(decisions, stranger.Id);
        var seen = asked.Perception.Objects.Single(o => o.Id == thing.Id);
        Assert.Equal(typeId, seen.Type);
        Assert.Null(seen.Label);
        Assert.All(asked.Perception.Objects, o => Assert.Null(o.Label));
        Assert.DoesNotContain("\"label\"", decisions.Json[asked.RequestId], StringComparison.Ordinal);

        // Somebody who believes anything about it calls it by its name: on the thing and on what it knows of it.
        var knows = Request(decisions, knowing.Id);
        var labelled = knows.Perception.Objects.Single(o => o.Id == thing.Id);
        Assert.Equal((name.De, name.En), (labelled.Label!.De, labelled.Label.En));
        var belief = Assert.Single(knows.Inhabitant.Knowledge, k => k.Subject.Id == typeId);
        Assert.Equal((name.De, name.En), (belief.Label!.De, belief.Label.En));

        // What the data name never gets one (the id is the vocabulary key).
        Assert.All(knows.Perception.Objects.Where(o => o.Type != typeId), o => Assert.Null(o.Label));
        Assert.Contains(knows.Perception.Objects, o => o.Type != typeId);
        Assert.Empty(ContractValidator.Validate("decision-request", decisions.Json[asked.RequestId]));
        Assert.Empty(ContractValidator.Validate("decision-request", decisions.Json[knows.RequestId]));
    }

    [Fact]
    public void KnowledgeAndHabitsCarryTheNameByTheSameRule()
    {
        var (host, _, _, typeId, _) = WorldWithAnInvention(52);
        var name = host.Inventions.Invention(typeId)!.Name;
        var inventor = host.Inhabitants.Get("inh-000001");
        var knowing = Person(host, "inh-000002", new Position(200, 0));
        var stranger = Person(host, "inh-000003", new Position(210, 0));
        var imprinting = Content.Knowledge.Imprinting!;
        foreach (var who in new[] { knowing, stranger })
        {
            who.Beliefs.Set("flint", "hard", true, 0.8, KnowledgeSources.Seen, null, host.World.Tick);
            who.Imprints[new ImprintKey("use", InhabitantsSystem.AboutType, typeId, "day")] = imprinting.HabitFrom + 1;
            who.Imprints[new ImprintKey("inspect", InhabitantsSystem.AboutType, "flint", "day")] = imprinting.HabitFrom + 1;
        }

        knowing.Beliefs.Set(typeId, "crafted", true, 0.5, KnowledgeSources.Heard, inventor.Id, host.World.Tick);

        // The one rule: its inventor, or whoever believes anything about the type.
        Assert.Equal(name.En, host.Inhabitants.TypeLabelOf(inventor, typeId)!.En);
        Assert.Equal(name.En, host.Inhabitants.TypeLabelOf(knowing, typeId)!.En);
        Assert.Null(host.Inhabitants.TypeLabelOf(stranger, typeId));
        Assert.All(new[] { inventor, knowing, stranger }, who => Assert.Null(host.Inhabitants.TypeLabelOf(who, "flint")));

        var state = InhabitantStates.Of(knowing, Content.Knowledge, [typeId, "flint"], [], host.Inhabitants.LabelOf, host.Inhabitants.TypeLabelOf);
        Assert.Equal(name.De, state.Knowledge.Single(k => k.Subject.Id == typeId).Label!.De);
        Assert.Null(state.Knowledge.Single(k => k.Subject.Id == "flint").Label);
        Assert.Equal(name.De, state.Habits.Single(h => h.About!.Id == typeId).Label!.De);
        Assert.Null(state.Habits.Single(h => h.About!.Id == "flint").Label);

        // A habit about a thing it believes nothing about (it cannot have one by acting, but the rule holds).
        var unknowing = InhabitantStates.Of(stranger, Content.Knowledge, [typeId, "flint"], [], host.Inhabitants.LabelOf, host.Inhabitants.TypeLabelOf);
        Assert.All(unknowing.Knowledge, k => Assert.Null(k.Label));
        Assert.All(unknowing.Habits, h => Assert.Null(h.Label));

        // Without the resolver nothing is labelled (callers that have none, older tests).
        Assert.All(InhabitantStates.Of(knowing, Content.Knowledge, [typeId], []).Knowledge, k => Assert.Null(k.Label));
    }

    [Fact]
    public void TheConversationRequestNamesWhatTheSpeakerSeesKnowsAndDoes()
    {
        var (host, _, _, typeId, thing) = WorldWithAnInvention(53);
        var name = host.Inventions.Invention(typeId)!.Name;
        var port = new CapturePort();
        host.Conversation.Port = port;
        var knowing = Person(host, "inh-000002", new Position(thing.Position.X + 1, 0));
        var stranger = Person(host, "inh-000003", new Position(thing.Position.X + 1, 1));
        knowing.Beliefs.Set(typeId, "container", true, 0.8, KnowledgeSources.Seen, null, host.World.Tick);
        foreach (var who in new[] { knowing, stranger })
        {
            who.Activity = new Activity(Content.Knowledge.Looking!.Action, thing.Id, thing.Position, 600);
            who.Planned = new Activity(Content.Inventions.Build.Settings.UseAction, thing.Id, thing.Position, null);
        }

        Speaker Ask(Inhabitant who)
        {
            Assert.True(host.Decisions.Submit(new PlayerCommand(ConversationSystem.SendText, who.Id) { Text = "Was machst du?" }).Accepted);
            host.World.Step();
            var json = port.Requests[^1];
            Assert.Empty(ContractValidator.Validate("conversation-request", json));
            var speaker = ContractSerializer.Deserialize<ConversationRequest>(json).Speaker;
            Assert.Equal(who.Id, speaker.Id);
            Assert.Contains(typeId, speaker.Sees!);
            Assert.Equal((SubjectKind.Type, typeId), (speaker.Doing!.Target!.Kind, speaker.Doing.Target.Id));
            Assert.Equal((SubjectKind.Type, typeId), (speaker.Doing.Planned!.Target!.Kind, speaker.Doing.Planned.Target.Id));
            return speaker;
        }

        var knows = Ask(knowing);
        var seen = Assert.Single(knows.SeesLabels!);
        Assert.Equal((typeId, name.De, name.En), (seen.Type, seen.Label.De, seen.Label.En));
        Assert.Equal(name.En, knows.Doing!.Label!.En);
        Assert.Equal(name.En, knows.Doing.Planned!.Label!.En);
        Assert.Equal(name.En, knows.Knowledge!.Single(k => k.Subject.Id == typeId).Label!.En);
        Assert.All(knows.Knowledge!.Where(k => k.Subject.Id != typeId), k => Assert.Null(k.Label));

        var unknowing = Ask(stranger);
        Assert.Null(unknowing.SeesLabels);
        Assert.Null(unknowing.Doing!.Label);
        Assert.Null(unknowing.Doing.Planned!.Label);
        Assert.DoesNotContain("\"label\"", port.Requests[^1].Replace("\"memory\"", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void TheQuestionJobNamesTheInventionItAsksAboutAndNeverAThingTheDataName()
    {
        var (host, _, jobs, typeId, thing) = WorldWithAnInvention(54);
        var name = host.Inventions.Invention(typeId)!.Name;
        var looker = Person(host, "inh-000002", new Position(thing.Position.X + 0.5, 0));
        var other = Person(host, "inh-000003", new Position(300, 0));
        var stone = host.Objects.Spawn("flint", new Position(300.5, 0));
        host.World.Step();
        Assert.Contains(host.Decisions.Open, q => q.InhabitantId == looker.Id && q.Subject == "type:" + typeId);
        Assert.Contains(host.Decisions.Open, q => q.InhabitantId == other.Id && q.Subject == "type:" + stone.TypeId);

        // Both have looked at their thing since (a question job needs something to guess from).
        looker.Beliefs.Set(typeId, "container", true, 0.8, KnowledgeSources.Seen, null, host.World.Tick);
        other.Beliefs.Set(stone.TypeId, "stone", true, 0.8, KnowledgeSources.Seen, null, host.World.Tick);

        // Busy with nothing until the night, so the questions stay as they are.
        looker.Activity = new Activity("idle", null, looker.Position, 1_000_000);
        other.Activity = new Activity("idle", null, other.Position, 1_000_000);
        var limit = host.World.Tick + (2 * host.World.Calendar.TicksPerDay);
        while (jobs.Submitted.Count(j => j.Kind == JobKind.Question && (j.Inhabitant == looker.Id || j.Inhabitant == other.Id)) < 2)
        {
            Assert.True(host.World.Tick < limit, "no question jobs within two days");
            host.World.Step();
        }

        var about = jobs.Submitted.Single(j => j.Kind == JobKind.Question && j.Inhabitant == looker.Id);
        var payload = about.Payload.Question!;
        Assert.Equal(typeId, payload.Subject.Id);
        Assert.Equal((name.De, name.En), (payload.Label!.De, payload.Label.En));
        Assert.Equal(name.En, payload.Inhabitant.Knowledge.First(k => k.Subject.Id == typeId).Label!.En);
        Assert.Empty(ContractValidator.Validate("generation-job", jobs.Json[about.RequestId]));

        var plain = jobs.Submitted.Single(j => j.Kind == JobKind.Question && j.Inhabitant == other.Id);
        Assert.Equal(stone.TypeId, plain.Payload.Question!.Subject.Id);
        Assert.Null(plain.Payload.Question.Label);
        Assert.All(plain.Payload.Question.Inhabitant.Knowledge, k => Assert.Null(k.Label));
    }

    [Fact]
    public void TheInventionJobNamesAnEarlierInventionAmongTheMaterialsForWhoKnowsIt()
    {
        foreach (var knows in new[] { true, false })
        {
            var (host, _, jobs, typeId, thing) = WorldWithAnInvention(55);
            var name = host.Inventions.Invention(typeId)!.Name;

            // A second inhabitant with the same lasting lack next to the thing: its wish lists what it sees.
            var second = Person(host, "inh-000002", new Position(thing.Position.X + 1, 0));
            second.Needs[Need] = 0.0;

            // Busy with nothing, so what it believes about the thing is what the test gives it.
            second.Activity = new Activity("idle", null, second.Position, 1_000_000);
            if (knows)
            {
                second.Beliefs.Set(typeId, "crafted", true, 0.5, KnowledgeSources.Heard, "inh-000001", host.World.Tick);
            }

            var trigger = Content.Inventions.Settings.Trigger;
            var limit = host.World.Tick + ((trigger.HoursBelow + 2) * host.World.Calendar.Settings.TicksPerHour);
            GenerationJob? wish = null;
            while (wish is null && host.World.Tick < limit)
            {
                host.World.Step();
                wish = jobs.Submitted.FirstOrDefault(j => j.Kind == JobKind.Invention && j.Inhabitant == second.Id);
            }

            Assert.NotNull(wish);
            Assert.Empty(ContractValidator.Validate("generation-job", jobs.Json[wish.RequestId]));
            var material = wish.Payload.Invention!.Materials.Single(m => m.Type == typeId);
            if (knows)
            {
                Assert.Equal((name.De, name.En), (material.Label!.De, material.Label.En));
                Assert.Contains("crafted", material.Observed);
            }
            else
            {
                Assert.Null(material.Label);
                Assert.Empty(material.Observed);
            }

            Assert.All(wish.Payload.Invention.Materials.Where(m => m.Type != typeId), m => Assert.Null(m.Label));
            Assert.Contains(wish.Payload.Invention.Materials, m => m.Type != typeId);
        }
    }

    // ------------------------------------------------------------------ helpers (test data only)

    /// <summary>The first request the decider got from an inhabitant.</summary>
    private static DecisionRequest Request(RecordingPort port, string inhabitantId) =>
        port.Requests.First(r => r.Inhabitant.Id == inhabitantId);

    /// <summary>
    /// A world in which inh-000001 has invented something (lasting lack, wish, approved invention; not built)
    /// and one thing of that type stands nearby. The decider never answers; the building plan is not answered.
    /// </summary>
    private static (SimulationHost Host, RecordingPort Decisions, ScriptedJobPort Jobs, string TypeId, WorldObject Thing) WorldWithAnInvention(ulong seed)
    {
        var decisions = new RecordingPort();
        var jobs = new ScriptedJobPort();
        var host = SimulationHost.CreateEmpty(ContentDirectory, seed, decisions, "de", jobs);
        var inventor = Person(host, "inh-000001", new Position(0, 0));
        inventor.Needs[Need] = 0.0;
        for (var i = 0; i < NearbyTypes.Length; i++)
        {
            host.Objects.Spawn(NearbyTypes[i], new Position(1 + i, 0));
        }

        var limit = (Content.Inventions.Settings.Trigger.HoursBelow + 2) * host.World.Calendar.Settings.TicksPerHour;
        for (var i = 0; i < limit && !jobs.Submitted.Any(j => j.Kind == JobKind.Invention); i++)
        {
            host.World.Step();
        }

        var job = Assert.Single(jobs.Submitted, j => j.Kind == JobKind.Invention);
        jobs.Answer(job, Approved(job));
        host.World.Step();
        var typeId = InventionSystem.TypeIdFor(job.RequestId);
        Assert.True(host.Types.Contains(typeId));
        var thing = host.Objects.Spawn(typeId, new Position(40, 0));
        return (host, decisions, jobs, typeId, thing);
    }

    private static Inhabitant Person(SimulationHost host, string id, Position position)
    {
        var model = Content.InhabitantModel;
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
        return inhabitant;
    }

    private static JobStatus Approved(GenerationJob job)
    {
        var example = File.ReadAllText(Path.Combine(TestData.Root, "contracts", "examples", "valid", "job-status.invention-approved.json"));
        var status = ContractSerializer.Deserialize<JobStatus>(example);
        status.JobId = job.RequestId;
        status.Tick = job.Tick;
        var invention = status.Result!.Invention!.Invention!;
        invention.Materials = [NearbyTypes[0]];
        foreach (var step in invention.Steps)
        {
            step.Materials = [NearbyTypes[^1]];
        }

        invention.Introduces = ["container"];
        invention.TechLevel = Content.Inventions.Settings.TechLevel;
        invention.SizeM.X = invention.SizeM.Y = invention.SizeM.Z = Content.Inventions.Settings.Limits.MaxSizeM / 2;
        status.Result.Invention.Sound!.Needed = false;
        return status;
    }

    /// <summary>Keeps every request and never answers.</summary>
    private sealed class RecordingPort : IDecisionPort
    {
        public List<DecisionRequest> Requests { get; } = [];

        public Dictionary<string, string> Json { get; } = new(StringComparer.Ordinal);

        public void Send(string requestId, string requestJson)
        {
            Json[requestId] = requestJson;
            Requests.Add(ContractSerializer.Deserialize<DecisionRequest>(requestJson));
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }

    /// <summary>Keeps the conversation requests and never answers.</summary>
    private sealed class CapturePort : IConversationPort
    {
        public List<string> Requests { get; } = [];

        public void Send(string requestId, string requestJson) => Requests.Add(requestJson);

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }

    /// <summary>Records submitted jobs; outcomes are handed in by the test.</summary>
    private sealed class ScriptedJobPort : IJobPort
    {
        private readonly Queue<PortMessage> _ready = new();

        public List<GenerationJob> Submitted { get; } = [];

        public Dictionary<string, string> Json { get; } = new(StringComparer.Ordinal);

        public void Submit(string requestId, string jobJson)
        {
            Json[requestId] = jobJson;
            Submitted.Add(ContractSerializer.Deserialize<GenerationJob>(jobJson));
        }

        public void Answer(GenerationJob job, JobStatus status) => _ready.Enqueue(new PortMessage(job.RequestId, ContractSerializer.Serialize(status), IsError: false));

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            var ready = _ready.ToList();
            _ready.Clear();
            return ready;
        }

        public void SetPhase(string phase, long tick, bool force)
        {
        }
    }
}
