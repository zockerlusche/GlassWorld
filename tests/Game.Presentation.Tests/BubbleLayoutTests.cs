using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>Bubbles over heads never lie on top of each other and stay in the free area (W1687).</summary>
public sealed class BubbleLayoutTests
{
    [Fact]
    public void NearlyLevelBubblesKeepTheirOrderUntilTheDifferenceIsClear()
    {
        var area = (0.0, 0.0, 1000.0, 1000.0);
        BubbleRequest A(double y) => new("a", 500, y, 200, 40);
        BubbleRequest B(double y) => new("b", 520, y, 200, 40);

        var first = BubbleLayout.Place([A(400), B(396)], area, 6);
        Assert.Equal(["a", "b"], first.Select(p => p.Id));

        // b comes 3 px in front: without memory the stack flips, with it the order stays.
        Assert.Equal(["b", "a"], BubbleLayout.Place([A(400), B(403)], area, 6).Select(p => p.Id));
        var kept = BubbleLayout.Place([A(400), B(403)], area, 6, [.. first.Select(p => p.Id)], 12);
        Assert.Equal(["a", "b"], kept.Select(p => p.Id));

        // Clearly in front: the order changes.
        var changed = BubbleLayout.Place([A(400), B(430)], area, 6, [.. kept.Select(p => p.Id)], 12);
        Assert.Equal(["b", "a"], changed.Select(p => p.Id));
    }

    private static readonly (double, double, double, double) Area = (0, 180, 1700, 1080);

    [Fact]
    public void ABubbleAloneSitsCentredAboveItsHead()
    {
        var place = BubbleLayout.Place([new BubbleRequest("a", 800, 500, 400, 60)], Area, 8).Single();
        Assert.Equal(600, place.Left);
        Assert.Equal(440, place.Top);
    }

    [Fact]
    public void TwoInhabitantsStandingTogetherGetStackedBubbles()
    {
        // Marcel's test (D612): two sentences on top of each other were unreadable.
        var places = BubbleLayout.Place([new BubbleRequest("a", 800, 500, 500, 80), new BubbleRequest("b", 830, 480, 500, 80)], Area, 8);
        AssertNoOverlap(places, [(500, 80), (500, 80)]);
    }

    [Fact]
    public void ABubbleUnderTheTopBarMovesDownIntoView()
    {
        // D609/D610: the text was cut off by the top bar.
        var place = BubbleLayout.Place([new BubbleRequest("a", 800, 190, 400, 60)], Area, 8).Single();
        Assert.Equal(180, place.Top);
    }

    [Fact]
    public void AtTheScreenEdgeTheBubbleStaysInside()
    {
        var left = BubbleLayout.Place([new BubbleRequest("a", 10, 500, 400, 60)], Area, 8).Single();
        var right = BubbleLayout.Place([new BubbleRequest("a", 1690, 500, 400, 60)], Area, 8).Single();
        Assert.Equal(0, left.Left);
        Assert.Equal(1300, right.Left);
    }

    [Fact]
    public void NearTheTopTheyStackDownwardsInstead()
    {
        var places = BubbleLayout.Place([new BubbleRequest("a", 800, 250, 400, 60), new BubbleRequest("b", 800, 240, 400, 60)], Area, 8);
        AssertNoOverlap(places, [(400, 60), (400, 60)]);
        Assert.All(places, p => Assert.True(p.Top >= 180));
    }

    [Fact]
    public void ACrowdStaysApartAndTheResultRepeats()
    {
        var crowd = Enumerable.Range(0, 6).Select(i => new BubbleRequest($"i{i}", 800 + (i * 15), 600 - (i * 5), 420, 70)).ToList();
        var places = BubbleLayout.Place(crowd, Area, 8);
        AssertNoOverlap(places, [.. crowd.Select(c => (c.Width, c.Height))]);
        Assert.Equal(places, BubbleLayout.Place([.. Enumerable.Reverse(crowd)], Area, 8).OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => places.Single(q => q.Id == p.Id)));
    }

    private static void AssertNoOverlap(IReadOnlyList<BubblePlace> places, IReadOnlyList<(double W, double H)> sizes)
    {
        var boxes = places.Zip(sizes, (p, s) => (p.Left, p.Top, s.W, s.H)).ToList();
        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                var (a, b) = (boxes[i], boxes[j]);
                Assert.False(a.Left < b.Left + b.W && b.Left < a.Left + a.W && a.Top < b.Top + b.H && b.Top < a.Top + a.H, $"{places[i].Id} overlaps {places[j].Id}");
            }
        }
    }
}
