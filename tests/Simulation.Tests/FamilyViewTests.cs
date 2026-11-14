using GlassWorld.Contracts;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B9-1b (R26/R29/R30, D1049 v2): the interface families build on. The words of the family options are in the vocabulary
/// but not offered until a step makes them executable; the read-only family view answers for the first generation.
/// </summary>
public sealed class FamilyViewTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    [Fact]
    public void TheFirstGenerationStandsAloneInItsTreeBornBeforeTheWorld()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 9, new MockDecisionPort(), "de");
        host.World.Step();
        foreach (var inhabitant in host.Inhabitants.All)
        {
            var node = Assert.Single(host.FamilyView.Tree(inhabitant.Id, 3));
            Assert.Equal((inhabitant.Id, inhabitant.LifeStage), (node.Id, node.LifeStage));
            Assert.True(node.BornDay < 0);
            Assert.Null(node.DiedDay);
            Assert.Empty(node.Parents);
            Assert.Empty(host.FamilyView.FamilyOf(inhabitant.Id));
            Assert.Null(host.FamilyView.ReputationOf(inhabitant.Id));
        }

        Assert.Empty(host.FamilyView.Tree("nobody", 1));
    }

    [Fact]
    public void FamilyWordsAreInTheVocabularyButNotOfferedYet()
    {
        // R26: no word in the contract the core cannot carry out - an action is offered only once a step gives it a timing.
        var actions = Content.Value.Vocabularies[VocabularyKind.Action].Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        string[] family = ["court", "propose_partnership", "wish_child", "care", "bury", "name_person", "sail"];
        Assert.All(family, a => Assert.Contains(a, actions));
        Assert.DoesNotContain(Content.Value.Actions.OfferedActions, a => family.Contains(a));
        Assert.All(family, a => Assert.True(Content.Value.Vocabularies[VocabularyKind.Action].Entries.Single(e => e.Id == a).Reserved == true));
        Assert.DoesNotContain(family, a => Content.Value.Inventions.Actions.Contains(a));
    }
}
