using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B2668 (TL, B2616): the kit of base functions as data. Using a made thing with a true property of the kit fills a
/// need (sheltering: safety, insulating and hot: warmth); a natural thing with the same property does not (requires_also
/// crafted - natural laws come in B5). The routine offers such a thing for that need only when the inhabitant believes
/// it is made. Values are read from the data.
/// </summary>
public sealed class BaseFunctionTests
{
    private static readonly ContentSet Content = ContentSet.Load(Path.Combine(TestData.Root, "content"));
    private const string Made = "crafted";
    private const string Shelter = InventionSystem.TypePrefix + "00000000a001";

    [Fact]
    public void TheKitIsDataAndServesOnlyMadeThings()
    {
        var kit = Content.Actions.Rules("use").Where(r => r.RequiresAlso is not null).ToList();
        Assert.Equal(["hot", "insulating", "sheltering"], kit.Select(r => r.RequiresProperty).Order(StringComparer.Ordinal));
        Assert.All(kit, r => Assert.Equal(Made, r.RequiresAlso));

        var types = new WorldTypeCatalog(Content.Objects);
        types.Register(Shelter, [Made, "sheltering"]);
        var rules = Content.Actions.WithTruth(types);
        Assert.Contains(rules.Applying("use", Shelter).SelectMany(r => r.Effects), e => e.Kind == EffectKind.Need && e.Id == "safety" && e.Delta > 0);

        // A rock niche shelters too, but it is not made: the kit leaves it alone.
        Assert.True(types.HasProperty("rock_niche", "sheltering"));
        Assert.DoesNotContain(rules.Applying("use", "rock_niche").SelectMany(r => r.Effects), e => e.Id == "safety");
    }

    [Fact]
    public void TheRoutineOffersAMadeShelterForSafetyButNeverANaturalOne()
    {
        var shelter = new WorldObject("obj-000001", Shelter, new Position(1, 0));
        var niche = new WorldObject("obj-000002", "rock_niche", new Position(0, 1));
        double Best(double safety, WorldObject thing)
        {
            var who = new Inhabitant("inh-000001", "female", 2, new Position(0, 0), new Dictionary<string, double> { ["safety"] = safety, ["purpose"] = 1.0 }, new Dictionary<string, double>(), new Dictionary<string, double>(), 0.8);
            who.Beliefs.Set(Shelter, Made, true, 1.0);
            who.Beliefs.Set(Shelter, "sheltering", true, 1.0);
            who.Beliefs.Set("rock_niche", "sheltering", true, 1.0);
            return Content.Brain.Score(who, [shelter, niche]).Where(o => o.Action == "use" && o.TargetId == thing.Id).Select(o => o.Score).DefaultIfEmpty(0).Max();
        }

        Assert.True(Best(0.1, shelter) > Best(1.0, shelter), "low safety draws to the made shelter");
        // Low safety never draws to the natural niche (trying out made things gives way to the need instead).
        Assert.True(Best(0.1, niche) <= Best(1.0, niche));
    }

    [Fact]
    public void RequiresAlsoNeedsATargetPropertyAndNoSearch()
    {
        var weights = ContractSerializer.Deserialize<UtilityWeights>(File.ReadAllText(Path.Combine(TestData.Root, "content", "behaviour", "utility.json")));
        var kit = weights.Considerations.First(c => c.RequiresAlso is not null);
        kit.SearchAction = "wander";
        Assert.Throws<InvalidDataException>(() => _ = new Behaviour.UtilityBrain(weights, Content.Actions, Content.InhabitantModel.Needs.Select(n => n.Id).ToHashSet(StringComparer.Ordinal), Content.Objects.PropertyVocabulary));
    }
}
