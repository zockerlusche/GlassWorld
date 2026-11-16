using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1744 (B2790 b): whoever is under a shelter while it rains (or while it feels cold) is drawn less to what leads out
/// of it - a thing or someone outside, a walk away - by the body data's factor; what stays inside keeps its weight.
/// A weight, never a ban; without rain or cold, or outside a shelter, nothing changes.
/// </summary>
public sealed class StayShelteredTests
{
    private static readonly ContentSet Content = ContentSet.Load(Path.Combine(TestData.Root, "content"));

    [Fact]
    public void InTheRainUnderAShelterWhatLeadsOutWeighsLess()
    {
        var stay = Content.Body!.StaySheltered!;
        Assert.True(stay.LeavingFactorPermille < 1000, "content: leaving weighs less");
        var factor = stay.LeavingFactorPermille / 1000.0;

        var air = new Air(150, true);
        var world = new World(3, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Content.Knowledge,
            AirAt = (_, _) => air,
            StaySheltered = stay,
            ShelteredAt = at => at.X < 10,
        };
        inhabitants.Sheltered = inhabitant => inhabitants.ShelteredAt!(inhabitant.Position);
        world.Add(objects).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [new(1, 1), new(50, 1), new(60, 60), new(70, 70)]);
        var (one, outside) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        var inside = objects.Spawn("fieldstone", new Position(2, 2));
        var away = objects.Spawn("fieldstone", new Position(40, 2));

        List<Candidate> Weighed(Inhabitant who)
        {
            var options = new List<Candidate>
            {
                new("inspect", inside.Id, 1.0),
                new("inspect", away.Id, 1.0),
                new("approach", outside.Id, 1.0),
                new("wander", null, 1.0),
                new("idle", null, 1.0),
            };
            inhabitants.WeighLeavingShelter(world, who, options);
            return options;
        }

        // Under the shelter in the rain: the thing inside, the idle here keep their weight; out costs.
        Assert.Equal([1.0, factor, factor, factor, 1.0], Weighed(one).Select(o => o.Score));

        // What serves a neglected need keeps its weight (review GW1 #480): food outside for the starving.
        var hunger = Content.InhabitantModel.Needs.First(n => n.Id == "hunger");
        one.Needs["hunger"] = hunger.NeglectBelow / 2;
        var eat = new List<Candidate> { new("eat", away.Id, 1.0), new("inspect", away.Id, 1.0) };
        inhabitants.WeighLeavingShelter(world, one, eat);
        Assert.Equal([1.0, factor], eat.Select(o => o.Score));
        one.Needs["hunger"] = 1.0;

        // Outside the shelter nothing changes; nor under it when it is dry and not cold.
        Assert.All(Weighed(outside), o => Assert.Equal(1.0, o.Score));
        air = new Air(200, false);
        Assert.All(Weighed(one), o => Assert.Equal(1.0, o.Score));
    }

    [Fact]
    public void TakingShelterLastsWhileTheRainLastsAndEndsWhenANeedPresses()
    {
        // A1793 (D1192 #7, Marcel 15a: 'rock niche used' over and over): the sheltering action goes on as one action while
        // it rains (or feels cold) there - not in ten-minute pieces; a need it does not serve below the line ends it.
        var stay = Content.Body!.StaySheltered!;
        Assert.Equal("use", stay.HoldsAction);
        var line = stay.HoldWhileNeedsAbove!.Value;

        var air = new Air(150, true);
        var world = new World(3, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Content.Knowledge,
            AirAt = (_, _) => air,
            StaySheltered = stay,
            ShelteredAt = at => at.X < 10,
            ShelterProperty = Content.Body.Wetness.ShelterProperty,
        };
        inhabitants.Sheltered = inhabitant => inhabitants.ShelteredAt!(inhabitant.Position);
        world.Add(objects).Add(inhabitants);

        // Midday, far from bedtime (bedtime ends it as well).
        world.Run(12L * world.Calendar.Settings.TicksPerHour);
        Assert.False(inhabitants.AtBedtime(world));
        inhabitants.SpawnStartInhabitants(world, [new(1, 1), new(50, 1), new(60, 60), new(70, 70)]);
        var (one, outside) = (inhabitants.Get("inh-000001"), inhabitants.Get("inh-000002"));
        foreach (var need in one.Needs.Keys.ToList())
        {
            one.Needs[need] = 1.0;
        }

        var niche = objects.Spawn("rock_niche", new Position(2, 1));
        var use = new Activity("use", niche.Id, niche.Position, null);

        // In the rain under the shelter it lasts.
        Assert.True(inhabitants.KeepsSheltering(world, one, use, niche));

        // Not another thing lying under the shelter (review GW1 #528: a cup in the niche), not another action, not
        // out in the open, not once it is dry and warm.
        var cup = objects.Spawn("fieldstone", new Position(2, 2));
        Assert.False(inhabitants.KeepsSheltering(world, one, use with { TargetId = cup.Id }, cup));
        Assert.False(inhabitants.KeepsSheltering(world, one, use with { Action = "inspect" }, niche));
        Assert.False(inhabitants.KeepsSheltering(world, outside, use, niche));
        air = new Air(250, false);
        Assert.False(inhabitants.KeepsSheltering(world, one, use, niche));

        // Dry, but the need it is taken for is still low (feeling cool is not yet cold): it lasts.
        var hold = stay.HoldWhile!;
        one.Needs[hold.Need] = hold.Below / 2;
        Assert.True(inhabitants.KeepsSheltering(world, one, use, niche));
        one.Needs[hold.Need] = 1.0;
        air = new Air(150, true);

        // A need the shelter does not serve, below the line: it ends, and the inhabitant chooses again.
        one.Needs["hunger"] = line / 2;
        Assert.False(inhabitants.KeepsSheltering(world, one, use, niche));
    }
}
