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
        Assert.Equal(ActionOutcomes.Done, first.Outcome);
        Assert.Equal(1, first.Seq);

        // Once the first time is in the summary, the next time is an everyday event of its own.
        a.Memory.Condense("Ate an apple.", new HashSet<long> { first.Seq }, new HashSet<long> { first.Tick });
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
    public void ARepeatedEverydayEventIsCountedOnItsOpenEntryInsteadOfAdded()
    {
        // GW2 #227: a day of drinking must not crowd out what matters (data: repeat counted).
        var (world, objects, inhabitants) = NewWorld(Settings);
        var a = inhabitants.Get("inh-000001");
        Assert.Equal(KnowledgeMemoryEventRepeat.Counted, Settings.Events.First(e => e.ActionAsKind == true).Repeat);

        Do(world, a, "eat", objects.Spawn("apple", new Position(1, 0)));
        var firstTick = a.Memory.Events[0].Tick;
        Do(world, a, "drink", objects.Spawn("spring_water", new Position(1, 0)));
        Do(world, a, "eat", objects.Spawn("apple", new Position(1, 0)));
        Do(world, a, "eat", objects.Spawn("apple", new Position(1, 0)));
        Do(world, a, "drink", objects.Spawn("spring_water", new Position(1, 0)));

        // One entry per action and thing: how often, when last (it is the newest event then), since when;
        // the first time stays formative. Running numbers are never reused.
        Assert.Equal([("eat", "apple", 3, true), ("drink", "spring_water", 2, true)], a.Memory.Events.Select(e => (e.Kind, e.AboutId!, e.Times, e.Formative)));
        var eat = a.Memory.Events[0];
        Assert.Equal(firstTick, eat.Since);
        Assert.True(eat.Tick > firstTick);
        Assert.True(a.Memory.Events[1].Tick > eat.Tick);
        Assert.Equal(5, a.Memory.LastSeq);
        Assert.Equal([4L, 5L], a.Memory.Events.Select(e => e.Seq));

        // Another thing is an entry of its own.
        Do(world, a, "eat", objects.Spawn("blue_berry", new Position(1, 0)));
        Assert.Equal(3, a.Memory.Events.Count);

        // What has gone out to a condensing stays what it was: a repetition starts a new entry, not formative.
        var before = a.Memory.Events.ToList();
        a.Memory.Seal(a.Memory.LastSeq);
        Do(world, a, "eat", objects.Spawn("apple", new Position(1, 0)));
        Do(world, a, "eat", objects.Spawn("apple", new Position(1, 0)));
        Assert.Equal(before, a.Memory.Events.Take(3));
        var again = Assert.Single(a.Memory.Events.Skip(3));
        Assert.Equal(("eat", "apple", 2, false), (again.Kind, again.AboutId, again.Times, again.Formative));

        // The requests say how often (absent for once).
        var recent = InhabitantStates.Of(a).Memory.Recent.ToList();
        Assert.Equal([3, 2, null, 2], recent.Select(e => e.Times));
        Assert.Equal(a.Memory.Events.Select(e => e.Tick), recent.Select(e => e.Tick));
    }

    [Fact]
    public void AnAttemptIsNotRememberedAsADeed()
    {
        // GW2 #227: 'drink flint' was stored like a real drink ('Drank flint'). The event carries what came
        // of the action, by the measure the repetition guard uses (data: with_outcome).
        var (world, objects, inhabitants) = NewWorld(Settings);
        var a = inhabitants.Get("inh-000001");
        var flint = objects.Spawn("flint", new Position(1, 0));
        var spring = objects.Spawn("spring_water", new Position(1, 0));
        var everyday = Settings.Events.First(e => e.ActionAsKind == true);
        Assert.True(everyday.WithOutcome);
        Assert.Equal([ActionOutcomes.Nothing], everyday.EverydayOutcomes);

        // The first try changes nothing, but it finds out (not drinkable): a first time worth remembering.
        // Trying again brings nothing at all: an everyday event of its own, counted from then on.
        Do(world, a, "drink", flint);
        Do(world, a, "drink", flint);
        Do(world, a, "drink", flint);
        Assert.Equal(
            [("drink", "flint", ActionOutcomes.FoundOut, true, 1), ("drink", "flint", ActionOutcomes.Nothing, false, 2)],
            a.Memory.Events.Select(e => (e.Kind, e.AboutId!, e.Outcome!, e.Formative, e.Times)));

        // A real drink is a deed - and drinking the same water without being thirsty is not counted with it.
        Do(world, a, "drink", spring);
        Do(world, a, "drink", spring);
        Do(world, a, "drink", spring, satisfied: true);
        Assert.Equal(
            [("spring_water", ActionOutcomes.Done, true, 2), ("spring_water", ActionOutcomes.Nothing, false, 1)],
            a.Memory.Events.Skip(2).Select(e => (e.AboutId!, e.Outcome!, e.Formative, e.Times)));

        // What it felt like stays an event of its own without an outcome; so does everything that is no action.
        Do(world, a, "eat", objects.Spawn("red_berry", new Position(1, 0)));
        inhabitants.Report(new WorldEvent(InhabitantsSystem.FirstMeeting, world.Tick, a.Id, null, "inh-000003"));
        Assert.Equal(
            [("eat", ActionOutcomes.Done), ("felt_bad", null), (InhabitantsSystem.FirstMeeting, null)],
            a.Memory.Events.Skip(4).Select(e => (e.Kind, e.Outcome)));

        // The requests carry it as the contract's ids.
        var recent = InhabitantStates.Of(a).Memory.Recent.ToList();
        Assert.Equal(
            [MemoryOutcome.Found_out, MemoryOutcome.Nothing, MemoryOutcome.Done, MemoryOutcome.Nothing, MemoryOutcome.Done, null, null],
            recent.Select(e => e.Outcome));
    }

    [Fact]
    public void WhetherTheOutcomeIsRememberedAndWhichOneIsEverydayIsData()
    {
        var data = ContractSerializer.Deserialize<KnowledgeSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "knowledge", "settings.json"))).Memory!;
        var entry = data.Events.First(e => e.ActionAsKind == true);

        // Without everyday outcomes the first attempt without any effect is a first time as well.
        entry.EverydayOutcomes = null;
        var (world, objects, inhabitants) = NewWorld(data);
        var a = inhabitants.Get("inh-000001");
        var flint = objects.Spawn("flint", new Position(1, 0));
        Do(world, a, "drink", flint);
        Do(world, a, "drink", flint);
        Assert.Equal([(ActionOutcomes.FoundOut, true), (ActionOutcomes.Nothing, true)], a.Memory.Events.Select(e => (e.Outcome!, e.Formative)));

        // Without with_outcome the event is the action alone, as before: attempt and deed are one entry.
        entry.WithOutcome = null;
        (world, objects, inhabitants) = NewWorld(data);
        a = inhabitants.Get("inh-000001");
        flint = objects.Spawn("flint", new Position(1, 0));
        Do(world, a, "drink", flint);
        Do(world, a, "drink", flint);
        var only = Assert.Single(a.Memory.Events);
        Assert.Null(only.Outcome);
        Assert.Equal((2, true), (only.Times, only.Formative));
    }

    [Fact]
    public void WhatHappenedEveryTimeItHappensIsNeverCounted()
    {
        // felt_bad and the other always-formative kinds stay entries of their own (data: repeat add).
        var memory = new InhabitantMemory();
        memory.Remember(10, "felt_bad", "type", "red_berry", formative: true, max: 8);
        memory.Remember(20, "felt_bad", "type", "red_berry", formative: true, max: 8);
        memory.Remember(30, "drink", "type", "spring_water", formative: false, max: 8, count: true);
        memory.Remember(40, "drink", "type", "sea_water", formative: false, max: 8, count: true);
        memory.Remember(50, "drink", "type", "spring_water", formative: false, max: 8, count: true);

        Assert.Equal(
            [("felt_bad", "red_berry", 1, 10L), ("felt_bad", "red_berry", 1, 20L), ("drink", "sea_water", 1, 40L), ("drink", "spring_water", 2, 50L)],
            memory.Events.Select(e => (e.Kind, e.AboutId!, e.Times, e.Tick)));
        Assert.All(Settings.Events.Where(e => e.Formative == KnowledgeMemoryEventFormative.Always), e => Assert.NotEqual(KnowledgeMemoryEventRepeat.Counted, e.Repeat));

        // Counting changes the hash, and an entry the summary holds is not counted on.
        static string Hash(InhabitantMemory m)
        {
            using var hasher = new State.StateHasher();
            m.WriteState(hasher);
            return hasher.Finish();
        }

        var before = Hash(memory);
        memory.Condense("Summary.", new HashSet<long> { memory.Events[3].Seq }, new HashSet<long> { 50 });
        memory.Remember(60, "drink", "type", "spring_water", formative: false, max: 8, count: true);
        Assert.Equal([(2, true), (1, false)], memory.Events.Where(e => e.AboutId == "spring_water").Select(e => (e.Times, e.Condensed)));
        Assert.NotEqual(before, Hash(memory));
    }

    [Fact]
    public void ALabelIsWhatTheInhabitantCallsTheSubjectNowAndOnlyWhatItKnows()
    {
        // GW2 #227: 'Learned the name inh-000004' - the event needs the name, by the inhabitant's own knowledge.
        var (world, _, inhabitants) = NewWorld(Settings);
        var a = inhabitants.Get("inh-000001");
        var c = inhabitants.Get("inh-000003");
        Assert.NotNull(c.Name);
        inhabitants.Report(new WorldEvent(InhabitantsSystem.FirstMeeting, world.Tick, a.Id, null, c.Id));
        inhabitants.Report(new WorldEvent(InhabitantsSystem.KnowledgeGained, world.Tick, a.Id, "warn", "red_berry") { Detail = "poisonous/" + KnowledgeSources.Heard });

        // It has met the other but knows no name yet: no label, for neither of them.
        Assert.All(InhabitantStates.MemoryOf(a, inhabitants.LabelOf).Recent, e => Assert.Null(e.Label));
        Assert.All(InhabitantStates.MemoryOf(c, inhabitants.LabelOf).Recent, e => Assert.Null(e.Label));

        // The name is learned after the event: the earlier event carries it from now on - for the one who
        // learned it only, and never for a thing the data name.
        a.KnownNames.Add(c.Id);
        var recent = InhabitantStates.Of(a, Content.Knowledge, [], [], inhabitants.LabelOf).Memory.Recent.ToList();
        Assert.Equal((InhabitantsSystem.FirstMeeting, c.Name, c.Name), (recent[0].Kind, recent[0].Label!.De, recent[0].Label!.En));
        Assert.Equal("was_told", recent[1].Kind);
        Assert.Null(recent[1].Label);
        Assert.All(InhabitantStates.MemoryOf(c, inhabitants.LabelOf).Recent, e => Assert.Null(e.Label));

        // Without the resolver (no world at hand) there are no labels, and the name of someone who is gone is not made up.
        Assert.All(InhabitantStates.MemoryOf(a).Recent, e => Assert.Null(e.Label));
        Assert.Null(inhabitants.LabelOf(a, new MemoryEntry(9, 0, "first_meeting", InhabitantsSystem.AboutInhabitant, "inh-000099", true)));
        a.KnownNames.Add("inh-000099");
        Assert.Null(inhabitants.LabelOf(a, new MemoryEntry(9, 0, "first_meeting", InhabitantsSystem.AboutInhabitant, "inh-000099", true)));
        Assert.Null(inhabitants.LabelOf(a, new MemoryEntry(9, 0, "help_needed", null, null, true)));
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

        // The eating itself is counted (twice); what it felt like is an event of its own every time.
        Assert.Equal(
            [("felt_bad", "red_berry", true, 1), ("eat", "red_berry", true, 2), ("felt_bad", "red_berry", true, 1), ("drink", "sea_water", true, 1), ("felt_bad", "sea_water", true, 1)],
            a.Memory.Events.Select(e => (e.Kind, e.AboutId!, e.Formative, e.Times)));

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
        Assert.Contains("not_an_outcome", Refused(m => m.Events.First().EverydayOutcomes = ["not_an_outcome"]));
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

    /// <summary>Lets the inhabitant do one action to its end; hungry and thirsty unless <paramref name="satisfied"/>, so eating and drinking do something.</summary>
    private static void Do(World world, Inhabitant inhabitant, string action, WorldObject? target, bool satisfied = false)
    {
        foreach (var need in inhabitant.Needs.Keys.ToList())
        {
            inhabitant.Needs[need] = satisfied ? 1.0 : 0.3;
        }

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
            Names = Content.InhabitantNames,
            Knowledge = Content.Knowledge,
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
