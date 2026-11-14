using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>D958 D2: eating and drinking show as arm gestures from the look data.</summary>
public sealed class GestureCadenceTests
{
    private static LookFigures Figures() => ContractSerializer.Deserialize<LookFigures>(
        File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "figures.json")));

    [Fact]
    public void DrinkingAtTheWaterScoopsEatingAndDrinkingElseBringTheHandToTheMouth()
    {
        var gestures = Figures().Gestures;

        Assert.Equal("scoop", GestureCadence.Choose(gestures, "drink", AnimationMappingReach.Low)?.Id);
        Assert.Equal("hand_to_mouth", GestureCadence.Choose(gestures, "drink", AnimationMappingReach.Mid)?.Id);
        Assert.Equal("hand_to_mouth", GestureCadence.Choose(gestures, "drink", null)?.Id);
        Assert.Equal("hand_to_mouth", GestureCadence.Choose(gestures, "eat", AnimationMappingReach.Low)?.Id);
        Assert.Null(GestureCadence.Choose(gestures, "inspect", AnimationMappingReach.Low));
        Assert.Null(GestureCadence.Choose(gestures, null, null));
    }

    [Fact]
    public void AGestureRisesHoldsLowersAndRestsWithoutJumps()
    {
        foreach (var gesture in Figures().Gestures!)
        {
            var cycle = GestureCadence.CycleS(gesture);
            Assert.InRange(cycle, 1, 30);
            Assert.Equal(0, GestureCadence.At(gesture, 0).Weight, 6);

            // It reaches its full influence, and at the head the hand is fully there.
            var peak = Enumerable.Range(0, 1000).Select(i => GestureCadence.At(gesture, cycle * i / 1000.0)).ToList();
            Assert.Contains(peak, s => Math.Abs(s.Weight - gesture.Influence) < 1e-9 && s.ToHead >= 1 - 1e-9);

            // Calm: no step between two moments 20 ms apart is larger than the data allows.
            var previous = GestureCadence.At(gesture, 0);
            for (var t = 0.02; t < cycle * 2; t += 0.02)
            {
                var now = GestureCadence.At(gesture, t);
                Assert.InRange(Math.Abs(now.Weight - previous.Weight), 0, 0.1);
                if (Math.Min(now.Weight, previous.Weight) > 0.01)
                {
                    // Where the hand is matters only while the gesture shows (between cycles it starts anew).
                    Assert.InRange(Math.Abs(now.ToHead - previous.ToHead), 0, 0.1);
                }
                previous = now;
            }

            // It rests before the next cycle (chewing, swallowing).
            Assert.Equal(0, GestureCadence.At(gesture, cycle - (gesture.RestS / 2)).Weight, 6);
        }
    }

    [Fact]
    public void ScoopingDipsAtTheWaterBeforeTheHandsComeUp()
    {
        var scoop = Figures().Gestures!.Single(g => g.Id == "scoop");
        var atWater = GestureCadence.At(scoop, scoop.RiseS + ((scoop.DipS ?? 0) / 2));
        Assert.Equal(0, atWater.ToHead, 6);
        Assert.Equal(scoop.Influence, atWater.Weight, 6);
        Assert.Equal(2, scoop.Arms.Count);
    }
}
