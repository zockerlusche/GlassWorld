using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;

namespace GlassWorld.Simulation.Tests;

/// <summary>A1373 (D827 R15): building steps are interruptible and go on - the interruption is a state with events.</summary>
public sealed partial class BuildingTests
{
    [Fact]
    public void SomethingElseInterruptsTheBuildingOnceAndTheBuilderTakesItUpAgain()
    {
        // (The building tests' data set min_need_to_build to 0, so here another action takes the builder away; a
        // pressing need takes the same path in the core.)
        var (host, port, _) = Invented(22);
        var events = Record(host);
        AnswerPlan(port, Plan());
        RunUntil(host, () => host.Inventions.Builds.Single().PendingObjectId is not null, 3000);

        // On its way to the material, the builder turns to something else for a while.
        var builder = host.Inhabitants.Get(host.Inventions.Builds.Single().InhabitantId);
        builder.Planned = null;
        builder.Activity = new Activity("idle", null, builder.Position, 30);
        host.World.Step();

        var interrupted = Assert.Single(events, e => e.Kind == InventionSystem.BuildInterrupted);
        Assert.Equal((builder.Id, "idle"), (interrupted.InhabitantId, interrupted.Detail));
        Assert.Equal("idle", host.Inventions.Builds.Single().InterruptedBy);

        // A few ticks later: one event, not one per tick.
        host.World.Run(5);
        Assert.Single(events, e => e.Kind == InventionSystem.BuildInterrupted);

        // Free again, the builder takes the building up and finishes it.
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, 3000);
        Assert.Contains(events, e => e.Kind == InventionSystem.BuildResumed && e.InhabitantId == builder.Id);
        Assert.Null(host.Inventions.Builds.Single().InterruptedBy);
    }
}
