using System.Text.Json;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1375 (B4b R14): built inventions and building sites are drawn from their plans, not as the neutral marker.</summary>
public sealed class InventionLooksTests
{
    private const string Type = "inv_00a1b2c3";

    private static readonly Lazy<string> Plan = new(() =>
    {
        var example = File.ReadAllText(Path.Combine(TestPaths.Root, "contracts", "examples", "valid", "job-status.build-plan-bowl.json"));
        var plan = ContractSerializer.Deserialize<JobStatus>(example).Result!.BuildPlan!;

        // As the core keeps it (BuildTask.PlanJson).
        return JsonSerializer.Serialize(plan);
    });

    [Fact]
    public void NothingIsDrawnWithoutAPlan()
    {
        var waiting = Task(planJson: null);
        Assert.Empty(InventionLooks.Shown([waiting], [new WorldObject("obj-1", Type, new Position(1, 2))]));
        Assert.Empty(InventionLooks.DrawnTypes([waiting]));
    }

    [Fact]
    public void ASiteShowsTheFinishedStepsOnceTheFirstIsDoneAndGoesWhenTheInventorGivesUp()
    {
        Assert.Empty(InventionLooks.Shown([Task(steps: 0)], []));

        var site = Assert.Single(InventionLooks.Shown([Task(steps: 2)], []));
        Assert.Equal(("site:" + Type, 2, false, 10.0, 20.0), (site.Key, site.Step, site.Finished, site.X, site.Z));
        Assert.Empty(InventionLooks.DrawnTypes([Task(steps: 2)]));

        Assert.Empty(InventionLooks.Shown([Task(steps: 2) with { GaveUp = true }], []));
    }

    [Fact]
    public void EveryObjectOfABuiltTypeIsDrawnWholeAndTheObjectViewLeavesThemOut()
    {
        var built = Task(steps: 3) with { BuiltObjectId = "obj-7" };
        WorldObject[] objects = [new("obj-9", Type, new Position(4, 5)), new("obj-7", Type, new Position(10, 20)), new("obj-1", "flint", new Position(0, 0))];

        var shown = InventionLooks.Shown([built], objects);
        Assert.Equal(["obj-7", "obj-9"], shown.Select(s => s.Key));
        Assert.All(shown, s => Assert.True(s.Finished));
        Assert.Equal((4.0, 5.0), (shown[1].X, shown[1].Z));
        Assert.Equal([Type], InventionLooks.DrawnTypes([built]));
    }

    [Fact]
    public void AThingFinishedSmallerIsDrawnWithTheStepsItHas()
    {
        // A1373 step 2: the builder left it as far as it was built.
        var smaller = Task(steps: 1) with { BuiltObjectId = "obj-7", Smaller = true };
        var shown = Assert.Single(InventionLooks.Shown([smaller], [new("obj-7", Type, new Position(10, 20))]));
        Assert.Equal((true, 1), (shown.Finished, shown.Step));
        var whole = Assert.Single(InventionLooks.Shown([smaller with { Smaller = false }], [new("obj-7", Type, new Position(10, 20))]));
        Assert.Equal(int.MaxValue, whole.Step);
    }

    [Fact]
    public void TheSignatureChangesWithEveryNewStepAndNotOtherwise()
    {
        var two = InventionLooks.Signature(InventionLooks.Shown([Task(steps: 2)], []));
        Assert.Equal(two, InventionLooks.Signature(InventionLooks.Shown([Task(steps: 2)], [])));
        Assert.NotEqual(two, InventionLooks.Signature(InventionLooks.Shown([Task(steps: 3)], [])));
    }

    [Fact]
    public void ThePlanAsTheCoreKeepsItReadsBackWithAllItsParts()
    {
        var plan = InventionSystem.PlanOf(Plan.Value);
        var example = ContractSerializer.Deserialize<JobStatus>(File.ReadAllText(Path.Combine(TestPaths.Root, "contracts", "examples", "valid", "job-status.build-plan-bowl.json"))).Result!.BuildPlan!;
        Assert.Equal(example.Parts.Count, plan.Parts.Count);
        Assert.Throws<ArgumentException>(() => InventionSystem.PlanOf(string.Empty));
    }

    private static BuildTask Task(int steps = 0, string? planJson = "plan") =>
        new(Type, "inh-000001", 1, null, null, planJson == "plan" ? Plan.Value : planJson, 10, 20, steps, [], null, null, null, false);
}
