using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// D1105 H3b (A1724, B2738/B2741): climbing what gives grip. Towards an apple up high one climbs the tree whose crown
/// it hangs in, up to a height by the tree's grip; up there the apple is in reach. A smooth trunk fails honestly
/// (out_of_reach, too_smooth); slipping is gentle (a fright). The decider is offered climbing only where it can mean
/// something. No way up is named, nothing is placed for it: the trees and apples are the start island's own.
/// </summary>
public sealed class ClimbingTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);

    [Fact]
    public void UpInTheTreeTheHighAppleIsInReach()
    {
        var (host, events, inhabitant, high) = Island();
        var body = host.Body!;
        var carrier = body.CarrierOf(high)!;
        Assert.NotNull(carrier);
        Assert.Equal("fruit_tree", carrier.TypeId);
        Assert.True(Content.Objects.Params(carrier.TypeId).GripPermille >= Content.Body!.Climbing!.MinGripPermille, "the start island's apple tree gives grip");

        // Eating from below fails (too high); climbing - again after a gentle slip, if one comes - takes it up.
        Run(host, inhabitant, "eat", high);
        Assert.Contains(events, e => e.Kind == InhabitantsSystem.OutOfReach && e.TargetId == high.Id && e.Detail == InhabitantsSystem.TooHigh);
        for (var tries = 0; tries < 20 && body.ClimbedCm(inhabitant.Id) == 0; tries++)
        {
            Run(host, inhabitant, "climb", high);
            host.World.Run(1);
        }

        var up = body.ClimbedCm(inhabitant.Id);
        Assert.True(up > 0, "it got up");
        Assert.True(up <= Content.Objects.Params(carrier.TypeId).ShadeHeightCm, "never higher than the tree");
        Assert.All(events.Where(e => e.Kind == BodySystem.Slipped), e => Assert.Equal(carrier.Id, e.TargetId));

        // Up there the apple is in reach: no out_of_reach this time.
        var before = events.Count(e => e.Kind == InhabitantsSystem.OutOfReach);
        Run(host, inhabitant, "eat", high);
        Assert.Equal(before, events.Count(e => e.Kind == InhabitantsSystem.OutOfReach));

        // Up is state: in the hash and in a save; down again after the data's minutes.
        Assert.Single(body.CaptureUp());
        host.World.Run(Content.Body.Climbing.UpMinutes * host.World.Calendar.Settings.TicksPerHour / 60 + 1);
        Assert.Equal(0, body.ClimbedCm(inhabitant.Id));
    }

    [Fact]
    public void ASmoothTrunkFailsHonestly()
    {
        var (host, events, inhabitant, _) = Island();
        var smooth = Content.Objects.TypeIds.First(t => Content.Objects.Params(t) is { GripPermille: { } grip, ShadeHeightCm: > 0 } && grip < Content.Body!.Climbing!.MinGripPermille);
        var trunk = host.Objects.Spawn(smooth, new Position(inhabitant.Position.X + 2, inhabitant.Position.Z));
        Run(host, inhabitant, "climb", trunk);

        var failed = Assert.Single(events, e => e.Kind == InhabitantsSystem.OutOfReach && e.TargetId == trunk.Id);
        Assert.Equal(InhabitantsSystem.TooSmooth, failed.Detail);
        Assert.Equal(0, host.Body!.ClimbedCm(inhabitant.Id));
    }

    [Fact]
    public void ClimbingIsOfferedOnlyWhereItCanMeanSomething()
    {
        var (host, _, _, high) = Island();
        var offers = host.Decisions.OffersOn!;
        Assert.True(offers("climb", high), "towards an apple up high");
        Assert.True(offers("climb", host.Body!.CarrierOf(high)!), "on the tree itself");
        Assert.False(offers("climb", host.Objects.All.First(o => o.TypeId == "fieldstone")), "not on a stone");
        Assert.True(offers("inspect", high), "other actions as before");
    }

    [Fact]
    public void UpInACrownSurvivesASaveAndCountsInTheHash()
    {
        var (host, _, inhabitant, high) = Island();
        var body = host.Body!;
        var carrier = body.CarrierOf(high)!;
        using var hasher = new State.StateHasher();
        body.WriteState(hasher);
        var down = hasher.Finish();

        body.RestoreUp([new UpInhabitant(inhabitant.Id, carrier.Id, 300, host.World.Tick + 100)]);
        Assert.Equal(300, body.ClimbedCm(inhabitant.Id));
        Assert.Equal(300, body.UpCm(inhabitant, high));
        using var again = new State.StateHasher();
        body.WriteState(again);
        Assert.NotEqual(down, again.Finish());
        Assert.Equal(body.CaptureUp(), [new UpInhabitant(inhabitant.Id, carrier.Id, 300, host.World.Tick + 100)]);
    }

    private static (SimulationHost Host, List<WorldEvent> Events, Inhabitant Inhabitant, WorldObject High) Island()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new SilentPort(), "de", "start_island");
        host.World.Run(1);
        var reach = Content.InhabitantModel.Reach!.HeightCm;
        var high = host.Objects.All.First(o => o.TypeId == "apple" && host.Map!.AboveCm(o.Id) > reach);
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        var inhabitant = host.Inhabitants.All.First();
        inhabitant.Needs["hunger"] = 0.2;
        return (host, events, inhabitant, high);
    }

    private static void Run(SimulationHost host, Inhabitant inhabitant, string action, WorldObject target)
    {
        inhabitant.Activity = new Activity(action, target.Id, target.Position, null);
        for (var i = 0; i < 5000 && inhabitant.Activity is { } a && a.Action == action; i++)
        {
            host.World.Run(1);
        }

        Assert.False(inhabitant.Activity is { } still && still.Action == action, "the try did not end");
    }

    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
