using GlassWorld.Contracts;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

public sealed class ObjectModelTests
{
    private static readonly ContentSet Content = ContentSet.Load(Path.Combine(TestData.Root, "content"));

    [Fact]
    public void TruthComesFromTheCatalog()
    {
        Assert.True(Content.Objects.HasProperty("red_berry", "poisonous"));
        Assert.False(Content.Objects.HasProperty("red_berry", "edible"));
        Assert.True(Content.Objects.HasProperty("apple", "edible"));
        Assert.Throws<ArgumentException>(() => Content.Objects.HasProperty("apple", "tasty"));
        Assert.Throws<KeyNotFoundException>(() => Content.Objects.TrueProperties("unicorn"));
    }

    [Fact]
    public void BeliefCanDifferFromTruth()
    {
        var beliefs = new BeliefSet();
        beliefs.Set("red_berry", "edible", value: true, confidence: 0.6);

        Assert.True(beliefs.TryGet("red_berry", "edible", out var belief));
        Assert.True(belief.Value);
        Assert.False(Content.Objects.HasProperty("red_berry", "edible"));
        Assert.False(beliefs.TryGet("red_berry", "poisonous", out _));
    }

    [Fact]
    public void BeliefConfidenceIsClampedAndHashed()
    {
        var a = new BeliefSet();
        var b = new BeliefSet();
        a.Set("apple", "edible", true, 1.7);
        b.Set("apple", "edible", true, 1.0);
        Assert.True(a.TryGet("apple", "edible", out var belief));
        Assert.Equal(1.0, belief.Confidence);
        Assert.Equal(Hash(a), Hash(b));

        b.Set("apple", "edible", true, 0.5);
        Assert.NotEqual(Hash(a), Hash(b));
    }

    [Fact]
    public void ObjectIdsAreSequentialAndNeverReused()
    {
        var objects = new WorldObjects(Content.Objects);
        var first = objects.Spawn("apple", new Position(1, 2));
        var second = objects.Spawn("flint", new Position(3, 4));
        Assert.Equal(("obj-000001", "obj-000002"), (first.Id, second.Id));

        Assert.True(objects.Remove(first.Id));
        Assert.Equal("obj-000003", objects.Spawn("apple", new Position(0, 0)).Id);
        Assert.Throws<ArgumentException>(() => objects.Spawn("unicorn", new Position(0, 0)));
    }

    [Fact]
    public void ObjectsAreInTheWorldHash()
    {
        var world = new World(1, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        world.Add(objects);
        var empty = world.StateHash();
        objects.Spawn("apple", new Position(1, 1));
        Assert.NotEqual(empty, world.StateHash());
    }

    [Theory]
    [InlineData("flint", "pyrite")]
    [InlineData("pyrite", "flint")]
    public void PyriteAndFlintMakeASparkInAnyOrder(string a, string b)
    {
        // TL #18657 / B2514 FT4: pyrite is the spark partner of flint.
        var matches = Content.Combinations.Match("combine", [a, b]);
        var match = Assert.Single(matches);
        Assert.Equal("spark_from_pyrite_and_flint", match.Rule.Id);
        Assert.Equal(["spark"], match.Rule.Creates);
    }

    [Theory]
    [InlineData("flint", "flint")]
    [InlineData("pyrite", "pyrite")]
    public void TwoEqualStonesMakeNoSpark(string a, string b) =>
        Assert.Empty(Content.Combinations.Match("combine", [a, b]));

    [Fact]
    public void UnfittingObjectsOrActionsDoNotCombine()
    {
        Assert.Empty(Content.Combinations.Match("combine", ["flint", "apple"]));
        Assert.Empty(Content.Combinations.Match("eat", ["flint", "pyrite"]));
        Assert.Empty(Content.Combinations.Match("combine", ["flint"]));
    }

    [Fact]
    public void NewObjectTypeTakesPartWithoutCodeChange()
    {
        // Frame, not outcome: a type added only in data matches existing rules through its properties.
        var catalog = ObjectCatalog.Create(
            [Types(("steel_bar", ["hard", "sparks", "malleable"]))],
            Content.Vocabularies[VocabularyKind.Property]);
        var rules = CombinationRuleSet.Create(
            [new CombinationRules
            {
                ContractVersion = ContractInfo.Version,
                Rules = [new CombinationRule { Id = "r", Action = "combine", Inputs = [Slot("sparks", "hard"), Slot("sparks", "hard")], Creates = [], Consumes = [] }],
            }],
            catalog,
            Content.Vocabularies[VocabularyKind.Action]);

        Assert.Single(rules.Match("combine", ["steel_bar", "steel_bar"]));
    }

    [Fact]
    public void CrossFileErrorsAreRejected()
    {
        var properties = Content.Vocabularies[VocabularyKind.Property];
        var actions = Content.Vocabularies[VocabularyKind.Action];

        Assert.Throws<InvalidDataException>(() => ObjectCatalog.Create([Types(("x", ["tasty"]))], properties));
        Assert.Throws<InvalidDataException>(() => ObjectCatalog.Create([Types(("x", ["hard"]), ("x", ["sharp"]))], properties));
        Assert.Throws<InvalidDataException>(() => ObjectCatalog.Create([Types(("x", ["hard"]))], actions));

        var catalog = ObjectCatalog.Create([Types(("x", ["hard"]))], properties);
        Assert.Throws<InvalidDataException>(() => Rules(catalog, actions, new CombinationRule { Id = "r", Action = "juggle", Inputs = [Slot("hard")], Creates = [], Consumes = [] }));
        Assert.Throws<InvalidDataException>(() => Rules(catalog, actions, new CombinationRule { Id = "r", Action = "combine", Inputs = [Slot("hard")], Creates = ["unicorn"], Consumes = [] }));
        Assert.Throws<InvalidDataException>(() => Rules(catalog, actions, new CombinationRule { Id = "r", Action = "combine", Inputs = [Slot("hard")], Creates = [], Consumes = [3] }));
    }

    private static ObjectTypes Types(params (string Id, string[] Properties)[] types) => new()
    {
        ContractVersion = ContractInfo.Version,
        Types = [.. types.Select(t => new ObjectType { Id = t.Id, Description = t.Id, Properties = [.. t.Properties] })],
    };

    private static InputSlot Slot(params string[] properties) => new() { Properties = [.. properties] };

    private static CombinationRuleSet Rules(ObjectCatalog catalog, Vocabulary actions, CombinationRule rule) =>
        CombinationRuleSet.Create([new CombinationRules { ContractVersion = ContractInfo.Version, Rules = [rule] }], catalog, actions);

    private static string Hash(BeliefSet beliefs)
    {
        using var hasher = new StateHasher();
        beliefs.WriteState(hasher);
        return hasher.Finish();
    }
}
