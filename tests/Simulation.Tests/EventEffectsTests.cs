using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// W1744: sources of purpose as data. A core event has effects on the inhabitant it is about (data:
/// event_effects of content/effects), and exploring and passing knowledge on carry a little purpose in
/// their action rules. Every number is read from the data, so the tests follow the proposal values.
/// </summary>
public sealed class EventEffectsTests
{
    private const string Purpose = "purpose";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);

    [Fact]
    public void LearningByTryingRaisesPurposeOnceNotOnEveryRepeat()
    {
        var (world, objects, inhabitants) = NewWorld(Content.Actions);
        var events = new List<WorldEvent>();
        inhabitants.Reported += events.Add;
        var eater = inhabitants.Get("inh-000001");
        eater.Needs[Purpose] = 0.2;
        var start = world.Tick;
        Eat(world, objects, eater);

        var learned = events.Where(e => e.Kind == InhabitantsSystem.KnowledgeGained && e.InhabitantId == eater.Id).ToList();
        Assert.Contains(learned, e => e.Detail == "edible/experienced");
        var gain = learned.Sum(e => OnPurpose(Content.Actions.EventEffects(e.Kind, e.Detail)));
        Assert.True(gain > 0, "content: learning by trying gives purpose");
        Assert.Equal(0.2 - (Decay(world) * (world.Tick - start)) + gain, eater.Needs[Purpose], 9);

        // The second berry teaches nothing new: no event, so only the decay.
        events.Clear();
        var before = eater.Needs[Purpose];
        start = world.Tick;
        Eat(world, objects, eater);
        Assert.DoesNotContain(events, e => e.Kind == InhabitantsSystem.KnowledgeGained);
        Assert.Equal(before - (Decay(world) * (world.Tick - start)), eater.Needs[Purpose], 9);
    }

    [Fact]
    public void PassingKnowledgeOnGivesTheTellerPurposeAndTheListenerForWhatItHeard()
    {
        var (world, _, inhabitants) = NewWorld(Content.Actions);
        var teller = inhabitants.Get("inh-000001");
        var listener = inhabitants.Get("inh-000002");
        teller.Beliefs.Set("red_berry", "poisonous", true, 1.0, KnowledgeSources.Experienced, null, 1);
        teller.Needs[Purpose] = listener.Needs[Purpose] = 0.2;
        listener.Activity = new Activity("idle", null, listener.Position, 100_000);
        var start = world.Tick;
        teller.Activity = new Activity("warn", listener.Id, listener.Position, null);
        RunUntil(world, () => teller.Activity is null);

        var telling = OnPurpose(Content.Actions.Applying("warn", null).SelectMany(r => r.Effects));
        var hearing = OnPurpose(Content.Actions.EventEffects(InhabitantsSystem.KnowledgeGained, "poisonous/" + KnowledgeSources.Heard));
        Assert.True(telling > 0, "content: warning someone gives the teller purpose");
        Assert.True(hearing > 0, "content: hearing something new gives purpose");
        Assert.True(listener.Beliefs.TryGet("red_berry", "poisonous", out _));
        var decay = Decay(world) * (world.Tick - start);
        Assert.Equal(0.2 - decay + telling, teller.Needs[Purpose], 9);
        Assert.Equal(0.2 - decay + hearing, listener.Needs[Purpose], 9);
    }

    [Theory]
    [InlineData("explore")]
    [InlineData("show")]
    [InlineData("teach")]
    public void TheseActionsCarryPurposeInTheirRules(string action)
    {
        var gain = OnPurpose(Content.Actions.Applying(action, null).SelectMany(r => r.Effects));
        Assert.True(gain > 0, $"content: {action} gives purpose");

        // Finishing the action applies it (the last tick of a running action; where it happens does not matter here).
        var (world, _, inhabitants) = NewWorld(Content.Actions);
        var actor = inhabitants.Get("inh-000001");
        var other = inhabitants.Get("inh-000002");
        actor.Needs[Purpose] = 0.2;
        var needsListener = Content.Vocabularies[VocabularyKind.Action].Entries.Single(e => e.Id == action).Target is not (null or VocabularyEntryTarget.None);
        actor.Activity = new Activity(action, needsListener ? other.Id : null, actor.Position, 1);
        world.Step();
        Assert.Null(actor.Activity);
        Assert.Equal(0.2 - Decay(world) + gain, actor.Needs[Purpose], 9);
    }

    [Fact]
    public void AnEventReachesOnlyItsInhabitantAndOnlyWithTheDetailOfTheData()
    {
        var (world, _, inhabitants) = NewWorld(Content.Actions);
        var all = inhabitants.All.ToList();
        foreach (var inhabitant in all)
        {
            inhabitant.Needs[Purpose] = 0.2;
        }

        var gain = OnPurpose(Content.Actions.EventEffects(InhabitantsSystem.KnowledgeGained, "edible/" + KnowledgeSources.Experienced));
        inhabitants.Report(new WorldEvent(InhabitantsSystem.KnowledgeGained, world.Tick, all[0].Id, "eat", "blue_berry") { Detail = "edible/" + KnowledgeSources.Experienced });
        Assert.Equal(0.2 + gain, all[0].Needs[Purpose], 12);
        Assert.All(all.Skip(1), i => Assert.Equal(0.2, i.Needs[Purpose], 12));

        // No entry of the data fits these: a guess, an event without detail, an event about nobody, another kind.
        Assert.Empty(Content.Actions.EventEffects(InhabitantsSystem.KnowledgeGained, "edible/" + KnowledgeSources.Guessed));
        Assert.Empty(Content.Actions.EventEffects(InhabitantsSystem.KnowledgeGained, null));
        inhabitants.Report(new WorldEvent(InhabitantsSystem.KnowledgeGained, world.Tick, all[1].Id, "inspect", "blue_berry") { Detail = "edible/" + KnowledgeSources.Guessed });
        inhabitants.Report(new WorldEvent(InhabitantsSystem.KnowledgeGained, world.Tick, all[1].Id, "inspect", "blue_berry"));
        inhabitants.Report(new WorldEvent(InhabitantsSystem.KnowledgeGained, world.Tick, string.Empty, null, null) { Detail = "edible/" + KnowledgeSources.Experienced });
        inhabitants.Report(new WorldEvent(InhabitantsSystem.ActionStarted, world.Tick, all[1].Id, "idle", null) { Detail = "edible/" + KnowledgeSources.Experienced });
        Assert.All(all.Skip(1), i => Assert.Equal(0.2, i.Needs[Purpose], 12));
    }

    [Fact]
    public void WithoutEventEffectsInTheDataNoEventHasAnEffect()
    {
        var file = RealFile();
        file.EventEffects = null;
        var rules = Rules(file);
        Assert.Empty(rules.EventEffects(InhabitantsSystem.KnowledgeGained, "edible/" + KnowledgeSources.Experienced));

        var (world, _, inhabitants) = NewWorld(rules);
        var inhabitant = inhabitants.Get("inh-000001");
        inhabitant.Needs[Purpose] = 0.2;
        inhabitants.Report(new WorldEvent(InhabitantsSystem.KnowledgeGained, world.Tick, inhabitant.Id, "eat", "blue_berry") { Detail = "edible/" + KnowledgeSources.Experienced });
        Assert.Equal(0.2, inhabitant.Needs[Purpose], 12);
    }

    [Fact]
    public void EventEffectsAreAppliedInDataOrderAndMayTouchMoodsAndTheWillToLive()
    {
        var file = RealFile();
        file.EventEffects =
        [
            new EventEffect { Event = InhabitantsSystem.NameLearned, Effects = [new Effect { Kind = EffectKind.Need, Id = Purpose, Delta = 0.5 }, new Effect { Kind = EffectKind.Mood, Id = "joy", Delta = 0.1 }] },
            new EventEffect { Event = InhabitantsSystem.NameLearned, DetailEndsWith = "/x", Effects = [new Effect { Kind = EffectKind.Need, Id = Purpose, Delta = -0.3 }, new Effect { Kind = EffectKind.Will_to_live, Id = "will_to_live", Delta = -0.1 }] },
        ];
        var (world, _, inhabitants) = NewWorld(Rules(file));
        var inhabitant = inhabitants.Get("inh-000001");
        inhabitant.Needs[Purpose] = 0.9;
        var (joy, will) = (inhabitant.Moods["joy"], inhabitant.WillToLive);

        // +0.5 is clamped at 1 first, then −0.3: the order of the data decides.
        inhabitants.Report(new WorldEvent(InhabitantsSystem.NameLearned, world.Tick, inhabitant.Id, "introduce", "inh-000002") { Detail = "a/x" });
        Assert.Equal(0.7, inhabitant.Needs[Purpose], 12);
        Assert.Equal(joy + 0.1, inhabitant.Moods["joy"], 12);
        Assert.Equal(will - 0.1, inhabitant.WillToLive, 12);
    }

    [Fact]
    public void EventEffectsWithUnknownIdsOrAListenerShareAreRefusedAtLoad()
    {
        static ActionEffects With(EventEffect effect)
        {
            var file = RealFile();
            file.EventEffects = [effect];
            return file;
        }

        var ok = new Effect { Kind = EffectKind.Need, Id = Purpose, Delta = 0.1 };
        Assert.NotNull(Rules(With(new EventEffect { Event = InhabitantsSystem.KnowledgeGained, Effects = [ok] })));
        Assert.Contains("unknown event", Assert.Throws<InvalidDataException>(() => Rules(With(new EventEffect { Event = "nonsense", Effects = [ok] }))).Message, StringComparison.Ordinal);
        Assert.Contains("unknown Need", Assert.Throws<InvalidDataException>(() => Rules(With(new EventEffect { Event = InhabitantsSystem.KnowledgeGained, Effects = [new Effect { Kind = EffectKind.Need, Id = "nonsense", Delta = 0.1 }] }))).Message, StringComparison.Ordinal);
        Assert.Contains("listener_share", Assert.Throws<InvalidDataException>(() => Rules(With(new EventEffect { Event = InhabitantsSystem.KnowledgeGained, Effects = [new Effect { Kind = EffectKind.Need, Id = Purpose, Delta = 0.1, ListenerShare = 0.5 }] }))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheModelHoldsEveryFeelingOfTheVocabularyAndEveryInhabitantStartsWithAll()
    {
        // A1626 (D923): the concept names ten feelings; the model gives each a rest value, so none is missing.
        var vocabulary = Ids(VocabularyKind.Mood);
        Assert.Equal(vocabulary.Order(StringComparer.Ordinal), Content.InhabitantModel.Moods.Select(m => m.Id).Order(StringComparer.Ordinal));

        var (world, _, inhabitants) = NewWorld(Content.Actions);
        world.Step();
        Assert.All(inhabitants.All, i => Assert.Equal(vocabulary.Order(StringComparer.Ordinal), i.Moods.Keys.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void WhatOneMadeOrFoundOutMakesItProud()
    {
        // A1626: pride comes from events in the data (W1744 path); the values are the data's.
        var (world, _, inhabitants) = NewWorld(Content.Actions);
        var maker = inhabitants.Get("inh-000001");
        var other = inhabitants.Get("inh-000002");
        foreach (var kind in new[] { "invented", "built", "discovered_process", "praised" })
        {
            var gain = Content.Actions.EventEffects(kind, null).Where(e => e.Kind == EffectKind.Mood && e.Id == "pride").Sum(e => e.Delta);
            Assert.True(gain > 0, $"content: {kind} makes proud");
            maker.Moods["pride"] = other.Moods["pride"] = 0.1;
            inhabitants.Report(new WorldEvent(kind, world.Tick, maker.Id, null, null));
            Assert.Equal(0.1 + gain, maker.Moods["pride"], 12);
            Assert.Equal(0.1, other.Moods["pride"], 12);
        }
    }

    private static double OnPurpose(IEnumerable<Effect> effects) => effects.Where(e => e.Kind == EffectKind.Need && e.Id == Purpose).Sum(e => e.Delta);

    private static double Decay(World world) =>
        Content.InhabitantModel.Needs.Single(n => n.Id == Purpose).DecayPerHour / world.Calendar.Settings.TicksPerHour;

    private static ActionEffects RealFile() =>
        ContractSerializer.Deserialize<ActionEffects>(File.ReadAllText(Path.Combine(ContentDirectory, "effects", "basic-actions.json")));

    private static ActionRules Rules(ActionEffects file) => ActionRules.Create(
        [file],
        Content.Objects,
        Content.Vocabularies[VocabularyKind.Action],
        Ids(VocabularyKind.Need),
        Ids(VocabularyKind.Mood),
        Content.Calendar.Settings.TicksPerHour,
        Ids(VocabularyKind.Event));

    private static HashSet<string> Ids(VocabularyKind kind) => Content.Vocabularies[kind].Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

    private static void Eat(World world, WorldObjects objects, Inhabitant eater)
    {
        var berry = objects.Spawn("blue_berry", new Position(eater.Position.X + 1, eater.Position.Z));
        eater.Activity = new Activity("eat", berry.Id, berry.Position, null);
        RunUntil(world, () => eater.Activity is null);
    }

    private static void RunUntil(World world, Func<bool> done, int maxTicks = 5_000)
    {
        for (var i = 0; i < maxTicks && !done(); i++)
        {
            world.Step();
        }

        Assert.True(done(), "the action came to its end");
    }

    /// <summary>Two inhabitants near each other and two far away, with the action rules given.</summary>
    private static (World World, WorldObjects Objects, InhabitantsSystem Inhabitants) NewWorld(ActionRules actions)
    {
        var world = new World(1, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Content.Knowledge,
            EventKinds = Content.EventKinds,
        };
        world.Add(objects).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [new Position(0, 0), new Position(2, 0), new Position(1000, 0), new Position(0, 1000)]);
        foreach (var inhabitant in inhabitants.All.Skip(2))
        {
            inhabitant.Activity = new Activity("idle", null, inhabitant.Position, 100_000);
        }

        return (world, objects, inhabitants);
    }
}
