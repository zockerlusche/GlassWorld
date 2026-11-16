using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1385 (GW3): the snapshot carries how wet an inhabitant is and how high it is up in a tree, for the look - from the
/// body system on islands, absent (null) on worlds without one.
/// </summary>
public sealed class SnapshotBodyTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");

    [Fact]
    public void OnAnIslandTheSnapshotShowsWetnessAndHeight()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        var body = host.Body!;
        var inhabitant = host.Inhabitants.All.First();
        var apple = host.Objects.All.First(o => o.TypeId == "apple" && host.Map!.AboveCm(o.Id) > 0);
        body.RestoreUp([new UpInhabitant(inhabitant.Id, body.CarrierOf(apple)!.Id, 250, host.World.Tick + 100)]);

        var seen = host.Snapshot().Inhabitants.First(i => i.Id == inhabitant.Id);
        Assert.Equal(body.WetPermille(inhabitant.Id), seen.WetPermille);
        Assert.Equal(250, seen.ClimbedCm);
        Assert.All(host.Snapshot().Inhabitants.Where(i => i.Id != inhabitant.Id), i => Assert.Equal(0, i.ClimbedCm));
    }

    [Fact]
    public void WithoutABodySystemBothAreAbsent()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 1, new MockDecisionPort(), "de");
        Assert.Null(host.Body);
        Assert.All(host.Snapshot().Inhabitants, i => Assert.True(i.WetPermille is null && i.ClimbedCm is null));
    }
}
