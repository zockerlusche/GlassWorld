using GlassWorld.Contracts;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>B4b K4 part 2 (B2752): one worn thing per inhabitant; warmth only through its material.</summary>
public sealed class WearTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");

    [Fact]
    public void PuttingOnTakesTheThingOffTheGroundAndTakingOffLaysItDownWithItsOwnId()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), "de");
        host.Types.Register("test_wrap", ["wearable", "insulating", "crafted"]);
        var wearer = host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).First();
        var wrap = host.Objects.Spawn("test_wrap", wearer.Position);
        var other = host.Objects.Spawn("test_wrap", wearer.Position);

        Assert.True(host.Wear.Wear(wearer, wrap, wearer.Position));
        Assert.False(host.Objects.TryGet(wrap.Id, out _)); // nobody else sees or takes it
        Assert.Equal(new WornThing(wrap.Id, "test_wrap"), host.Wear.WornBy(wearer.Id));
        Assert.False(host.Wear.Wear(wearer, other, wearer.Position)); // one thing at a time
        Assert.True(host.Objects.TryGet(other.Id, out _));

        var at = new Position(wearer.Position.X + 3, wearer.Position.Z);
        Assert.True(host.Wear.Wear(wearer, null, at));
        Assert.True(host.Objects.TryGet(wrap.Id, out var back));
        Assert.Equal(("test_wrap", at), (back.TypeId, back.Position));
        Assert.Null(host.Wear.WornBy(wearer.Id));
        Assert.False(host.Wear.Wear(wearer, null, at)); // nothing worn, nothing to lay down
    }

    [Fact]
    public void PutOnAndTakeOffAreRulesOfTheDataWithTheirWearField()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), "de");
        host.Types.Register("test_wrap", ["wearable", "crafted"]);
        Assert.Contains(host.Actions.Applying("put_on", "test_wrap"), r => r.Wear == EffectRuleWear.On);
        Assert.Contains(host.Actions.Applying("take_off", null), r => r.Wear == EffectRuleWear.Off);
        Assert.DoesNotContain(host.Actions.Applying("put_on", "flint"), r => r.Wear is not null); // not wearable
    }

    [Fact]
    public void AWornThingKeepsWarmOnlyWhenItsMaterialInsulates()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        Assert.NotNull(host.Body);
        host.Types.Register("test_wrap", ["wearable", "insulating", "crafted"]);
        host.Types.Register("test_band", ["wearable", "crafted"]);
        var wearer = host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).First();
        var bare = host.Body!.FeltTenthC(host.World, wearer);

        host.Wear.Wear(wearer, host.Objects.Spawn("test_band", wearer.Position), wearer.Position);
        Assert.Equal(bare, host.Body.FeltTenthC(host.World, wearer)); // the function alone gives no warmth

        host.Wear.Wear(wearer, null, wearer.Position);
        host.Wear.Wear(wearer, host.Objects.Spawn("test_wrap", wearer.Position), wearer.Position);
        Assert.Equal(bare + host.Content.Body!.Worn!.WarmTenthC, host.Body.FeltTenthC(host.World, wearer));
    }

    [Fact]
    public void AColdInhabitantPutsOnAWearableThingBesideIt()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), "de");
        host.Types.Register("test_wrap", ["wearable", "insulating", "crafted"]);
        var wearer = host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).First();
        var wrap = host.Objects.Spawn("test_wrap", wearer.Position);
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;

        for (var hour = 0; hour < 24 && host.Wear.WornBy(wearer.Id) is null; hour++)
        {
            wearer.Needs["warmth"] = 0.05;
            host.World.Run(host.World.Calendar.Settings.TicksPerHour);
        }

        Assert.Equal(wrap.Id, host.Wear.WornBy(wearer.Id)?.Id);
        Assert.Contains(events, e => e.Kind == InhabitantsSystem.ActionStarted && e.Action == "put_on" && e.InhabitantId == wearer.Id);
    }
}
