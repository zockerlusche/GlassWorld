using GlassWorld.Simulation.Random;

namespace GlassWorld.Simulation.Tests;

public sealed class RandomTests
{
    /// <summary>Reference output of pcg32-demo (pcg-c-basic) for pcg32_srandom_r(42, 54).</summary>
    [Fact]
    public void Pcg32MatchesReferenceVectors()
    {
        var rng = new Pcg32(42, 54);
        uint[] expected = [0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e];
        foreach (var value in expected)
        {
            Assert.Equal(value, rng.NextUInt());
        }
    }

    [Fact]
    public void BoundedValuesStayInRangeAndCoverIt()
    {
        var rng = new Pcg32(1, 1);
        var seen = new bool[7];
        for (var i = 0; i < 10_000; i++)
        {
            var v = rng.NextUInt(7);
            Assert.InRange(v, 0u, 6u);
            seen[v] = true;
        }

        Assert.All(seen, Assert.True);
    }

    [Fact]
    public void NextIntCoversTheFullIntRange()
    {
        var rng = new Pcg32(3, 9);
        var sawNegative = false;
        var sawPositive = false;
        for (var i = 0; i < 1_000; i++)
        {
            var v = rng.NextInt(int.MinValue, int.MaxValue);
            Assert.InRange(v, int.MinValue, int.MaxValue - 1);
            sawNegative |= v < 0;
            sawPositive |= v > 0;
        }

        Assert.True(sawNegative && sawPositive);
        Assert.Equal(-5, new Pcg32(1, 1).NextInt(-5, -4));
    }

    [Fact]
    public void NextDoubleIsInUnitInterval()
    {
        var rng = new Pcg32(7, 3);
        for (var i = 0; i < 10_000; i++)
        {
            Assert.InRange(rng.NextDouble(), 0.0, 0.9999999999999999);
        }
    }

    [Fact]
    public void SavedStateContinuesIdentically()
    {
        var a = new Pcg32(99, 5);
        a.NextUInt();
        var copy = Pcg32.FromState(a.State);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(a.NextUInt(), copy.NextUInt());
        }
    }

    [Fact]
    public void NewStreamDoesNotShiftExistingStreams()
    {
        var plain = new RandomStreams(123);
        var withExtra = new RandomStreams(123);

        var first = Enumerable.Range(0, 5).Select(_ => plain.Get("weather").NextUInt()).ToList();
        withExtra.Get("inhabitant/inh-000001/mood").NextUInt();
        var second = Enumerable.Range(0, 5).Select(_ => withExtra.Get("weather").NextUInt()).ToList();

        Assert.Equal(first, second);
    }

    [Fact]
    public void StreamsDifferByNameAndSeed()
    {
        Assert.NotEqual(new RandomStreams(1).Get("a").NextUInt(), new RandomStreams(1).Get("b").NextUInt());
        Assert.NotEqual(new RandomStreams(1).Get("a").NextUInt(), new RandomStreams(2).Get("a").NextUInt());
        Assert.Equal(new RandomStreams(1).Get("a").NextUInt(), new RandomStreams(1).Get("a").NextUInt());
    }

    [Fact]
    public void StreamsRestoreFromSnapshot()
    {
        var streams = new RandomStreams(5);
        streams.Get("x").NextUInt();
        streams.Get("y").NextUInt();
        var restored = RandomStreams.Restore(5, streams.Snapshot());

        Assert.Equal(streams.Get("x").NextUInt(), restored.Get("x").NextUInt());
        Assert.Equal(streams.Get("y").NextUInt(), restored.Get("y").NextUInt());
    }

    [Fact]
    public void Fnv1aMatchesKnownValues()
    {
        Assert.Equal(0xCBF29CE484222325UL, RandomStreams.Fnv1a64(string.Empty));
        Assert.Equal(0xAF63DC4C8601EC8CUL, RandomStreams.Fnv1a64("a"));
    }
}
