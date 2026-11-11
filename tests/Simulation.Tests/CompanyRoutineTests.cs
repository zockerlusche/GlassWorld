using System.Text.Json;
using System.Text.Json.Nodes;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// W1744 (TL #20750): the routine seeks company. A consideration with an inhabitant target
/// (content/behaviour/utility.json: target_inhabitant) offers its action on the familiar, waking inhabitants
/// in sight and its search action while none of them is in sight; it yields to the body's needs, never
/// addresses a sleeper, rests like any repeat without effect and keeps the world deterministic. Every
/// expectation is read from the data, so Marcel's values can change without touching the tests.
/// </summary>
public sealed class CompanyRoutineTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);
    private static readonly UtilityWeights Weights = Content.Brain.Weights;
    private static readonly Consideration Company = Weights.Considerations.First(c => c.TargetInhabitant is not null);
    private static readonly Togetherness Together = Content.InhabitantModel.Togetherness!;
    private static readonly string Need = Company.Need!;
    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>(StringComparer.Ordinal);

    [Fact]
    public void TheShippedDataSeeksCompanyWithAFamiliarInhabitantAndASearch()
    {
        Assert.True(Content.Brain.AimsAtInhabitants);
        Assert.NotNull(Content.Brain.Familiarity);
        Assert.Equal(Together.Need, Need);
        Assert.Equal(InhabitantTargetRelation.Familiar, Company.TargetInhabitant!.Relation);
        Assert.Equal(VocabularyEntryTarget.Inhabitant, Content.Vocabularies[VocabularyKind.Action].Entries.Single(e => e.Id == Company.Action).Target);
        Assert.NotNull(Company.SearchAction);
        Assert.True(Company.YieldsToNeeds, "company gives way to the body's needs");
        Assert.Contains(Content.Actions.Applying(Company.Action, null).SelectMany(r => r.Effects), e => e.Id == Need && e.Delta > 0);
    }

    [Fact]
    public void AFamiliarInhabitantInSightIsScoredByUrgencyAndDistance()
    {
        var (_, inhabitants, _) = NewWorld([new(0, 0), new(10, 0), new(20, 0), new(500, 500)]);
        var (a, b, c) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"), inhabitants.Get("inh-000003"));
        Settle(a, 0.1);

        var options = Content.Brain.Score(a, [], Nothing, null, [b, c]);
        var talk = Assert.Single(options, o => o.TargetId is not null);
        Assert.Equal((Company.Action, b.Id), (talk.Action, talk.TargetId));
        Assert.Equal(Urgency(0.1) * DistanceFactor(10), talk.Score, 9);
        Assert.DoesNotContain(options, o => o.SearchFor is not null);

        // Nearer scores higher, and the more it lacks company the more it wants it.
        b.Position = new Position(3, 0);
        var near = Content.Brain.Score(a, [], Nothing, null, [b]).Single(o => o.TargetId == b.Id);
        Assert.True(near.Score > talk.Score);
        a.Needs[Need] = 0.0;
        Assert.True(Content.Brain.Score(a, [], Nothing, null, [b]).Single(o => o.TargetId == b.Id).Score > near.Score);

        // Content with its company: nobody is addressed for nothing.
        a.Needs[Need] = 1.0;
        Assert.DoesNotContain(Content.Brain.Score(a, [], Nothing, null, [b]), o => o.TargetId is not null || o.SearchFor is not null);
    }

    [Fact]
    public void WithNobodyFamiliarInSightTheSearchActionIsOfferedWhileTheInhabitantKnowsSomeone()
    {
        var (_, inhabitants, _) = NewWorld([new(0, 0), new(500, 0), new(20, 0), new(500, 500)]);
        var (a, b, c) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"), inhabitants.Get("inh-000003"));
        Settle(a, 0.1);
        var key = UtilityBrain.SearchKey(Company);

        foreach (var inSight in new IReadOnlyList<Inhabitant>?[] { null, [], [c] })
        {
            var searching = new HashSet<string>(StringComparer.Ordinal);
            var options = Content.Brain.Score(a, [], Nothing, searching, inSight);
            Assert.DoesNotContain(options, o => o.TargetId is not null);
            var search = Assert.Single(options, o => o.SearchFor is not null);
            Assert.Equal((Company.SearchAction, key), (search.Action, search.SearchFor));
            Assert.Equal(Urgency(0.1) * Weights.SearchWeight, search.Score, 9);
            Assert.Contains(key, searching);
        }

        // A search that was given up is not offered, but still counts as called for.
        var still = new HashSet<string>(StringComparer.Ordinal);
        Assert.DoesNotContain(Content.Brain.Score(a, [], new HashSet<string>(StringComparer.Ordinal) { key }, still, []), o => o.SearchFor is not null);
        Assert.Contains(key, still);

        // Whoever knows nobody it is familiar with has nobody to look for.
        a.Relationships.Set(b.Id, Strangers());
        var none = new HashSet<string>(StringComparer.Ordinal);
        Assert.DoesNotContain(Content.Brain.Score(a, [], Nothing, none, []), o => o.SearchFor is not null);
        Assert.Empty(none);
    }

    [Fact]
    public void AStrangerIsNotATargetAndNeitherIsSomeoneNeverMet()
    {
        var (_, inhabitants, _) = NewWorld([new(0, 0), new(500, 0), new(10, 0), new(500, 500)]);
        var (a, c) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000003"));
        Settle(a, 0.0);
        Assert.DoesNotContain(c.Id, a.Met);
        Assert.DoesNotContain(Content.Brain.Score(a, [], Nothing, null, [c]), o => o.TargetId == c.Id);

        // Met, but below the thresholds of togetherness: still a stranger.
        a.Met.Add(c.Id);
        Assert.DoesNotContain(Content.Brain.Score(a, [], Nothing, null, [c]), o => o.TargetId == c.Id);

        // The own relationship decides, each threshold on its own; what the other feels does not count.
        a.Relationships.Set(c.Id, new Relation(Together.MinAffection, Together.MinTrust - 0.01, 0));
        Assert.DoesNotContain(Content.Brain.Score(a, [], Nothing, null, [c]), o => o.TargetId == c.Id);
        a.Relationships.Set(c.Id, new Relation(Together.MinAffection - 0.01, Together.MinTrust, 0));
        Assert.DoesNotContain(Content.Brain.Score(a, [], Nothing, null, [c]), o => o.TargetId == c.Id);
        a.Relationships.Set(c.Id, new Relation(Together.MinAffection, Together.MinTrust, 0));
        Assert.Contains(Content.Brain.Score(a, [], Nothing, null, [c]), o => o.Action == Company.Action && o.TargetId == c.Id);
        Assert.Equal(Strangers(), c.Relationships.Get(a.Id, default));

        // Familiar by feeling but never met (no first meeting yet): not somebody it could go to.
        a.Met.Remove(c.Id);
        Assert.DoesNotContain(Content.Brain.Score(a, [], Nothing, null, [c]), o => o.TargetId == c.Id);
    }

    [Fact]
    public void SomebodyWhoseOptionScoresNothingIsNotThere()
    {
        // Review GW2 #228: with distance_falloff 1 a familiar inhabitant at the edge of sight scores 0. It must
        // not count as there with no option and no search, or the need would wait for nothing.
        var weights = Clone(Weights);
        weights.DistanceFalloff = 1.0;
        var brain = Brain(weights);
        var (_, inhabitants, _) = NewWorld([new(0, 0), new(weights.PerceptionRadiusM, 0), new(500, 0), new(500, 500)]);
        var (a, b) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        Settle(a, 0.1);
        var key = UtilityBrain.SearchKey(Company);

        var searching = new HashSet<string>(StringComparer.Ordinal);
        var options = brain.Score(a, [], Nothing, searching, [b]);
        Assert.DoesNotContain(options, o => o.TargetId == b.Id);
        Assert.Contains(options, o => o.SearchFor == key && o.Score > 0);
        Assert.Contains(key, searching);

        // A sleeper at the same spot is there: nobody walks off from it.
        b.Activity = new Activity(Together.SleepAction, null, b.Position, 200);
        searching.Clear();
        Assert.DoesNotContain(brain.Score(a, [], Nothing, searching, [b]), o => o.TargetId is not null || o.SearchFor is not null);
        Assert.Empty(searching);

        // Nearer and awake the action is offered on it, and there is nothing to search for.
        b.Activity = null;
        b.Position = new Position(weights.PerceptionRadiusM / 2, 0);
        searching.Clear();
        options = brain.Score(a, [], Nothing, searching, [b]);
        Assert.Contains(options, o => o.TargetId == b.Id && o.Score > 0);
        Assert.DoesNotContain(options, o => o.SearchFor is not null);
        Assert.Empty(searching);
    }

    [Fact]
    public void ADeciderChoiceQueuedOrUnderWayIsNotDisplacedByTheWishForCompany()
    {
        // Review GW2 #228: an accepted decider choice (an AI call) must not lose against a routine option that
        // gives way to needs. The routine only chooses when nothing is queued and nothing is under way; what
        // the decisions system sets (Planned behind a running action, else Activity) is carried out first.
        var (world, inhabitants, events) = NewWorld([new(0, 0), new(1.2, 0), new(500, 0), new(500, 500)], out var objects);
        var (a, b) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        Settle(a, 0.0);
        Settle(b, 1.0);
        b.Activity = new Activity("idle", null, b.Position, 100_000);
        var thing = objects.Spawn("apple", new Position(0, 30));
        world.Step();
        Assert.Equal((Company.Action, b.Id), (a.Activity?.Action, a.Activity?.TargetId));
        Assert.NotNull(a.Activity!.RemainingTicks);

        // Queued behind the running talk: when the talk ends, the choice starts, although the partner stands
        // next to it and the wish for company is far from met.
        var choice = new Activity("inspect", thing.Id, thing.Position, null);
        a.Planned = choice;
        for (var t = 0; t < 100 && !events.Any(e => e.Kind == InhabitantsSystem.ActionFinished && e.InhabitantId == a.Id); t++)
        {
            world.Step();
        }

        world.Step();
        Assert.Equal(b.Id, Content.Brain.Score(a, [thing], Nothing, null, [b])[0].TargetId);
        Assert.Equal((choice.Action, choice.TargetId), (a.Activity?.Action, a.Activity?.TargetId));
        Assert.Null(a.Planned);

        // Under way: the walk to the thing goes on tick after tick; the routine is not asked again.
        for (var t = 0; t < 100 && a.Activity is { RemainingTicks: null }; t++)
        {
            Assert.Equal((choice.Action, choice.TargetId), (a.Activity?.Action, a.Activity?.TargetId));
            world.Step();
        }

        Assert.Contains(events, e => e.Kind == InhabitantsSystem.ActionStarted && e.InhabitantId == a.Id && e.Action == choice.Action && e.TargetId == thing.Id);
        Assert.Single(events, e => e.Kind == InhabitantsSystem.ActionStarted && e.InhabitantId == a.Id && e.Action == Company.Action);

        // Only a neglected body need drops a queued choice that does not serve it (F5275 B), as before.
        Assert.NotNull(a.Activity?.RemainingTicks);
        a.Planned = new Activity("inspect", thing.Id, thing.Position, null);
        a.Needs["thirst"] = Content.InhabitantModel.Needs.Single(n => n.Id == "thirst").NeglectBelow / 2;
        for (var t = 0; t < 100 && a.Planned is not null; t++)
        {
            world.Step();
        }

        Assert.Contains(events, e => e.Kind == InhabitantsSystem.AnswerExpired && e.InhabitantId == a.Id && e.Detail == InhabitantsSystem.NeedNeglected);
    }

    [Fact]
    public void ASleeperIsNotAddressedAndNobodyWalksOffFromOne()
    {
        var (world, inhabitants, events) = NewWorld([new(0, 0), new(10, 0), new(500, 0), new(500, 500)]);
        var (a, b) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        Settle(a, 0.0);
        Settle(b, 1.0);

        // On its way to bed the partner is still awake; once the sleep has started it is left alone.
        b.Activity = new Activity(Together.SleepAction, null, b.Position, null);
        Assert.Contains(Content.Brain.Score(a, [], Nothing, null, [b]), o => o.TargetId == b.Id);
        b.Activity = new Activity(Together.SleepAction, null, b.Position, 200);
        var searching = new HashSet<string>(StringComparer.Ordinal);
        var options = Content.Brain.Score(a, [], Nothing, searching, [b]);
        Assert.DoesNotContain(options, o => o.TargetId is not null);
        Assert.DoesNotContain(options, o => o.SearchFor is not null);
        Assert.Empty(searching);

        var (at, company) = (a.Position, b.Needs[Need]);
        world.Run(100);
        Assert.Equal(Together.SleepAction, b.Activity?.Action);
        Assert.DoesNotContain(events, e => e.InhabitantId == a.Id && e.TargetId == b.Id);
        Assert.Equal(at, a.Position);
        Assert.True(b.Needs[Need] <= company, "nothing reaches the sleeper");
    }

    [Fact]
    public void WhoeverIsOnTheWayToSomeoneWhoFallsAsleepGivesItUp()
    {
        var (world, inhabitants, events) = NewWorld([new(0, 0), new(30, 0), new(500, 0), new(500, 500)]);
        var (a, b) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        Settle(a, 0.0);
        Settle(b, 1.0);
        world.Run(3);
        Assert.Equal((Company.Action, b.Id), (a.Activity?.Action, a.Activity?.TargetId));
        Assert.Null(a.Activity!.RemainingTicks);

        b.Activity = new Activity(Together.SleepAction, null, b.Position, 200);
        world.Step();
        var cancelled = Assert.Single(events, e => e.Kind == InhabitantsSystem.ActionCancelled && e.InhabitantId == a.Id);
        Assert.Equal((Company.Action, b.Id, InhabitantsSystem.ListenerAsleep), (cancelled.Action, cancelled.TargetId, cancelled.Detail));
        world.Run(60);
        Assert.DoesNotContain(events, e => e.Kind == InhabitantsSystem.ActionStarted && e.InhabitantId == a.Id && e.TargetId == b.Id);

        // The rule checker holds for every action aimed at a sleeper, whoever chose it (the decider included).
        events.Clear();
        a.Activity = new Activity("teach", b.Id, b.Position, null);
        world.Step();
        Assert.Contains(events, e => e.Kind == InhabitantsSystem.ActionCancelled && e.Action == "teach" && e.Detail == InhabitantsSystem.ListenerAsleep);
        Assert.Null(a.Activity);
    }

    [Fact]
    public void OutOfSightTheWalkerGoesToWhereItSawTheOtherLastAndGivesUpThere()
    {
        var (world, inhabitants, events) = NewWorld([new(0, 0), new(30, 0), new(500, 0), new(500, 500)]);
        var (a, b) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        Settle(a, 0.0);
        Settle(b, 1.0);
        world.Run(3);
        Assert.Equal((Company.Action, b.Id), (a.Activity?.Action, a.Activity?.TargetId));
        var lastSeen = b.Position;

        // The partner is gone further than anyone sees: the walker cannot know where to.
        b.Position = new Position(lastSeen.X + (inhabitants.SightRadiusM * 3), lastSeen.Z);
        b.Activity = new Activity("idle", null, b.Position, 500);
        for (var t = 0; t < 60 && !events.Any(e => e.Kind == InhabitantsSystem.ActionCancelled && e.InhabitantId == a.Id); t++)
        {
            world.Step();
            Assert.True(a.Position.X <= lastSeen.X + 1e-9, "it never follows what it cannot see");
        }

        var cancelled = Assert.Single(events, e => e.Kind == InhabitantsSystem.ActionCancelled && e.InhabitantId == a.Id);
        Assert.Equal((Company.Action, b.Id, InhabitantsSystem.ListenerOutOfSight), (cancelled.Action, cancelled.TargetId, cancelled.Detail));
        Assert.True(Distance(a.Position, lastSeen) <= Content.Gaits.Data.Spacing!.ConversationMaxM + 1e-9);
        Assert.DoesNotContain(events, e => e.Kind == InhabitantsSystem.ActionStarted && e.InhabitantId == a.Id && e.TargetId == b.Id);
    }

    [Fact]
    public void TheRoutineWalksOverTalksAndBothFeelLessAlone()
    {
        var (world, inhabitants, events) = NewWorld([new(0, 0), new(20, 0), new(500, 0), new(500, 500)]);
        var (a, b) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        var effect = Content.Actions.Applying(Company.Action, null).SelectMany(r => r.Effects).Single(e => e.Id == Need);
        Settle(a, 0.2);

        // The partner is content (its own wish is zero there) but has room to gain.
        var content = Company.Curve.Select(p => p.ToArray()).First(p => p[1] <= 0)[0];
        Assert.True(content + (effect.Delta * 2) <= 1.0, "the test needs room above the content value");
        Settle(b, content + (effect.Delta * 0.5));
        var (mine, theirs, feeling) = (a.Needs[Need], b.Needs[Need], a.Relationships.Get(b.Id, default));

        for (var t = 0; t < 200 && !events.Any(e => e.Kind == InhabitantsSystem.ActionFinished && e.InhabitantId == a.Id && e.Action == Company.Action); t++)
        {
            world.Step();
        }

        var started = Assert.Single(events, e => e.Kind == InhabitantsSystem.ActionStarted && e.InhabitantId == a.Id && e.TargetId == b.Id);
        Assert.Equal(Company.Action, started.Action);
        Assert.Contains(events, e => e.Kind == InhabitantsSystem.ActionFinished && e.InhabitantId == a.Id && e.Action == Company.Action && e.TargetId == b.Id);
        var spacing = Content.Gaits.Data.Spacing!;
        Assert.InRange(Distance(a.Position, b.Position), spacing.ConversationMinM - 1e-9, spacing.ConversationMaxM + 1e-9);

        var ticks = world.Tick;
        var decay = Content.InhabitantModel.Needs.Single(n => n.Id == Need).DecayPerHour * ticks / world.Calendar.Settings.TicksPerHour;
        Assert.True(a.Needs[Need] >= mine + effect.Delta - decay - 1e-9, "the one who talks gains the effect of the data");
        Assert.Equal(theirs + (effect.Delta * effect.ListenerShare!.Value) - decay, b.Needs[Need], 9);
        Assert.True(a.Relationships.Get(b.Id, default).Affection > feeling.Affection);
    }

    [Theory]
    [InlineData("hunger", "eat", "apple", "edible")]
    [InlineData("thirst", "drink", "spring_water", "drinkable")]
    public void TheWishForCompanyNeverOutbidsANeglectedBodyNeed(string need, string action, string type, string property)
    {
        var (world, inhabitants, events) = NewWorld([new(0, 0), new(2, 0), new(500, 0), new(500, 500)], out var objects);
        var (a, b) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        Settle(a, 0.0);
        Settle(b, 1.0);
        var thing = objects.Spawn(type, new Position(0, 40));
        a.Beliefs.Set(type, property, true, 1.0);
        var neglect = Content.InhabitantModel.Needs.Single(n => n.Id == need).NeglectBelow;

        // Neglected: the wish for company is silent, although the partner stands next to it and the thing is far.
        a.Needs[need] = neglect - 0.01;
        var options = Content.Brain.Score(a, [thing], Nothing, null, [b]);
        Assert.Equal((action, thing.Id), (options[0].Action, options[0].TargetId));
        Assert.DoesNotContain(options, o => o.TargetId == b.Id);

        // Low but not yet neglected: the body's need still comes first.
        a.Needs[need] = neglect + 0.05;
        options = Content.Brain.Score(a, [thing], Nothing, null, [b]);
        Assert.Equal((action, thing.Id), (options[0].Action, options[0].TargetId));

        // And so it acts: first the body, then the company.
        a.Needs[need] = neglect - 0.01;
        for (var t = 0; t < 400 && !events.Any(e => e.Kind == InhabitantsSystem.ActionStarted && e.InhabitantId == a.Id && e.TargetId == b.Id); t++)
        {
            world.Step();
        }

        var order = events.Where(e => e.Kind == InhabitantsSystem.ActionStarted && e.InhabitantId == a.Id && e.TargetId is not null).Select(e => e.Action).ToList();
        Assert.Equal(action, order[0]);
        Assert.Contains(Company.Action, order);
    }

    [Fact]
    public void TalkingWithoutEffectRestsLikeAnyRepeat()
    {
        // A wish that never ends (curve constant 1) towards a partner when nobody can gain anything from talking.
        var repetition = Weights.Repetition!;
        var weights = Clone(Weights);
        var company = weights.Considerations.First(c => c.TargetInhabitant is not null);
        company.Curve = [[0.0, 1.0], [1.0, 1.0]];
        company.YieldsToNeeds = false;
        company.Weight = 10;
        var (world, inhabitants, events) = NewWorld([new(0, 0), new(1.2, 0), new(500, 0), new(500, 500)], out _, Brain(weights));
        var (a, b) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        foreach (var i in new[] { a, b })
        {
            Settle(i, 1.0);
            i.Moods["boredom"] = 0;
        }

        b.Activity = new Activity("idle", null, b.Position, 100_000);
        var restTicks = (long)Math.Round(repetition.RestHours * world.Calendar.Settings.TicksPerHour);
        for (var t = 0; t < 400 && a.Resting.Count == 0; t++)
        {
            world.Step();
        }

        var talks = events.Where(e => e.Kind == InhabitantsSystem.ActionFinished && e.InhabitantId == a.Id && e.Action == company.Action).ToList();
        Assert.Equal(repetition.MaxRepeatsWithoutEffect, talks.Count);
        Assert.Equal(InhabitantsSystem.RepeatLimit, talks[^1].Detail);
        Assert.Contains(a.Resting, r => r.Action == company.Action && r.TargetId == b.Id);

        // While it rests the partner is not addressed again, however strong the wish.
        var rested = world.Tick;
        events.Clear();
        world.Run(restTicks - 2);
        Assert.DoesNotContain(events, e => e.InhabitantId == a.Id && e.Action == company.Action && e.Kind == InhabitantsSystem.ActionStarted);
        world.Run(60);
        Assert.Contains(events, e => e.InhabitantId == a.Id && e.Action == company.Action && e.Kind == InhabitantsSystem.ActionStarted && e.Tick > rested + restTicks - 2);
    }

    [Fact]
    public void SameSeedSameStateHashWhileTheRoutineSeeksCompany()
    {
        // No answers from the decider: every talk comes from the routine.
        var (first, second) = (Island(7), Island(7));
        var talks = 0;
        first.Inhabitants.Reported += e => talks += e.Kind == InhabitantsSystem.ActionStarted && e.Action == Company.Action ? 1 : 0;
        var ticks = first.World.Calendar.TicksPerDay * 2;
        for (var t = 0; t < ticks; t += 240)
        {
            first.World.Run(240);
            second.World.Run(240);
            Assert.Equal(first.World.StateHash(), second.World.StateHash());
        }

        Assert.True(talks > 0, "the routine talked to a partner");
        Assert.NotEqual(first.World.StateHash(), Island(8, ticks).World.StateHash());

        // A game saved in the middle of it walks on identically.
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        var loaded = StateCodec.Restore(new SimulationHostFactory(Content, settings, new SilentPort(), "de"), StateCodec.Capture(first));
        Assert.Equal(first.World.StateHash(), loaded.World.StateHash());
        first.World.Run(720);
        loaded.World.Run(720);
        Assert.Equal(first.World.StateHash(), loaded.World.StateHash());
    }

    [Fact]
    public void AnInhabitantTargetWithWrongDataIsRefused()
    {
        var needs = Ids(VocabularyKind.Need);
        var targetless = Targets(VocabularyEntryTarget.None);
        var atInhabitants = Targets(VocabularyEntryTarget.Inhabitant);
        UtilityBrain Build(Action<Consideration> change)
        {
            var weights = Clone(Weights);
            change(weights.Considerations.First(c => c.TargetInhabitant is not null));
            return new UtilityBrain(weights, Content.Actions, needs, Content.Objects.PropertyVocabulary, targetless, Ids(VocabularyKind.Trait), null, Content.Brain.Familiarity, atInhabitants);
        }

        _ = Build(_ => { });
        Assert.Contains("target_inhabitant", Assert.Throws<InvalidDataException>(() => Build(c => c.TargetProperty = "edible")).Message, StringComparison.Ordinal);
        Assert.Contains("target_inhabitant", Assert.Throws<InvalidDataException>(() => Build(c => c.AvoidProperties = ["poisonous"])).Message, StringComparison.Ordinal);
        Assert.Contains("target_inhabitant", Assert.Throws<InvalidDataException>(() => Build(c => (c.SearchAction, c.NoveltyCurve) = (null, [[0.0, 0.0], [1.0, 1.0]]))).Message, StringComparison.Ordinal);
        Assert.Contains("aimed at inhabitants", Assert.Throws<InvalidDataException>(() => Build(c => c.Action = "eat")).Message, StringComparison.Ordinal);
        Assert.Contains("search_action", Assert.Throws<InvalidDataException>(() => Build(c => c.SearchAction = "inspect")).Message, StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => Build(c => c.Action = "dance_with"));
        Assert.Throws<InvalidDataException>(() => Build(c => c.Need = "applause"));

        // The schema knows the relations; an unknown one never reaches the core.
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(ContentDirectory, "behaviour", "utility.json")))!;
        Assert.Empty(ContractValidator.Validate("utility-weights", json.ToJsonString()));
        var target = json["considerations"]!.AsArray().First(c => c!["target_inhabitant"] is not null)!["target_inhabitant"]!;
        target["relation"] = "enemy";
        Assert.NotEmpty(ContractValidator.Validate("utility-weights", json.ToJsonString()));
        target["relation"] = "familiar";
        target["min_trust"] = 0.9;
        Assert.NotEmpty(ContractValidator.Validate("utility-weights", json.ToJsonString()));
    }

    [Fact]
    public void WithoutTogethernessAnInhabitantTargetIsRefusedAtLoad()
    {
        // Who is familiar and who is asleep is defined once, by togetherness of the inhabitant model.
        var directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());
        try
        {
            foreach (var entry in Directory.GetFiles(ContentDirectory, "*.json", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(directory, Path.GetRelativePath(ContentDirectory, entry));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(entry, copy);
            }

            Assert.True(ContentSet.Load(directory).Brain.AimsAtInhabitants);
            var path = Path.Combine(directory, "inhabitants", "model.json");
            var model = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            model.Remove("togetherness");
            File.WriteAllText(path, model.ToJsonString());
            var error = Assert.Throws<InvalidDataException>(() => ContentSet.Load(directory));
            Assert.Contains("target_inhabitant", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>Urgency of the shipped consideration at a company value while no other need presses (calm 1).</summary>
    private static double Urgency(double company) => Company.Weight * new Curve(Company.Curve).At(company);

    private static double DistanceFactor(double metres) => 1.0 - (Weights.DistanceFalloff * Math.Min(1.0, metres / Weights.PerceptionRadiusM));

    private static double Distance(Position a, Position b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    private static Relation Strangers()
    {
        var strangers = Content.Knowledge.StartRelationships!.Strangers;
        return new Relation(strangers.Affection, strangers.Trust, strangers.Respect);
    }

    /// <summary>All needs met and nothing to be bored of, except the company at the given value.</summary>
    private static void Settle(Inhabitant inhabitant, double company)
    {
        foreach (var need in inhabitant.Needs.Keys.ToList())
        {
            inhabitant.Needs[need] = 1.0;
        }

        inhabitant.Needs[Need] = company;
    }

    private static HashSet<string> Ids(VocabularyKind kind) =>
        Content.Vocabularies[kind].Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Targets(VocabularyEntryTarget target) =>
        Content.Vocabularies[VocabularyKind.Action].Entries.Where(e => (e.Target ?? VocabularyEntryTarget.None) == target).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    private static UtilityBrain Brain(UtilityWeights weights) => new(
        weights,
        Content.Actions,
        Ids(VocabularyKind.Need),
        Content.Objects.PropertyVocabulary,
        Targets(VocabularyEntryTarget.None),
        Ids(VocabularyKind.Trait),
        Content.InhabitantModel.Needs.Where(n => n.NeglectBelow > 0).ToDictionary(n => n.Id, n => n.NeglectBelow, StringComparer.Ordinal),
        Content.Brain.Familiarity,
        Targets(VocabularyEntryTarget.Inhabitant));

    private static SimulationHost Island(ulong seed, long ticks = 0)
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, seed, new SilentPort(), "de", "start_island");
        host.World.Run(ticks);
        return host;
    }

    private static (World World, InhabitantsSystem Inhabitants, List<WorldEvent> Events) NewWorld(Position[] at) => NewWorld(at, out _);

    /// <summary>The start inhabitants at the given spots on a world without a map and without a decider: the routine alone.</summary>
    private static (World World, InhabitantsSystem Inhabitants, List<WorldEvent> Events) NewWorld(Position[] at, out WorldObjects objects, UtilityBrain? brain = null)
    {
        var world = new World(3, Content.Calendar);
        objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, brain ?? Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Content.Knowledge,
        };
        world.Add(objects).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, at);
        var events = new List<WorldEvent>();
        inhabitants.Reported += events.Add;
        return (world, inhabitants, events);
    }

    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
