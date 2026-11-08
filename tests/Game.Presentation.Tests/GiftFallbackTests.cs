using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Content;

namespace GlassWorld.Presentation.Tests;

/// <summary>Gift suggestions without the AI come from the data (F5238), never from a fixed list.</summary>
public sealed class GiftFallbackTests
{
    private static readonly ContentSet Content = ContentSet.Load(Path.Combine(TestPaths.Root, "content"));

    [Fact]
    public void ThirstGetsFreshWaterNotSaltWater()
    {
        var types = GiftFallback.For("thirst", Content.Brain.Weights, Content.Objects, 3);
        Assert.Contains("spring_water", types);
        Assert.DoesNotContain("sea_water", types);
    }

    [Fact]
    public void HungerGetsEdibleNotPoisonous()
    {
        var types = GiftFallback.For("hunger", Content.Brain.Weights, Content.Objects, 3);
        Assert.Contains("apple", types);
        Assert.DoesNotContain("red_berry", types);
    }

    [Fact]
    public void NeedsWithoutATargetGetNothing() => Assert.Empty(GiftFallback.For("sleep", Content.Brain.Weights, Content.Objects, 3));
}
