using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// D1025 (TL TH142): the position of an option says nothing - the decider's options are shuffled per request from
/// its id, the same request in the same order (replay), with no random stream of the world.
/// </summary>
public sealed class OptionOrderTests
{
    [Fact]
    public void TheSameSeedGivesTheSameOrderAndEveryOptionStays()
    {
        var a = Enumerable.Range(1, 12).ToList();
        var b = Enumerable.Range(1, 12).ToList();
        DecisionsSystem.Shuffle(a, 4711);
        DecisionsSystem.Shuffle(b, 4711);
        Assert.Equal(a, b);
        Assert.Equal(Enumerable.Range(1, 12), a.Order());
    }

    [Fact]
    public void DifferentRequestsGiveDifferentOrdersAndTheFirstPlaceIsSpread()
    {
        // Over many request ids the first option is not always the same one (61 of 67 in L1591 before).
        var first = new Dictionary<int, int>();
        for (var r = 0; r < 400; r++)
        {
            var items = Enumerable.Range(0, 6).ToList();
            DecisionsSystem.Shuffle(items, DecisionIds.SeedOf("request-" + r));
            first[items[0]] = first.GetValueOrDefault(items[0]) + 1;
        }

        Assert.Equal(6, first.Count);
        Assert.All(first.Values, n => Assert.InRange(n, 30, 110));
    }

    [Fact]
    public void TheShuffleOfARequestCanBeUndoneFromItsId()
    {
        // Resend finds the question's subject in the data's order: the permutation follows from the request id alone.
        var data = Enumerable.Range(0, 9).ToList();
        var sent = data.ToList();
        var seed = DecisionIds.SeedOf("req-42");
        DecisionsSystem.Shuffle(sent, seed);
        var positions = Enumerable.Range(0, sent.Count).ToList();
        DecisionsSystem.Shuffle(positions, seed);
        var back = new int[sent.Count];
        for (var i = 0; i < positions.Count; i++)
        {
            back[positions[i]] = sent[i];
        }

        Assert.Equal(data, back);
    }

    [Fact]
    public void ShortListsStayAsTheyAre()
    {
        var none = new List<int>();
        var one = new List<int> { 7 };
        DecisionsSystem.Shuffle(none, 1);
        DecisionsSystem.Shuffle(one, 1);
        Assert.Empty(none);
        Assert.Equal([7], one);
    }
}
