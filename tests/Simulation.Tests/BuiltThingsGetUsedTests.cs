using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Player;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// D9 (B4b): what was built gets used. Whoever built a thing knows by its own doing that it is made (a
/// belief with source experienced, only for what every invention is by being made); what the thing is
/// beyond that is the inventor's own guess (source guessed). The routine tries the use on things believed
/// made for the need the data names (content/behaviour/utility.json); whether the use helps is decided by
/// the world from the true properties, and a use that brings nothing teaches and ends. The chain wish →
/// invention → plan → building → built → first use runs here without the decider: its port never answers,
/// so every use is the routine's. Expectations are read from the data, so Marcel's values can change
/// without touching the tests - except where a test says that it pins a decision of the proposal.
/// </summary>
public sealed class BuiltThingsGetUsedTests : IDisposable
{
    private static readonly string ShippedDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Shipped = ContentSet.Load(ShippedDirectory);
    private static readonly UtilityWeights Weights = Shipped.Brain.Weights;
    private static readonly BuildSettings Build = Shipped.Inventions.Build.Settings;
    private static readonly Consideration Use = Weights.Considerations.Single(c => c.Action == Build.UseAction);
    private static readonly Curve UseCurve = new(Use.Curve);
    private static readonly string Need = Use.Need!;
    private static readonly string Made = Use.TargetProperty!;

    // The shipped content with a quiet body: only the need the made thing serves decays, so the chain can
    // be watched on its own (nobody has to find water in a test world) and every run can be replayed.
    private static readonly Lazy<string> QuietDirectory = new(CopyContentWithAQuietBody);
    private static readonly Lazy<ContentSet> Quiet = new(() => ContentSet.Load(QuietDirectory.Value));

