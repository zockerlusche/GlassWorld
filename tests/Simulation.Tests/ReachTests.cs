using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// D1105 H3a (A1724): the core checks the reach - an apple hanging above an inhabitant's reach cannot be eaten;
/// the try fails honestly (out_of_reach, too_high) and names no way up; one hanging low is eaten as before.
/// </summary>
public sealed class ReachTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");

    [Fact]
    public void AnAppleAboveTheReachFailsHonestlyAndOneBelowIsEaten()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new SilentPort(), "de", "start_island");
        host.World.Run(1);
        var map = host.Map!;
        var reach = ContentSet.Load(ContentDirectory).InhabitantModel.Reach!.HeightCm;
        var hanging = host.Objects.All.Where(o => o.TypeId == "apple" && map.GrowsInPlace(o.Id)).ToList();
        var high = hanging.FirstOrDefault(o => map.AboveCm(o.Id) > reach);
        var low = hanging.FirstOrDefault(o => map.AboveCm(o.Id) <= reach);
        Assert.True(high is not null && low is not null, "the island has apples above and below the reach");

        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        var inhabitant = host.Inhabitants.All.First();

        Try(host, inhabitant, high!);
        var failed = Assert.Single(events, e => e.Kind == InhabitantsSystem.OutOfReach && e.TargetId == high!.Id);
        Assert.Equal(InhabitantsSystem.TooHigh, failed.Detail);
        Assert.True(host.Objects.TryGet(high!.Id, out _), "the high apple still hangs");

        Try(host, inhabitant, low!);
        Assert.DoesNotContain(events, e => e.Kind == InhabitantsSystem.OutOfReach && e.TargetId == low!.Id);
    }

    private static void Try(SimulationHost host, Inhabitant inhabitant, WorldObject apple)
    {
        inhabitant.Needs["hunger"] = 0.2;
        inhabitant.Activity = new Activity("eat", apple.Id, apple.Position, null);
        for (var i = 0; i < 5000 && inhabitant.Activity is { Action: "eat" }; i++)
        {
            host.World.Run(1);
        }

        Assert.False(inhabitant.Activity is { Action: "eat" }, "the try did not end");
    }

    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
