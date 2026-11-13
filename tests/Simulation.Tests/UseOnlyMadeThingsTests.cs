using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// GW2 D971 (a): 'use' is for made things. What a thing is by being made (the invention data's implied
/// properties) is not assumed for a thing the inhabitant knows nothing about, so using a found thing is neither
/// offered nor scored; once it knows a thing is made, it is.
/// </summary>
public sealed class UseOnlyMadeThingsTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    [Fact]
    public void AThingNotKnownToBeMadeIsNotOfferedForUseAKnownMadeThingIs()
    {
        var content = Content.Value;
        var brain = content.Brain;
        var made = Assert.Single(content.Inventions.Settings.ImpliedProperties);
        Assert.Contains(made, (IEnumerable<string>)brain.MadeProperties);
        var use = content.Inventions.Build.Settings.UseAction;
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 1, new MockDecisionPort(), "de");
        var inhabitant = host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).First();
        foreach (var need in inhabitant.Needs.Keys.ToList())
        {
            inhabitant.Needs[need] = 0.1;
        }

        // A found thing it knows nothing about: not made as far as it knows.
        const string Found = "clay";
        Assert.False(inhabitant.Beliefs.TryGet(Found, made, out _));
        Assert.Equal(0.0, brain.BeliefFactor(inhabitant, Found, made));
        Assert.False(brain.NeedSuggests(inhabitant, use, Found));

        // Another property unknown keeps the unknown-target belief (only what a made thing is changes).
        Assert.Equal(brain.Weights.UnknownTargetBelief, brain.BeliefFactor(inhabitant, Found, "edible"));

        // Once it believes the thing is made, using it is suggested.
        inhabitant.Beliefs.Set(Found, made, true, 1.0, KnowledgeSources.Experienced, null, host.World.Tick);
        Assert.True(brain.NeedSuggests(inhabitant, use, Found));
        Assert.Equal(1.0, brain.BeliefFactor(inhabitant, Found, made));
    }
}
