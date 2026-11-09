using System.Text.Json;
using System.Text.Json.Nodes;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// TL #19921: curiosity drives looking at unknown things when the needs are met; looking teaches what can be
/// seen (kind, look), guesses and where the thing stands - never the effect.
/// </summary>
public sealed class CuriosityTests : IDisposable
{
    private const string Curiosity = "curiosity";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);
    private static readonly DeciderSettings Settings = ContractSerializer.Deserialize<DeciderSettings>(
        File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));

    private static readonly KnowledgeLooking Looking = Content.Knowledge.Looking!;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public CuriosityTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ASatedCuriousInhabitantLooksAtAnUnknownThingAndAnIncuriousOneDoesNot()
    {
        var w = NewWorld();
        var curious = Sated(w.Inhabitants.Get("inh-000001"), 0.9);
        var incurious = Sated(w.Inhabitants.Get("inh-000002"), 0.2);
        var flint = w.Objects.Spawn("flint", new Position(2, 2));
        var inspected = new List<WorldEvent>();
        w.Inhabitants.Reported += e =>
        {
            if (e.Kind == InhabitantsSystem.ActionFinished && e.Action == Looking.Action && e.TargetId == flint.Id)
            {
                inspected.Add(e);
            }
        };

        RunUntil(w.World, () => false, 120);

        Assert.Contains(inspected, e => e.InhabitantId == curious.Id);
        Assert.DoesNotContain(inspected, e => e.InhabitantId == incurious.Id);
        Assert.True(curious.Beliefs.TryGet("flint", "stone", out var stone) && stone.Value);
        Assert.Equal(Looking.InspectConfidence, stone.Confidence);
        Assert.True(curious.Beliefs.TryGet("flint", "sharp", out var sharp) && sharp.Value);
        Assert.True(curious.Beliefs.TryGet("flint", "fruit", out var fruit) && !fruit.Value);
        Assert.Equal("flint", curious.Places[flint.Id].TypeId);

        // Once looked at, the thing is no longer new: no more looking at it.
        Assert.DoesNotContain(Content.Brain.Score(curious, [flint]), o => o.Action == Looking.Action && o.TargetId == flint.Id);
    }

    [Fact]
    public void LookingTeachesTheKindAndGuessesButNeverTheEffect()
    {
        var w = NewWorld();
        var looker = Sated(w.Inhabitants.Get("inh-000001"), 0.9);
        foreach (var type in new[] { "red_berry", "sea_water" })
        {
            var thing = w.Objects.Spawn(type, new Position(1, 0));
            looker.Activity = new Activity(Looking.Action, thing.Id, thing.Position, null);
            RunUntil(w.World, () => looker.Activity is null);
        }

        // Looks like fruit, so it might be edible - a guess; that it is poisonous is not seen.
        Assert.True(looker.Beliefs.TryGet("red_berry", "fruit", out var fruit) && fruit.Value && fruit.Source == KnowledgeSources.Seen);
        Assert.True(looker.Beliefs.TryGet("red_berry", "edible", out var edible) && edible.Value);
        Assert.Equal((KnowledgeSources.Guessed, Looking.GuessedConfidence), (edible.Source, edible.Confidence));
        Assert.False(looker.Beliefs.TryGet("red_berry", "poisonous", out _));

        // Looks like water, so it might be drinkable - that it is salty is learned only by drinking.
        Assert.True(looker.Beliefs.TryGet("sea_water", "drinkable", out var drinkable) && drinkable.Source == KnowledgeSources.Guessed);
        Assert.False(looker.Beliefs.TryGet("sea_water", "salty", out _));

        // No property an action's effect depends on is ever learned as seen.
        var effects = Content.Actions.Actions.SelectMany(Content.Actions.Rules).Select(r => r.RequiresProperty).OfType<string>().ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(looker.Beliefs.All, b => effects.Contains(b.PropertyId) && b.Belief.Source != KnowledgeSources.Guessed);
    }

    [Fact]
    public void AGuessNeverReplacesWhatTheInhabitantAlreadyBelieves()
    {
        var w = NewWorld();
        var looker = Sated(w.Inhabitants.Get("inh-000001"), 0.9);
        looker.Beliefs.Set("apple", "edible", false, 0.7, KnowledgeSources.Heard, "inh-000002", 1);
        var apple = w.Objects.Spawn("apple", new Position(1, 0));
        looker.Activity = new Activity(Looking.Action, apple.Id, apple.Position, null);
        RunUntil(w.World, () => looker.Activity is null);

        Assert.True(looker.Beliefs.TryGet("apple", "edible", out var edible));
        Assert.Equal((false, KnowledgeSources.Heard), (edible.Value, edible.Source));
    }

    [Fact]
    public void LookingNeverOverridesWhatWasTriedOrIsSurer()
    {
        var w = NewWorld();
        var looker = Sated(w.Inhabitants.Get("inh-000001"), 0.9);
        looker.Beliefs.Set("flint", "stone", false, 0.5, KnowledgeSources.Experienced, null, 1);
        looker.Beliefs.Set("flint", "sharp", false, 0.9, KnowledgeSources.Heard, "inh-000002", 1);
        looker.Beliefs.Set("flint", "hard", false, 0.3, KnowledgeSources.Heard, "inh-000002", 1);
        var flint = w.Objects.Spawn("flint", new Position(1, 0));
        looker.Activity = new Activity(Looking.Action, flint.Id, flint.Position, null);
        RunUntil(w.World, () => looker.Activity is null);

        Assert.True(looker.Beliefs.TryGet("flint", "stone", out var stone) && stone.Source == KnowledgeSources.Experienced && !stone.Value);
        Assert.True(looker.Beliefs.TryGet("flint", "sharp", out var sharp) && sharp.Source == KnowledgeSources.Heard && !sharp.Value);
        Assert.True(looker.Beliefs.TryGet("flint", "hard", out var hard) && hard.Value);
        Assert.Equal((KnowledgeSources.Seen, Looking.InspectConfidence), (hard.Source, hard.Confidence));

        // What was seen goes to the decider as seen, not as tried.
        var state = InhabitantStates.Of(looker, Content.Knowledge, ["flint"], []);
        Assert.Equal(KnowledgeSource.Seen, Assert.Single(state.Knowledge, k => k.Property == "hard").Source);
    }

    [Fact]
    public void AThirstyCuriousInhabitantDrinksFirstAndANeglectedNeedSilencesCuriosity()
    {
        var w = NewWorld();
        var inhabitant = Sated(w.Inhabitants.Get("inh-000001"), 0.9);
        inhabitant.Beliefs.Set("spring_water", "drinkable", true, 1.0, KnowledgeSources.Experienced, null, 1);
        var water = w.Objects.Spawn("spring_water", new Position(30, 0));
        var flint = w.Objects.Spawn("flint", new Position(1, 0));

        inhabitant.Needs["thirst"] = 0.4;
        Assert.Equal(("drink", water.Id), Best(Content.Brain, inhabitant, [water, flint]));

        inhabitant.Needs["thirst"] = 0.1;
        Assert.Equal(0.0, Content.Brain.Calm(inhabitant));
        Assert.DoesNotContain(Content.Brain.Score(inhabitant, [water, flint]), o => o.Action == Looking.Action && o.TargetId is not null);

        inhabitant.Needs["thirst"] = 1.0;
        Assert.Equal((Looking.Action, flint.Id), Best(Content.Brain, inhabitant, [water, flint]));
    }

    [Fact]
    public void TheDataDecidesHowCuriosityDrivesLooking()
    {
        var w = NewWorld();
        var inhabitant = Sated(w.Inhabitants.Get("inh-000001"), 0.9);
        var flint = w.Objects.Spawn("flint", new Position(1, 0));
        Assert.Equal((Looking.Action, flint.Id), Best(Content.Brain, inhabitant, [flint]));

        var timid = Clone(Content.Brain.Weights);
        timid.Considerations.Single(c => c.NoveltyCurve is not null).Weight = 0.05;
        Assert.Equal(("idle", null), Best(Brain(timid), inhabitant, [flint]));

        var otherTrait = Clone(Content.Brain.Weights);
        otherTrait.Considerations.Single(c => c.NoveltyCurve is not null).Trait = "patience";
        inhabitant.Traits["patience"] = 0.0;
        Assert.Equal(("idle", null), Best(Brain(otherTrait), inhabitant, [flint]));

        var broken = Clone(Content.Brain.Weights);
        broken.Considerations.Single(c => c.NoveltyCurve is not null).TargetProperty = "edible";
        Assert.Throws<InvalidDataException>(() => Brain(broken));
    }

    [Fact]
    public void TheDeciderIsOfferedLookingOnlyWhileItIsCuriousAboutTheThing()
    {
        var w = NewWorld();
        var inhabitant = Sated(w.Inhabitants.Get("inh-000001"), 0.9);
        Assert.True(Content.Brain.NeedSuggests(inhabitant, Looking.Action, "flint"));

        inhabitant.Traits[Curiosity] = 0.0;
        inhabitant.Needs["fun"] = 1.0;
        Assert.False(Content.Brain.NeedSuggests(inhabitant, Looking.Action, "flint"));

        inhabitant.Traits[Curiosity] = 0.9;
        inhabitant.Beliefs.Set("flint", "stone", true, Looking.InspectConfidence, KnowledgeSources.Experienced, null, 1);
        Assert.False(Content.Brain.NeedSuggests(inhabitant, Looking.Action, "flint"));
    }

    [Fact]
    public void TheQuestionMarkGoesOnceOnlyTheEffectIsOpen()
    {
        var w = NewWorld();
        var looker = Sated(w.Inhabitants.Get("inh-000001"), 0.0);
        var apple = w.Objects.Spawn("apple", new Position(1, 0));
        w.World.Step();
        var question = Assert.Single(w.Decisions.OpenQuestionIds(looker.Id));
        Assert.Equal([question], w.Decisions.PuzzlingQuestionIds(looker));

        looker.Activity = new Activity(Looking.Action, apple.Id, apple.Position, null);
        RunUntil(w.World, () => looker.Activity is null);

        Assert.Single(w.Decisions.OpenQuestionIds(looker.Id));
        Assert.Empty(w.Decisions.PuzzlingQuestionIds(looker));
    }

    [Fact]
    public void PlacesAreBoundedHashedSavedAndGoToTheDecider()
    {
        var knowledge = Clone(Content.Knowledge);
        knowledge.Looking!.PlaceMemoryMax = 1;
        var w = NewWorld(knowledge);
        var looker = Sated(w.Inhabitants.Get("inh-000001"), 0.0);
        foreach (var (type, x) in new[] { ("apple", 1.0), ("flint", 2.0) })
        {
            var thing = w.Objects.Spawn(type, new Position(x, 0));
            looker.Activity = new Activity(Looking.Action, thing.Id, thing.Position, null);
            RunUntil(w.World, () => looker.Activity is null);
        }

        var place = Assert.Single(looker.Places);
        Assert.Equal("flint", place.Value.TypeId);

        var state = InhabitantStates.Of(looker, knowledge, ["flint"], []);
        var stone = Assert.Single(state.Knowledge, k => k.Property == "stone");
        Assert.Equal((place.Key, 2.0), (stone.Object, stone.Place!.XM));
        Assert.DoesNotContain(state.Knowledge, k => !k.Value);

        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 3, new MockDecisionPort(), "de");
        var before = host.World.StateHash();
        host.Inhabitants.Get("inh-000001").RememberPlace("obj-000001", new PlaceMemory("apple", new Position(1, 2), 5), 4);
        Assert.NotEqual(before, host.World.StateHash());
        var path = Path.Combine(_directory, "places.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loaded = StateCodec.Restore(new SimulationHostFactory(Content, Settings, new MockDecisionPort(), "de"), SaveGameStore.Load(path));
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(new PlaceMemory("apple", new Position(1, 2), 5), loaded.Inhabitants.Get("inh-000001").Places["obj-000001"]);
    }

    [Fact]
    public void AnEffectCannotBeMadeVisible()
    {
        var copy = Path.Combine(_directory, "content");
        foreach (var file in Directory.GetFiles(ContentDirectory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(ContentDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        var path = Path.Combine(copy, "knowledge", "settings.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["looking"]!["visible_properties"]!.AsArray().Add("poisonous");
        File.WriteAllText(path, json.ToJsonString());
        var error = Assert.Throws<InvalidDataException>(() => ContentSet.Load(copy));
        Assert.Contains("visible property 'poisonous' is an effect", error.Message, StringComparison.Ordinal);
    }

    private static Inhabitant Sated(Inhabitant inhabitant, double curiosity)
    {
        foreach (var need in inhabitant.Needs.Keys.ToList())
        {
            inhabitant.Needs[need] = 1.0;
        }

        inhabitant.Traits[Curiosity] = curiosity;
        return inhabitant;
    }

    private static (string Action, string? TargetId) Best(UtilityBrain brain, Inhabitant inhabitant, IReadOnlyList<WorldObject> perceived)
    {
        var best = brain.Score(inhabitant, perceived)[0];
        return (best.Action, best.TargetId);
    }

    private static UtilityBrain Brain(UtilityWeights weights) =>
        new(weights, Content.Actions, Content.InhabitantModel.Needs.Select(n => n.Id).ToHashSet(StringComparer.Ordinal), Content.Objects.PropertyVocabulary, null, null,
            Content.InhabitantModel.Needs.Where(n => n.NeglectBelow > 0).ToDictionary(n => n.Id, n => n.NeglectBelow, StringComparer.Ordinal));

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    private static (World World, WorldObjects Objects, InhabitantsSystem Inhabitants, DecisionsSystem Decisions) NewWorld(KnowledgeSettings? knowledge = null)
    {
        knowledge ??= Content.Knowledge;
        var world = new World(1, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = knowledge,
            EventKinds = Content.EventKinds,
        };
        var decisions = new DecisionsSystem(Settings, inhabitants, objects, Content.Actions, Content.Brain, Content.Vocabularies[VocabularyKind.Action], new SilentPort(), "de")
        {
            Knowledge = knowledge,
        };
        inhabitants.QuestionOpen = decisions.HasOpenQuestion;
        world.Add(objects).Add(decisions).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [new Position(0, 0), new Position(4, 0), new Position(1000, 0), new Position(0, 1000)]);
        return (world, objects, inhabitants, decisions);
    }

    private static void RunUntil(World world, Func<bool> done, int maxTicks = 5_000)
    {
        for (var i = 0; i < maxTicks && !done(); i++)
        {
            world.Step();
        }
    }

    /// <summary>A decider that never answers.</summary>
    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
