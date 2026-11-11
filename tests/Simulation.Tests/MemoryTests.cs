using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Player;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B4a R5, the store: what an inhabitant remembers comes from the core's events by data (which events,
/// under which kind, formative or not), is capped by data, and travels in the requests to the AI. Every
/// thing and action here is test data from the start catalogue.
/// </summary>
public sealed class MemoryTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);
    private static readonly KnowledgeMemory Settings = Content.Knowledge.Memory!;

    [Fact]
    public void AFinishedActionIsRememberedAboutTheTypeOfItsThingAndIsFormativeOnlyTheFirstTime()
    {
        var (world, objects, inhabitants) = NewWorld(Settings);
        var a = inhabitants.Get("inh-000001");

        // The apple is used up by eating: the memory still knows what it was.
        Do(world, a, "eat", objects.Spawn("apple", new Position(1, 0)));
        var first = Assert.Single(a.Memory.Events);
        Assert.Equal(("eat", InhabitantsSystem.AboutType, "apple", true, false), (first.Kind, first.AboutKind, first.AboutId, first.Formative, first.Condensed));
        Assert.Equal(1, first.Seq);

        Do(world, a, "eat", objects.Spawn("apple", new Position(1, 0)));
        Assert.Equal(2, a.Memory.Events.Count);
        Assert.False(a.Memory.Events[1].Formative);
        Assert.True(a.Memory.Events[1].Tick > first.Tick);

        // Another thing is a first time again.
        Do(world, a, "eat", objects.Spawn("blue_berry", new Position(1, 0)));
        Assert.True(a.Memory.Events[2].Formative);
        Assert.Empty(inhabitants.Get("inh-000002").Memory.Events);
    }

    [Fact]
    public void WhatHurtIsRememberedAsFeltAndIsFormativeEveryTime()
    {
        var (world, objects, inhabitants) = NewWorld(Settings);
        var a = inhabitants.Get("inh-000001");

        // Red berries lower the will to live, sea water makes thirstier (content/effects): both by an effect
        // entry of the data, never by the property behind it.
        Do(world, a, "eat", objects.Spawn("red_berry", new Position(1, 0)));
        Do(world, a, "eat", objects.Spawn("red_berry", new Position(1, 0)));
        Do(world, a, "drink", objects.Spawn("sea_water", new Position(1, 0)));

        Assert.Equal(
            [("eat", "red_berry", true), ("felt_bad", "red_berry", true), ("eat", "red_berry", false), ("felt_bad", "red_berry", true), ("drink", "sea_water", true), ("felt_bad", "sea_water", true)],
            a.Memory.Events.Select(e => (e.Kind, e.AboutId!, e.Formative)));

        // What did no harm leaves no such memory.
        Do(world, a, "drink", objects.Spawn("spring_water", new Position(1, 0)));
        Assert.Equal(("drink", "spring_water"), (a.Memory.Events[^1].Kind, a.Memory.Events[^1].AboutId));
        Assert.Equal(3, a.Memory.Events.Count(e => e.Kind == "felt_bad"));
    }

    [Fact]
    public void LeftOutActionsActionsWithoutATargetAndUnlistedEventsAreNotRemembered()
    {
        var (world, _, inhabitants) = NewWorld(Settings);
        var a = inhabitants.Get("inh-000001");
        var everyday = Settings.Events.First(e => e.ActionAsKind == true);
        Assert.True(everyday.OnlyWithSubject);
        Assert.Contains("approach", everyday.LeftOutActions!);

        // Looking around or resting is about nothing; walking up to someone is left out by name.
        Do(world, a, "idle", null);
        Do(world, a, "inspect", null);
        inhabitants.Report(new WorldEvent(InhabitantsSystem.ActionFinished, world.Tick, a.Id, "approach", "inh-000003"));
        inhabitants.Report(new WorldEvent("question_opened", world.Tick, a.Id, null, "q-000001"));
        inhabitants.Report(new WorldEvent(PlayerSystem.GiftPlaced, world.Tick, string.Empty, null, "obj-000001") { Detail = "apple" });

        Assert.Empty(a.Memory.Events);
        Assert.False(a.Memory.HasState);
    }

    [Fact]
    public void AFirstMeetingIsRememberedByBothAndANameOnlyByTheLearner()
    {
        var (world, _, inhabitants) = NewWorld(Settings);
        var a = inhabitants.Get("inh-000001");
        var c = inhabitants.Get("inh-000003");

        // The core reports a first meeting once per pair (actor = the lower id).
        inhabitants.Report(new WorldEvent(InhabitantsSystem.FirstMeeting, world.Tick, a.Id, null, c.Id));
        inhabitants.Report(new WorldEvent(InhabitantsSystem.NameLearned, world.Tick, a.Id, "introduce", c.Id));

        Assert.Equal(
            [(InhabitantsSystem.FirstMeeting, InhabitantsSystem.AboutInhabitant, c.Id, true), (InhabitantsSystem.NameLearned, InhabitantsSystem.AboutInhabitant, c.Id, true)],
            a.Memory.Events.Select(e => (e.Kind, e.AboutKind!, e.AboutId!, e.Formative)));
        var met = Assert.Single(c.Memory.Events);
        Assert.Equal((InhabitantsSystem.FirstMeeting, InhabitantsSystem.AboutInhabitant, a.Id, true), (met.Kind, met.AboutKind, met.AboutId, met.Formative));
    }

    [Fact]
    public void BeingToldSomethingIsRememberedAboutTheTypeAndAnEventWithoutATargetAboutNothing()
    {
        var (world, _, inhabitants) = NewWorld(Settings);
        var a = inhabitants.Get("inh-000001");

        // knowledge_gained names the type itself; only the heard kind is in the data.
        inhabitants.Report(new WorldEvent(InhabitantsSystem.KnowledgeGained, world.Tick, a.Id, "warn", "red_berry") { Detail = "poisonous/" + KnowledgeSources.Heard });
        inhabitants.Report(new WorldEvent(InhabitantsSystem.KnowledgeGained, world.Tick, a.Id, "eat", "apple") { Detail = "edible/" + KnowledgeSources.Experienced });
        inhabitants.Report(new WorldEvent(PlayerSystem.HelpNeeded, world.Tick, a.Id, null, null) { Detail = "thirst" });
        inhabitants.Report(new WorldEvent(PlayerSystem.HelpNeeded, world.Tick + 1, a.Id, null, null) { Detail = "thirst" });

        Assert.Equal(
            [("was_told", "red_berry", true), (PlayerSystem.HelpNeeded, null, true), (PlayerSystem.HelpNeeded, null, false)],
            a.Memory.Events.Select(e => (e.Kind, e.AboutId, e.Formative)));
    }

    [Fact]
    public void WhichEventsAreRememberedAndFormativeIsData()
    {
        // The same world with other data: nothing in the code names an event, an action or an effect.
        var data = ContractSerializer.Deserialize<KnowledgeSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "knowledge", "settings.json"))).Memory!;
        data.Events =
        [
            new KnowledgeMemoryEvent { Event = InhabitantsSystem.ActionFinished, Kind = "did_something", Formative = KnowledgeMemoryEventFormative.Never },
            new KnowledgeMemoryEvent { Event = InhabitantsSystem.ActionFinished, Kind = "enjoyed", Formative = KnowledgeMemoryEventFormative.Always, Effect = new KnowledgeMemoryEffect { Kind = KnowledgeMemoryEffectKind.Mood, Id = "joy", DeltaAtLeast = 0.01 } },

            // Two entries that fit one event with the same kind give one memory, the more formative one.
            new KnowledgeMemoryEvent { Event = InhabitantsSystem.ActionFinished, Kind = "did_something", Formative = KnowledgeMemoryEventFormative.First, LeftOutActions = ["drink"] },
            new KnowledgeMemoryEvent { Event = "question_opened", Formative = KnowledgeMemoryEventFormative.Always },
        ];
        var (world, objects, inhabitants) = NewWorld(data);
        var a = inhabitants.Get("inh-000001");

        Do(world, a, "eat", objects.Spawn("apple", new Position(1, 0)));
        Do(world, a, "eat", objects.Spawn("red_berry", new Position(1, 0)));
        Do(world, a, "drink", objects.Spawn("spring_water", new Position(1, 0)));
        inhabitants.Report(new WorldEvent("question_opened", world.Tick, a.Id, null, "q-000001"));

        Assert.Equal(
            [("did_something", "apple", true), ("enjoyed", "apple", true), ("did_something", "red_berry", true), ("did_something", "spring_water", false), ("question_opened", null, true)],
            a.Memory.Events.Select(e => (e.Kind, e.AboutId, e.Formative)));
    }

    [Fact]
    public void WithoutMemoryDataNothingIsRememberedAndTheHashStaysAsItWas()
    {
        var (world, objects, inhabitants) = NewWorld(null);
        var a = inhabitants.Get("inh-000001");
        Do(world, a, "eat", objects.Spawn("apple", new Position(1, 0)));

        Assert.False(a.Memory.HasState);
        var before = world.StateHash();
        a.Memory.Remember(world.Tick, "eat", InhabitantsSystem.AboutType, "apple", formative: false, max: 4);
        Assert.NotEqual(before, world.StateHash());
    }

    [Fact]
    public void BeyondTheCapTheOldestEverydayEventIsForgottenFirstThenWhatTheSummaryHolds()
    {
        var memory = new InhabitantMemory();
        void Add(string kind, bool formative) => memory.Remember(memory.LastSeq + 10, kind, null, null, formative, max: 4);

        Add("f1", true);
        Add("n1", false);
        Add("n2", false);
        Add("f2", true);
        Add("n3", false);
        Assert.Equal(["f1", "n2", "f2", "n3"], memory.Events.Select(e => e.Kind));

        Add("f3", true);
        Add("f4", true);
        Assert.Equal(["f1", "f2", "f3", "f4"], memory.Events.Select(e => e.Kind));

        // All formative: one the summary already holds goes before one it does not; then the oldest.
        memory.Condense("Summary.", new HashSet<long> { memory.Events[1].Seq }, new HashSet<long> { memory.Events[1].Tick });
        Assert.True(memory.Events[1].Condensed);
        Add("f5", true);
        Assert.Equal(["f1", "f3", "f4", "f5"], memory.Events.Select(e => e.Kind));
        Add("f6", true);
        Assert.Equal(["f3", "f4", "f5", "f6"], memory.Events.Select(e => e.Kind));

        // Running numbers are never reused, and what was a first time stays one that happened.
        Assert.Equal(9, memory.LastSeq);
        Assert.True(memory.First("eat", "type", "apple"));
        Assert.False(memory.First("eat", "type", "apple"));
        Assert.True(memory.First("eat", "type", "red_berry"));
    }

    [Fact]
    public void WithoutAnAiServiceEventsPileUpToTheCapAndNothingElseHappens()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 7, MockDecisionPort.FromContent(ContentDirectory), "de");
        host.World.Run(3 * host.World.Calendar.TicksPerDay);

        Assert.Contains(host.Inhabitants.All, i => i.Memory.Events.Count > 0);
        Assert.All(host.Inhabitants.All, i =>
        {
            Assert.InRange(i.Memory.Events.Count, 0, Settings.MaxEvents);
            Assert.Empty(i.Memory.Summary);
            Assert.DoesNotContain(i.Memory.Events, e => e.Condensed);
        });
        Assert.False(host.NightJobs.HasState);
        Assert.Empty(host.NightJobs.MemoryJobs);
        Assert.Empty(host.Inventions.Log.Entries);

        // Far beyond the cap: the newest everyday events and the formative ones stay.
        var a = host.Inhabitants.All.First();
        var formative = a.Memory.Events.Count(e => e.Formative);
        for (var i = 0; i < Settings.MaxEvents * 3; i++)
        {
            a.Memory.Remember(host.World.Tick + i, "drink", InhabitantsSystem.AboutType, "spring_water", formative: false, Settings.MaxEvents);
        }

        Assert.Equal(Settings.MaxEvents, a.Memory.Events.Count);
        Assert.Equal(Math.Min(formative, Settings.MaxEvents), a.Memory.Events.Count(e => e.Formative));
        host.World.Run(host.World.Calendar.TicksPerDay);
        Assert.False(host.NightJobs.HasState);
    }

    [Fact]
    public void TheDecisionRequestCarriesTheSummaryAndTheNewestEventsWithinTheContractLimit()
    {
        var port = new SilentDecisionPort();
        var host = SimulationHost.CreateEmpty(ContentDirectory, 5, port, "de");
        var a = Person(host, "inh-000001", new Position(0, 0));
        host.Objects.Spawn("flint", new Position(1, 0));
        var events = Enumerable.Range(1, 20).Select(i => new MemoryEntry(i, i * 10, i % 2 == 0 ? "drink" : "first_meeting", i % 2 == 0 ? InhabitantsSystem.AboutType : InhabitantsSystem.AboutInhabitant, i % 2 == 0 ? "spring_water" : "inh-000002", i % 2 != 0)).ToList();
        a.Memory.Restore("Woke up on a beach. Found fresh water.", 20, events, []);

        host.World.Step();

        var request = ContractSerializer.Deserialize<DecisionRequest>(Assert.Single(port.Requests));
        var memory = request.Inhabitant.Memory;
        Assert.Equal("Woke up on a beach. Found fresh water.", memory.Summary);
        Assert.Equal(InhabitantStates.MaxRecent, memory.Recent.Count);
        Assert.Equal(events.TakeLast(InhabitantStates.MaxRecent).Select(e => (e.Tick, e.Kind, e.AboutId)), memory.Recent.Select(e => (e.Tick, e.Kind, e.About?.Id)));
        Assert.Equal(SubjectKind.Inhabitant, memory.Recent.First().About!.Kind);
        Assert.Equal(SubjectKind.Type, memory.Recent.Last().About!.Kind);

        // An event about nothing has no subject; an empty memory is still a valid one.
        a.Memory.Restore(string.Empty, 21, [new MemoryEntry(21, 300, "help_needed", null, null, true)], []);
        Assert.Null(Assert.Single(InhabitantStates.Of(a).Memory.Recent).About);
        Assert.Empty(InhabitantStates.Of(Person(host, "inh-000002", new Position(200, 0))).Memory.Recent);
    }

    [Fact]
    public void TheConversationRequestCarriesTheSpeakersMemory()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 1, new MockDecisionPort(), "de");
        var port = new CaptureConversationPort();
        host.Conversation.Port = port;
        var a = host.Inhabitants.All.First();
        a.Memory.Restore("Met another person.", 1, [new MemoryEntry(1, 0, "first_meeting", InhabitantsSystem.AboutInhabitant, "inh-000002", true)], []);

        Assert.True(host.Decisions.Submit(new PlayerCommand(ConversationSystem.SendText, a.Id) { Text = "Hallo" }).Accepted);
        host.World.Run(2);

        var json = Assert.Single(port.Requests);
        Assert.Empty(ContractValidator.Validate("conversation-request", json));
        var memory = ContractSerializer.Deserialize<ConversationRequest>(json).Speaker.Memory!;
        Assert.Equal("Met another person.", memory.Summary);
        Assert.Contains(memory.Recent, e => e.Kind == "first_meeting" && e.About!.Id == "inh-000002");
    }

    [Fact]
    public void MemoryDataThatDoNotFitAreRefusedAtLoad()
    {
        Assert.Contains("job_max_formative", Refused(m => m.JobMaxEvents = m.JobMaxFormative - 1));
        Assert.Contains("max_events", Refused(m => m.MaxEvents = m.CondenseAt - 1));
        Assert.Contains("not_an_event", Refused(m => m.Events.First().Event = "not_an_event"));
        Assert.Contains("not_an_action", Refused(m => m.Events.First().LeftOutActions = ["not_an_action"]));
        Assert.Contains("action_as_kind", Refused(m => m.Events.First().Kind = "did"));
        Assert.Contains("not_a_need", Refused(m => m.Events.First(e => e.Effect?.Kind == KnowledgeMemoryEffectKind.Need).Effect!.Id = "not_a_need"));
        Assert.Contains("delta_at_most", Refused(m => m.Events.First(e => e.Effect is not null).Effect!.DeltaAtMost = null));
        Assert.Contains("unknown_object", Refused(s => s.NightQuestions!.Questions.Add(new KnowledgeNightQuestion { Trigger = KnowledgeNightQuestionTrigger.Unknown_object, Question = KnowledgeNightQuestionQuestion.Why })));
    }

    private static string Refused(Action<KnowledgeMemory> change) => Refused(s => change(s.Memory!));

    /// <summary>Loads a copy of the content with changed knowledge data and returns the refusal.</summary>
    private static string Refused(Action<KnowledgeSettings> change)
    {
        var copy = Path.Combine(Path.GetTempPath(), "gw-memory-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var file in Directory.GetFiles(ContentDirectory, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(copy, Path.GetRelativePath(ContentDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }

            var path = Path.Combine(copy, "knowledge", "settings.json");
            var settings = ContractSerializer.Deserialize<KnowledgeSettings>(File.ReadAllText(path));
            change(settings);
            File.WriteAllText(path, ContractSerializer.Serialize(settings));
            return Assert.Throws<InvalidDataException>(() => ContentSet.Load(copy)).Message;
        }
        finally
        {
            if (Directory.Exists(copy))
            {
                Directory.Delete(copy, recursive: true);
            }
        }
    }

    /// <summary>Lets the inhabitant do one action to its end.</summary>
    private static void Do(World world, Inhabitant inhabitant, string action, WorldObject? target)
    {
        inhabitant.Activity = new Activity(action, target?.Id, target?.Position ?? inhabitant.Position, null);
        for (var t = 0; t < 2_000 && inhabitant.Activity is { } running && running.Action == action && running.TargetId == target?.Id; t++)
        {
            world.Step();
        }

        // Until told otherwise it does nothing that would be remembered.
        inhabitant.Activity = new Activity("idle", null, inhabitant.Position, 100_000);
    }

    /// <summary>The start inhabitants far apart and idle, with a recorder on the given data.</summary>
    private static (World World, WorldObjects Objects, InhabitantsSystem Inhabitants) NewWorld(KnowledgeMemory? memory)
    {
        var world = new World(3, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            EventKinds = Content.EventKinds,
        };
        world.Add(objects).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [new(0, 0), new(300, 0), new(0, 300), new(300, 300)]);
        foreach (var inhabitant in inhabitants.All)
        {
            inhabitant.Activity = new Activity("idle", null, inhabitant.Position, 100_000);
        }

        inhabitants.Reported += new MemoryRecorder(memory, inhabitants, objects, Content.Actions).Observe;
        return (world, objects, inhabitants);
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

    /// <summary>Keeps the requests and never answers.</summary>
    private sealed class SilentDecisionPort : IDecisionPort
    {
        public List<string> Requests { get; } = [];

        public void Send(string requestId, string requestJson) => Requests.Add(requestJson);

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }

    /// <summary>Keeps the requests and never answers.</summary>
    private sealed class CaptureConversationPort : IConversationPort
    {
        public List<string> Requests { get; } = [];

        public void Send(string requestId, string requestJson) => Requests.Add(requestJson);

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