    // Test data from the start catalogue: the invention is made of the first, its step of the last.
    private static readonly string[] NearbyTypes = ["flint", "dry_grass", "pyrite"];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());
    private readonly ITestOutputHelper _output;

    public BuiltThingsGetUsedTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    // ------------------------------------------------------------------ the data

    [Fact]
    public void TheShippedDataUsesWhatIsBelievedMadeForTheNeedTheUseServes()
    {
        // The use action of the building data, on what every invention is by being made, for the need the
        // use really fills (action rules); it gives way to the body's needs and has no search: a made thing
        // has a place, and the routine has no way yet to walk back to a place out of sight.
        Assert.Contains(Made, Shipped.Inventions.Settings.ImpliedProperties);
        Assert.Contains(Shipped.Actions.Rules(Use.Action), r => r.RequiresProperty == Made && r.Effects.Any(e => e.Kind == EffectKind.Need && e.Id == Need && e.Delta > 0));
        Assert.Null(Use.SearchAction);
        Assert.True(Use.YieldsToNeeds, "using a made thing gives way to the body's needs");
        Assert.Empty(Use.AvoidProperties);
        Assert.Equal(0.0, UseCurve.At(1.0), 9);
        Assert.True(UseCurve.At(0.0) > 0);
        Assert.Empty(ContractValidator.Validate("utility-weights", File.ReadAllText(Path.Combine(ShippedDirectory, "behaviour", "utility.json"))));
    }

    // ------------------------------------------------------------------ knowing what one made

    [Fact]
    public void WhoBuiltAThingKnowsByExperienceThatItIsMadeAndGuessesWhatItsIdeaWasFor()
    {
        var port = new ScriptedJobPort();
        var host = World(port, 41);
        var builder = host.Inhabitants.All.Single();
        var events = Record(host);
        var typeId = Invent(host, port);
        AnswerPlan(port, Plan());
        RunUntil(host, () => host.Inventions.Builds.Single().StepsDone == 1, 3000);

        // The world knows the truth from the moment of the invention; the inventor believes nothing yet.
        Assert.True(host.Types.HasProperty(typeId, Made));
        Assert.Empty(builder.Beliefs.About(typeId));

        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, 3000);
        var built = events.Single(e => e.Kind == InventionSystem.Built);
        var knowledge = Quiet.Value.Knowledge;
        var implied = Quiet.Value.Inventions.Settings.ImpliedProperties.Order(StringComparer.Ordinal).ToList();
        var introduced = Invention().Introduces.Except(implied, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(introduced);
        Assert.Equal(implied.Concat(introduced).Order(StringComparer.Ordinal), builder.Beliefs.About(typeId).Select(b => b.PropertyId));
        foreach (var property in implied)
        {
            Assert.True(builder.Beliefs.TryGet(typeId, property, out var belief));
            Assert.Equal((true, knowledge.ExperiencedConfidence, KnowledgeSources.Experienced, null, built.Tick), (belief.Value, belief.Confidence, belief.Source, belief.ToldBy, belief.Tick));
            var gained = Assert.Single(events, e => e.Kind == InhabitantsSystem.KnowledgeGained && e.TargetId == typeId && e.Detail == property + "/" + KnowledgeSources.Experienced);
            Assert.Equal((builder.Id, Build.StepAction, built.Tick), (gained.InhabitantId, gained.Action, gained.Tick));
            Assert.True(events.IndexOf(gained) > events.IndexOf(built));
        }

        // What the invention introduces beyond that is the inventor's own idea of the thing: a guess, as
        // sure as a guess can be (guessed_cap), never knowledge.
        foreach (var property in introduced)
        {
            Assert.True(builder.Beliefs.TryGet(typeId, property, out var guess));
            Assert.Equal((true, knowledge.GuessedCap, KnowledgeSources.Guessed, null, built.Tick), (guess.Value, guess.Confidence, guess.Source, guess.ToldBy, guess.Tick));
            Assert.Single(events, e => e.Kind == InhabitantsSystem.KnowledgeGained && e.InhabitantId == builder.Id && e.TargetId == typeId && e.Detail == property + "/" + KnowledgeSources.Guessed);
        }

        Assert.True(knowledge.GuessedCap < knowledge.ExperiencedConfidence);
    }

    [Fact]
    public void LookingAndTryingReplaceTheInventorsGuessesLikeEveryOtherGuess()
    {
        var (host, _, typeId, builtId) = Built(48);
        var builder = host.Inhabitants.All.Single();
        var looking = Quiet.Value.Knowledge.Looking!;
        var guessed = Invention().Introduces.Single();
        Assert.Contains(guessed, looking.VisibleProperties);
        Assert.True(builder.Beliefs.TryGet(typeId, guessed, out var before));
        Assert.Equal(KnowledgeSources.Guessed, before.Source);

        // Looking shows what can be seen (the truth of the world): the guess becomes something seen.
        Assert.True(host.Objects.TryGet(builtId, out var thing));
        builder.Planned = new Activity(looking.Action, builtId, thing.Position, null);
        var events = Record(host);
        RunUntil(host, () => events.Any(e => e.Kind == InhabitantsSystem.ActionFinished && e.Action == looking.Action && e.TargetId == builtId), 600);
        Assert.True(builder.Beliefs.TryGet(typeId, guessed, out var seen));
        Assert.Equal((host.Types.HasProperty(typeId, guessed), looking.InspectConfidence, KnowledgeSources.Seen), (seen.Value, seen.Confidence, seen.Source));

        // The use itself teaches what its effect rules depend on (today: that the thing is made) and
        // nothing about the other properties - they have no effect rule for it yet (data).
        Assert.Equal([Made], Quiet.Value.Actions.Rules(Use.Action).Select(r => r.RequiresProperty).OfType<string>().Distinct());
    }

    [Fact]
    public void AUseThatBringsNothingTeachesAndIsNotRepeated()
    {
        // The world decides whether a use helps (effect rule on the true property), not the routine: someone
        // told that a stone is a made thing tries it, gets nothing from it, knows better and leaves it.
        var host = SimulationHost.CreateEmpty(QuietDirectory.Value, 49, new SilentPort(), "de");
        var knowledge = Quiet.Value.Knowledge;
        var misled = Person("inh-000001", new Position(0, 0));
        var teller = Person("inh-000002", new Position(500, 500));
        misled.Needs[Need] = 0.0;
        host.Inhabitants.Add(misled);
        host.Inhabitants.Add(teller);
        var stone = host.Objects.Spawn(NearbyTypes[0], new Position(1, 0));
        Assert.False(host.Types.HasProperty(stone.TypeId, Made));
        var start = new Relation(knowledge.RelationshipStart.Affection, knowledge.RelationshipStart.Trust, knowledge.RelationshipStart.Respect);
        misled.Beliefs.Set(stone.TypeId, Made, true, 1.0, KnowledgeSources.Heard, teller.Id);
        var events = Record(host);
        var before = misled.Needs[Need];

        host.World.Run(host.World.Calendar.Settings.TicksPerHour * 12);
        var use = Assert.Single(events, e => e.Kind == InhabitantsSystem.ActionFinished && e.InhabitantId == misled.Id && e.Action == Use.Action);
        Assert.Equal((stone.Id, ActionOutcomes.FoundOut), (use.TargetId, use.Outcome));
        Assert.True(misled.Beliefs.TryGet(stone.TypeId, Made, out var belief));
        Assert.Equal((false, KnowledgeSources.Experienced), (belief.Value, belief.Source));
        Assert.True(misled.Relationships.Get(teller.Id, start).Trust < start.Trust);
        Assert.DoesNotContain(events, e => e.Kind == InventionSystem.FirstUse);

        // Nothing came of the use itself: what the need gained is what finding out is worth (event effects).
        var foundOut = Quiet.Value.Actions.EventEffects(InhabitantsSystem.KnowledgeGained, Made + "/" + KnowledgeSources.Experienced).Where(e => e.Kind == EffectKind.Need && e.Id == Need).Sum(e => e.Delta);
        Assert.True(misled.Needs[Need] <= before + foundOut + 1e-9);
    }

    // ------------------------------------------------------------------ using it

    [Fact]
    public void WithTheNeedLowTheRoutineUsesTheBuiltThingTheFirstUseIsReportedAndTheNeedRises()
    {
        var (host, _, typeId, builtId) = Built(42);
        var builder = host.Inhabitants.All.Single();
        var events = Record(host);
        var before = builder.Needs[Need];
        Assert.True(UseCurve.At(before) > 0, $"the need is low after building ({before:0.00})");

        long? decided = -1;
        host.Inhabitants.Reported += e => decided = e.Kind == InhabitantsSystem.ActionStarted && e.Action == Use.Action ? builder.Activity!.DecidedTick : decided;
        RunUntil(host, () => events.Any(e => e.Kind == InventionSystem.FirstUse), host.World.Calendar.Settings.TicksPerHour * 6);

        var first = events.Single(e => e.Kind == InventionSystem.FirstUse);
        Assert.Equal((builder.Id, Use.Action, builtId), (first.InhabitantId, first.Action, first.TargetId));
        Assert.Null(decided); // the routine's choice, not an answer of the decider
        var finished = events.Single(e => e.Kind == InhabitantsSystem.ActionFinished && e.Action == Use.Action);
        Assert.Equal((builtId, typeId, ActionOutcomes.Done), (finished.TargetId, finished.TargetType, finished.Outcome));
        Assert.True(builder.Needs[Need] > before, $"{Need} {before:0.000} -> {builder.Needs[Need]:0.000}");
        Assert.Equal([typeId], host.Inventions.FirstUsed);
        Assert.Contains(host.Chronicle.All, e => e.Kind == InventionSystem.FirstUse && e.TargetId == builtId);

        // B2594: a thing it has used is a place it knows.
        Assert.True(builder.Places.TryGetValue(builtId, out var place));
        Assert.Equal(typeId, place.TypeId);
    }

    [Fact]
    public void TheBuilderDoesNotUseItsThingAllDay()
    {
        var (host, _, _, builtId) = Built(43);
        var builder = host.Inhabitants.All.Single();
        var calendar = host.World.Calendar;
        var start = host.World.Tick;
        var uses = new List<(long Tick, double Before)>();
        host.Inhabitants.Reported += e =>
        {
            if (e.Kind == InhabitantsSystem.ActionStarted && e.Action == Use.Action && e.TargetId == builtId)
            {
                uses.Add((e.Tick, builder.Needs[Need]));
            }
        };
        const int days = 3;
        host.World.Run(calendar.TicksPerDay * days);
        foreach (var (tick, value) in uses)
        {
            _output.WriteLine($"use at +{(tick - start) / (double)calendar.Settings.TicksPerHour:0.0} h, {Need} {value:0.000}");
        }

        // It catches up while the need is low, then a use follows only when the need has sunk again: never
        // while the curve gives the wish no urgency, and per day no more than the need's decay asks for.
        Assert.NotEmpty(uses);
        Assert.All(uses, u => Assert.True(UseCurve.At(u.Before) > 0, $"used at {Need} {u.Before:0.000}"));
        var gain = Quiet.Value.Actions.Rules(Use.Action).SelectMany(r => r.Effects).Single(e => e.Kind == EffectKind.Need && e.Id == Need).Delta;
        var decayPerDay = Quiet.Value.InhabitantModel.Needs.Single(n => n.Id == Need).DecayPerHour * calendar.Settings.HoursPerDay;
        var lastDay = uses.Count(u => u.Tick >= start + (calendar.TicksPerDay * (days - 1)));
        Assert.True(lastDay <= (int)Math.Ceiling(decayPerDay / gain) + 1, $"{lastDay} uses on the last day");
        Assert.True(builder.Needs[Need] > Quiet.Value.Inventions.Settings.Trigger.NeedBelow, "the lack that raised the wish is gone");
        Assert.Empty(builder.Resting); // a use with its effect never rests (repetition guard)
    }

    [Fact]
    public void WithTheNeedMetOrABodyNeedPressingTheThingIsNotUsed()
    {
        var (maker, thing) = MakerNextToItsThing();
        Assert.Equal(Use.Action, Shipped.Brain.Score(maker, [thing])[0].Action);

        // Content: no wish at all.
        maker.Needs[Need] = 1.0;
        var met = Shipped.Brain.Score(maker, [thing]);
        Assert.NotEqual(Use.Action, met[0].Action);
        Assert.Equal(0.0, met.Single(o => o.Action == Use.Action).Score, 9);
        Assert.False(Shipped.Brain.NeedSuggests(maker, Use.Action, thing.TypeId));

        // A neglected body need comes first (yields_to_needs): nothing is left of the wish, and the decider
        // is not offered the action either (F5275 A).
        maker.Needs[Need] = 0.0;
        Assert.True(Shipped.Brain.NeedSuggests(maker, Use.Action, thing.TypeId));
        var neglected = Shipped.InhabitantModel.Needs.First(n => n.NeglectBelow > 0);
        maker.Needs[neglected.Id] = neglected.NeglectBelow / 2;
        Assert.Equal(0.0, Shipped.Brain.Score(maker, [thing]).Single(o => o.Action == Use.Action).Score, 9);
        Assert.False(Shipped.Brain.NeedSuggests(maker, Use.Action, thing.TypeId));
    }

    [Fact]
    public void TheWishIsScoredByUrgencyBeliefAndDistance()
    {
        var (maker, thing) = MakerNextToItsThing();
        maker.Needs[Need] = 0.2;
        var far = thing with { Id = "obj-000002", Position = new Position(30, 0) };
        var options = Shipped.Brain.Score(maker, [thing, far]);
        Assert.Equal(Use.Weight * UseCurve.At(0.2) * DistanceFactor(1), options.Single(o => o.Action == Use.Action && o.TargetId == thing.Id).Score, 9);
        Assert.Equal(Use.Weight * UseCurve.At(0.2) * DistanceFactor(30), options.Single(o => o.Action == Use.Action && o.TargetId == far.Id).Score, 9);

        // Told about it, not tried: as sure as the teller was times the trust in the teller.
        maker.Beliefs.Set(thing.TypeId, Made, true, 0.7, KnowledgeSources.Heard, "inh-000002");
        Assert.Equal(Use.Weight * UseCurve.At(0.2) * 0.7 * DistanceFactor(1), Shipped.Brain.Score(maker, [thing]).Single(o => o.Action == Use.Action).Score, 9);

        // Found out that it is not made: no wish for it.
        maker.Beliefs.Set(thing.TypeId, Made, false, 1.0);
        Assert.DoesNotContain(Shipped.Brain.Score(maker, [thing]), o => o.Action == Use.Action);
    }

    // ------------------------------------------------------------------ the others

    [Fact]
    public void WhoDoesNotBelieveItIsMadeIsDrawnToItNoMoreThanToAnyUnknownThing()
    {
        var (maker, thing) = MakerNextToItsThing();
        var other = Person("inh-000002", new Position(0, 0));
        other.Needs[Need] = 0.0;
        var stone = new WorldObject("obj-000002", NearbyTypes[0], new Position(-1, 0));

        var scored = Shipped.Brain.Score(other, [thing, stone]);
        var unknown = Use.Weight * UseCurve.At(0.0) * Weights.UnknownTargetBelief * DistanceFactor(1);
        Assert.Equal(unknown, scored.Single(o => o.Action == Use.Action && o.TargetId == thing.Id).Score, 9);
        Assert.Equal(unknown, scored.Single(o => o.Action == Use.Action && o.TargetId == stone.Id).Score, 9);
        Assert.True(Shipped.Brain.Score(maker, [thing, stone]).Single(o => o.TargetId == thing.Id && o.Action == Use.Action).Score > unknown);

        // This pins a decision of the proposal values, not a rule of the frame: with the shipped weight the
        // routine does not try the use on things it knows nothing about (the wish for an unknown thing stays
        // below standing around), so no inhabitant goes round "using" stones and berries when its need is
        // low. Trying remains the decider's choice. A higher weight opens it; then this assertion goes.
        Assert.NotEqual(Use.Action, scored[0].Action);
        var idle = Weights.Considerations.Single(c => c.Need is null && c.TargetProperty is null && c.Trait is null && c.NoveltyCurve is null);
        Assert.True(Use.Weight * Weights.UnknownTargetBelief < idle.Weight);
    }

    [Fact]
    public void AnotherInhabitantLeavesTheThingAloneUntilItIsToldAndThenKnowsByTrying()
    {
        var (host, _, typeId, builtId) = Built(44);
        var builder = host.Inhabitants.All.Single();
        Assert.True(host.Objects.TryGet(builtId, out var thing));
        var other = Person("inh-000002", new Position(thing.Position.X + 1, thing.Position.Z));
        other.Needs[Need] = 0.0;
        host.Inhabitants.Add(other);
        var events = Record(host);
        var hour = host.World.Calendar.Settings.TicksPerHour;

        // The thing's name belongs to what one knows of it: the builder has it, a newcomer does not.
        Assert.NotNull(host.Inventions.NameKnownTo(builder, typeId));
        Assert.Null(host.Inventions.NameKnownTo(other, typeId));

        host.World.Run(hour * 12);
        Assert.Contains(events, e => e.Kind == InventionSystem.FirstUse && e.InhabitantId == builder.Id);
        Assert.DoesNotContain(events, e => e.InhabitantId == other.Id && e.Action == Use.Action);
        Assert.False(other.Beliefs.TryGet(typeId, Made, out _));

        // Told by the builder (the existing way: a passing action hands the belief on as heard, as sure as
        // the teller times the listener's trust): now the routine uses the thing, and trying makes it sure.
        var knowledge = Quiet.Value.Knowledge;
        var trust = other.Relationships.Get(builder.Id, new Relation(knowledge.RelationshipStart.Affection, knowledge.RelationshipStart.Trust, knowledge.RelationshipStart.Respect)).Trust;
        Assert.True(builder.Beliefs.TryGet(typeId, Made, out var told));
        other.Beliefs.Set(typeId, Made, told.Value, told.Confidence * trust, KnowledgeSources.Heard, builder.Id, host.World.Tick);
        RunUntil(host, () => events.Any(e => e.Kind == InhabitantsSystem.ActionFinished && e.InhabitantId == other.Id && e.Action == Use.Action && e.TargetId == builtId), hour * 6);
        Assert.True(other.Beliefs.TryGet(typeId, Made, out var tried));
        Assert.Equal((true, knowledge.ExperiencedConfidence, KnowledgeSources.Experienced), (tried.Value, tried.Confidence, tried.Source));

        // Who knows the thing by its own experience calls it by its name in what it remembers (GW2 #245),
        // without being its inventor.
        var remembered = other.Memory.Events.Last(e => e.Kind == Use.Action && e.AboutId == typeId);
        Assert.Equal(host.Inventions.Invention(typeId)!.Name.En, host.Inhabitants.LabelOf(other, remembered)!.En);
        Assert.Single(events, e => e.Kind == InventionSystem.FirstUse); // the first use stays the builder's
    }

    [Fact]
    public void ANeedTheRoutineDoesNotSearchForNeverCallsForHelp()
    {
        // A call for help follows futile searching (content/player). The need of the made thing has no
        // search action, so strolling about with it low is no search for it - and no gift could help.
        var host = SimulationHost.CreateEmpty(ShippedDirectory, 45, new SilentPort(), "de");
        var model = Shipped.InhabitantModel;
        var lonely = Person("inh-000001", new Position(0, 0));
        lonely.Needs[Need] = 0.0;
        foreach (var need in model.Needs.Where(n => n.Id != Need))
        {
            lonely.Needs[need.Id] = need.Id == "fun" ? 0.0 : 1.0;
        }

        host.Inhabitants.Add(lonely);
        var events = Record(host);
        host.World.Run(host.World.Calendar.TicksPerDay);
        Assert.True(lonely.Needs[Need] < host.Content.GiftRules.UrgentBelow);
        Assert.True(events.Count(e => e.Kind == InhabitantsSystem.ActionFinished && host.Content.GiftRules.SearchActions.Contains(e.Action!)) >= host.Content.GiftRules.FutileAttemptsThreshold);
        Assert.DoesNotContain(events, e => e.Kind == PlayerSystem.HelpNeeded && e.Detail == Need);
        Assert.DoesNotContain(host.Player.Open, c => c.Need == Need);
    }

    // ------------------------------------------------------------------ save, load, determinism

    [Fact]
    public void SaveAndLoadBetweenBuildingAndFirstUseKeepTheChainGoing()
    {
        var (host, _, typeId, builtId) = Built(46);
        var events = Record(host);
        var path = Path.Combine(_directory, "built.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loaded = Restore(path, host);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        var loadedBuilder = loaded.Inhabitants.All.Single();
        Assert.True(loadedBuilder.Beliefs.TryGet(typeId, Made, out var belief));
        Assert.Equal((true, KnowledgeSources.Experienced), (belief.Value, belief.Source));
        Assert.Empty(loaded.Inventions.FirstUsed);

        var hour = host.World.Calendar.Settings.TicksPerHour;
        RunUntil(host, () => events.Any(e => e.Kind == InventionSystem.FirstUse), hour * 6);
        host.World.Run(hour);
        var loadedEvents = Record(loaded);
        loaded.World.Run(host.World.Tick - loaded.World.Tick);
        var first = Assert.Single(loadedEvents, e => e.Kind == InventionSystem.FirstUse);
        Assert.Equal((loadedBuilder.Id, builtId), (first.InhabitantId, first.TargetId));
        Assert.Equal([typeId], loaded.Inventions.FirstUsed);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
    }

    [Fact]
    public void TheSameSeedGivesTheSameHashAndAReplayFromSeedAndLogsToo()
    {
        var (first, _, _, _) = Built(47);
        var (second, _, _, _) = Built(47);
        var day = first.World.Calendar.TicksPerDay;
        first.World.Run(day);
        second.World.Run(day);
        Assert.NotEmpty(first.Inventions.FirstUsed);
        Assert.Equal(first.World.Tick, second.World.Tick);
        Assert.Equal(first.World.StateHash(), second.World.StateHash());

        var replay = World(new ReplayJobPort(first.Inventions.Log), 47, new ReplayDecisionPort(first.Decisions.Log));
        replay.World.Run(first.World.Tick);
        Assert.Equal(first.Inventions.FirstUsed, replay.Inventions.FirstUsed);
        Assert.Equal(first.World.StateHash(), replay.World.StateHash());
    }

    // ------------------------------------------------------------------ helpers (test data only)

    private static double DistanceFactor(double metres) =>
        1.0 - (Weights.DistanceFalloff * Math.Min(1.0, metres / Weights.PerceptionRadiusM));

    /// <summary>An inhabitant with every need met, moods at rest and middling traits.</summary>
    private static Inhabitant Person(string id, Position at)
    {
        var model = Shipped.InhabitantModel;
        return new Inhabitant(
            id,
            "female",
            1,
            at,
            model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
            model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal),
            model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal),
            model.WillToLive.Start);
    }

    /// <summary>For the brain alone: someone with the need at 0 who believes by experience that the thing one metre away is made.</summary>
    private static (Inhabitant Maker, WorldObject Thing) MakerNextToItsThing()
    {
        var maker = Person("inh-000001", new Position(0, 0));
        maker.Needs[Need] = 0.0;
        var thing = new WorldObject("obj-000001", InventionSystem.TypePrefix + "000000000000", new Position(1, 0));
        maker.Beliefs.Set(thing.TypeId, Made, true, Shipped.Knowledge.ExperiencedConfidence);
        return (maker, thing);
    }

    /// <summary>The chain up to the standing thing: lasting lack, wish, approved invention, plan, building.</summary>
    private static (SimulationHost Host, ScriptedJobPort Port, string TypeId, string BuiltId) Built(ulong seed)
    {
        var port = new ScriptedJobPort();
        var host = World(port, seed);
        var typeId = Invent(host, port);
        AnswerPlan(port, Plan());
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, 3000);
        return (host, port, typeId, host.Inventions.Builds.Single().BuiltObjectId!);
    }

    /// <summary>Runs until the lasting lack has become a wish and answers it with an approved invention.</summary>
    private static string Invent(SimulationHost host, ScriptedJobPort port)
    {
        var trigger = Quiet.Value.Inventions.Settings.Trigger;
        RunUntil(host, () => port.Submitted.Any(j => j.Kind == JobKind.Invention), (trigger.HoursBelow + 2) * host.World.Calendar.Settings.TicksPerHour);

        // (The night's own jobs share the port; they stay unanswered here.)
        var job = Assert.Single(port.Submitted, j => j.Kind == JobKind.Invention);
        Assert.Equal(Need, job.Payload.Invention!.Want);
        port.Answer(job, Approved(job));
        host.World.Step();
        var typeId = InventionSystem.TypeIdFor(job.RequestId);
        Assert.True(host.Types.Contains(typeId));
        return typeId;
    }

    private static SimulationHost World(IJobPort port, ulong seed, IDecisionPort? decisions = null)
    {
        var host = SimulationHost.CreateEmpty(QuietDirectory.Value, seed, decisions ?? new SilentPort(), "de", port);
        var builder = Person("inh-000001", new Position(0, 0));
        builder.Needs[Need] = 0.0;
        host.Inhabitants.Add(builder);
        for (var i = 0; i < NearbyTypes.Length; i++)
        {
            host.Objects.Spawn(NearbyTypes[i], new Position(1 + i, 0));
        }

        return host;
    }

    private static string CopyContentWithAQuietBody()
    {
        var target = Path.Combine(Path.GetTempPath(), "glassworld-use-content-" + Path.GetRandomFileName());
        foreach (var file in Directory.EnumerateFiles(ShippedDirectory, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(ShippedDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        var path = Path.Combine(target, "inhabitants", "model.json");
        var model = ContractSerializer.Deserialize<InhabitantModel>(File.ReadAllText(path));
        foreach (var need in model.Needs.Where(n => n.Id != Need))
        {
            need.DecayPerHour = 0;
        }

        File.WriteAllText(path, ContractSerializer.Serialize(model));
        return target;
    }

    private static List<WorldEvent> Record(SimulationHost host)
    {
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        return events;
    }

    private static void RunUntil(SimulationHost host, Func<bool> done, long maxTicks)
    {
        for (var i = 0L; i < maxTicks && !done(); i++)
        {
            host.World.Step();
        }

        Assert.True(done(), "condition not reached");
    }

    private static void AnswerPlan(ScriptedJobPort port, BuildPlan plan)
    {
        var job = port.Submitted.Last(j => j.Kind == JobKind.Build_plan);
        var example = File.ReadAllText(Path.Combine(TestData.Root, "contracts", "examples", "valid", "job-status.build-plan-bowl.json"));
        var status = ContractSerializer.Deserialize<JobStatus>(example);
        status.JobId = job.RequestId;
        status.Tick = job.Tick;
        status.Result!.BuildPlan = plan;
        port.Answer(job, status);
    }

    /// <summary>Two parts in two steps: the invention's material first, then its step material.</summary>
    private static BuildPlan Plan()
    {
        var kit = Quiet.Value.Inventions.Build.Kit;
        var plan = new BuildPlan();
        for (var i = 0; i < 2; i++)
        {
            plan.Parts.Add(new BuildPart
            {
                Shape = kit.Shapes.ElementAt(i),
                SizeM = new SizeM2 { X = 0.3, Y = 0.2, Z = 0.3 },
                PositionM = new PositionM { X = 0, Y = 0.1 + (0.2 * i), Z = 0 },
                RotationYDeg = 0,
                Material = i == 0 ? NearbyTypes[0] : NearbyTypes[^1],
                Color = kit.Colors.ElementAt(i).Id,
                Step = i + 1,
            });
        }

        return plan;
    }

    private static Invention Invention()
    {
        var example = File.ReadAllText(Path.Combine(TestData.Root, "contracts", "examples", "valid", "job-status.invention-approved.json"));
        var invention = ContractSerializer.Deserialize<JobStatus>(example).Result!.Invention!.Invention!;
        invention.Materials = [NearbyTypes[0]];
        foreach (var step in invention.Steps)
        {
            step.Materials = [NearbyTypes[^1]];
        }

        invention.Introduces = ["container"];
        invention.TechLevel = Quiet.Value.Inventions.Settings.TechLevel;
        invention.SizeM.X = invention.SizeM.Y = invention.SizeM.Z = Quiet.Value.Inventions.Settings.Limits.MaxSizeM / 2;
        return invention;
    }

    private static JobStatus Approved(GenerationJob job)
    {
        var example = File.ReadAllText(Path.Combine(TestData.Root, "contracts", "examples", "valid", "job-status.invention-approved.json"));
        var status = ContractSerializer.Deserialize<JobStatus>(example);
        status.JobId = job.RequestId;
        status.Tick = job.Tick;
        status.Result!.Invention!.Invention = Invention();
        status.Result.Invention.Sound!.Needed = false;
        return status;
    }

    private static SimulationHost Restore(string path, SimulationHost original)
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(QuietDirectory.Value, "decider", "settings.json")));
        var factory = new SimulationHostFactory(ContentSet.Load(QuietDirectory.Value), settings, new ReplayDecisionPort(original.Decisions.Log), "de", new ReplayJobPort(original.Inventions.Log));
        return StateCodec.Restore(factory, SaveGameStore.Load(path));
    }

    /// <summary>A decider that never answers: whatever is done is the routine's choice.</summary>
    private sealed class SilentPort : IDecisionPort
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

        public List<GenerationJob> Submitted { get; } = [];

        public void Submit(string requestId, string jobJson) => Submitted.Add(ContractSerializer.Deserialize<GenerationJob>(jobJson));

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
