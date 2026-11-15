using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// D1105 H2 (A1723, B2738/B2741): things up high come only from nature - an apple hangs in its tree's crown at its
/// own height (seeded from its id) and falls with a chance per day; fallen it lies on the ground. The decider sees
/// the height (above_m); whether it can be reached is the core's later step (H3).
/// </summary>
public sealed class ThingsAboveTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");

    [Fact]
    public void ApplesHangInTheCrownEachAtItsOwnHeight()
    {
        var host = Island();
        var map = host.Map!;
        var (min, max, _, plant) = map.Hangs["apple"];
        var hanging = host.Objects.All.Where(o => o.TypeId == "apple" && map.GrowsInPlace(o.Id)).ToList();
        Assert.NotEmpty(hanging);
        var heights = hanging.Select(o => map.AboveCm(o.Id)).ToList();
        Assert.All(heights, h => Assert.InRange(h, plant * min / 1000, plant * max / 1000));
        Assert.True(heights.Distinct().Count() > 1 || heights.Count == 1, "each apple at its own height");
        Assert.Equal(heights, hanging.Select(o => map.AboveCm(o.Id)).ToList());

        // Things on the ground stay at 0 (berries on bushes, stones).
        Assert.All(host.Objects.All.Where(o => o.TypeId == "fieldstone"), o => Assert.Equal(0, map.AboveCm(o.Id)));
    }

    [Fact]
    public void ApplesFallOverTheDaysAndLieOnTheGround()
    {
        var host = Island();
        var map = host.Map!;
        var apples = host.Objects.All.Where(o => o.TypeId == "apple" && map.GrowsInPlace(o.Id)).Select(o => o.Id).ToList();
        host.World.Run(4 * host.World.Calendar.TicksPerDay);

        var fallen = apples.Where(id => host.Objects.TryGet(id, out _) && !map.GrowsInPlace(id) && host.Objects.HeldBy(id) is null).ToList();
        Assert.NotEmpty(fallen);
        Assert.All(fallen, id => Assert.Equal(0, map.AboveCm(id)));
    }

    [Fact]
    public void TheDeciderSeesHowHighAThingHangs()
    {
        var host = Island();
        var apple = host.Objects.All.First(o => o.TypeId == "apple" && host.Map!.GrowsInPlace(o.Id));
        Assert.True(host.Decisions.AboveCm!(apple.Id) > 0);
    }

    private static SimulationHost Island()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        return host;
    }
}
