using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1726 (D1123 #2): against the cold there are actions without an invention - going into what one believes
/// shelters (a rock niche: dry, out of the wind) and going to someone familiar (others' warmth). The core only
/// offers them, by the inhabitant's beliefs and needs (data: utility, meeting options); the warmth itself comes
/// from the body (shelter, others_warmth), never from an effect the core grants.
/// </summary>
public sealed class WarmthActionsTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);

    [Fact]
    public void TheColdDrawsToWhatOneBelievesShelters()
    {
        var niche = new WorldObject("obj-000002", "rock_niche", new Position(0, 3));
        double Best(double warmth, bool believes)
        {
            var who = new Inhabitant("inh-000001", "female", 2, new Position(0, 0), new Dictionary<string, double> { ["warmth"] = warmth, ["purpose"] = 1.0 }, new Dictionary<string, double>(), new Dictionary<string, double>(), 0.8);
            who.Beliefs.Set("rock_niche", "sheltering", believes, 1.0);
            return Content.Brain.Score(who, [niche]).Where(o => o.Action == "use" && o.TargetId == niche.Id).Select(o => o.Score).DefaultIfEmpty(0).Max();
        }

        Assert.True(Best(0.1, believes: true) > Best(1.0, believes: true), "the cold draws to the niche one believes shelters");
        Assert.Equal(0, Best(0.1, believes: false));

        // Only what it knows shelters (known_only): an unknown thing draws nothing and suggests no use (GW2 D971).
        var who = new Inhabitant("inh-000001", "female", 2, new Position(0, 0), new Dictionary<string, double> { ["warmth"] = 0.1 }, new Dictionary<string, double>(), new Dictionary<string, double>(), 0.8);
        Assert.DoesNotContain(Content.Brain.Score(who, [niche]), o => o.Action == "use");
        Assert.False(Content.Brain.NeedSuggests(who, "use", "clay"));

        // 'use' is still about made things in a free moment (D999) - a natural niche is no made thing.
        Assert.True(Content.Brain.IsAboutMadeThings("use"));
    }

    [Fact]
    public void TheColdDrawsToSomeoneFamiliarAndTheDeciderIsOfferedIt()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        var all = host.Inhabitants.All.ToList();
        var (one, other) = (all[0], all[1]);
        other.Position = new Position(one.Position.X + 8, one.Position.Z);
        var none = new HashSet<string>(StringComparer.Ordinal);

        double Approach(double warmth)
        {
            one.Needs["warmth"] = warmth;
            return host.Content.Brain.Score(one, [], none, null, [other]).Where(o => o.Action == "approach" && o.TargetId == other.Id).Select(o => o.Score).DefaultIfEmpty(0).Max();
        }

        Assert.True(Approach(0.1) > 0, "the cold draws to the partner");
        Assert.Equal(0, Approach(1.0));

        // The decider: approach is offered to the familiar partner while cold, not when warm (strangers as before).
        var knowledge = host.Inhabitants.Knowledge!;
        one.Needs["warmth"] = 0.1;
        Assert.True(InhabitantsSystem.HasPurpose(knowledge, one, other, "approach"));
        one.Needs["warmth"] = 1.0;
        Assert.False(InhabitantsSystem.HasPurpose(knowledge, one, other, "approach"));

        // Close by already, nothing to offer.
        one.Needs["warmth"] = 0.1;
        other.Position = new Position(one.Position.X + 1, one.Position.Z);
        Assert.False(InhabitantsSystem.HasPurpose(knowledge, one, other, "approach"));
    }

    [Fact]
    public void OthersWarmReachAsFarAsAnApproachEnds()
    {
        // Who walks up to someone stops at the action's reach; the others' warmth reaches that far (A1726).
        var reach = Content.Actions.Timing("approach").ReachM;
        Assert.True(Content.Body!.OthersWarmth!.RadiusCm >= reach * 100, "others' warmth reaches the end of an approach");
    }
}
