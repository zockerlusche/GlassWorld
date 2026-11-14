using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// D1017 G1 (B2707): knowledge fades when it is not used - weaker by the day, forgotten below the data's limit (kept,
/// never deleted), as sure as before once it is used again. Every number is read from the data.
/// </summary>
public sealed class ForgettingTests
{
    private const string Thing = "pyrite";
    private const string Property = "hard";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    [Fact]
    public void AHeardBeliefGrowsWeakerByTheDayAndIsForgottenBelowTheLimitButKept()
    {
        var (host, a) = World();
        var forgetting = Content.Value.Knowledge.Forgetting!;
        var decay = forgetting.DecayPerDay[KnowledgeSources.Heard];
        a.Beliefs.Set(Thing, Property, true, 0.8, KnowledgeSources.Heard, null, 0);
        var day = host.World.Calendar.TicksPerDay;

        // The fifth day's start is the step after five days of ticks.
        host.World.Run((day * 5) + 1);
        Assert.True(a.Beliefs.TryGet(Thing, Property, out var weaker));
        Assert.Equal(0.8 * (1 - (decay * 5)), weaker.Confidence, 9);

        // Long unused: below the limit it leaves the mind, and is kept as it was.
        var days = (int)Math.Ceiling((1 - forgetting.ForgottenBelow) / decay) + 1;
        host.World.Run(day * (days - 5));
        Assert.False(a.Beliefs.TryGet(Thing, Property, out _));
        var kept = Assert.Single(host.Forgetting.Forgotten, f => f.InhabitantId == a.Id && f.TypeId == Thing);
        Assert.Equal((true, 0.8, KnowledgeSources.Heard), (kept.Value, kept.Confidence, kept.Source));
    }

    [Fact]
    public void ActingOnAThingOfItsKindMakesAFadingBeliefAsSureAsBefore()
    {
        var (host, a) = World();
        a.Beliefs.Set(Thing, Property, true, 0.8, KnowledgeSources.Heard, null, 0);
        host.World.Run(host.World.Calendar.TicksPerDay * 3);
        Assert.True(a.Beliefs.TryGet(Thing, Property, out var weaker) && weaker.Confidence < 0.8);

        host.Inhabitants.Report(new WorldEvent(InhabitantsSystem.ActionFinished, host.World.Tick, a.Id, "inspect", null) { TargetType = Thing });
        Assert.True(a.Beliefs.TryGet(Thing, Property, out var again));
        Assert.Equal(0.8, again.Confidence, 9);

        // Used just now: the next day it has hardly faded.
        host.World.Run(host.World.Calendar.TicksPerDay);
        Assert.True(a.Beliefs.TryGet(Thing, Property, out var next) && next.Confidence > weaker.Confidence);
    }

    [Fact]
    public void FadingAndForgottenKnowledgeSurviveSavingAndLoadingWithTheSameHash()
    {
        var (host, a) = World();
        a.Beliefs.Set(Thing, Property, true, 0.8, KnowledgeSources.Heard, null, 0);
        a.Beliefs.Set("flint", Property, true, 0.5, KnowledgeSources.Guessed, null, 0);
        host.World.Run(host.World.Calendar.TicksPerDay * 20);
        Assert.True(host.Forgetting.HasState);

        var path = Path.Combine(Path.GetTempPath(), "gw-forgetting-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var loaded = StateCodec.Restore(Factory(), SaveGameStore.Load(path));
            Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
            Assert.Equal(host.Forgetting.Forgotten.Count, loaded.Forgetting.Forgotten.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ASourceTheDataDoNotListNeverFades()
    {
        var (host, a) = World();
        var unlisted = "unlisted_source";
        Assert.DoesNotContain(unlisted, Content.Value.Knowledge.Forgetting!.DecayPerDay.Keys);
        a.Beliefs.Set(Thing, Property, true, 0.8, unlisted, null, 0);
        host.World.Run(host.World.Calendar.TicksPerDay * 10);
        Assert.True(a.Beliefs.TryGet(Thing, Property, out var same));
        Assert.Equal(0.8, same.Confidence, 12);
    }

    /// <summary>One inhabitant, busy with nothing, in an empty world (nothing to act on, nothing new to learn).</summary>
    private static (SimulationHost Host, Inhabitant A) World()
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 31, new MockDecisionPort(), "de");
        var model = Content.Value.InhabitantModel;
        var a = new Inhabitant(
            "inh-000001",
            "female",
            1,
            new Position(0, 0),
            model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
            model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal),
            model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal),
            model.WillToLive.Start);
        host.Inhabitants.Add(a);
        a.Activity = new Activity("idle", null, a.Position, 100_000_000);
        return (host, a);
    }

    private static SimulationHostFactory Factory()
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new MockDecisionPort(), "de");
    }
}
