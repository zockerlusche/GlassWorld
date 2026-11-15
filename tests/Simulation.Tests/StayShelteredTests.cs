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
}
