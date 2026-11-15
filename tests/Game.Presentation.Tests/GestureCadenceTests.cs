using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>D958 D2: eating and drinking show as arm gestures from the look data.</summary>
public sealed class GestureCadenceTests
{
    private static readonly string[] SomeActions = ["idle", "eat", "drink", "wander", "sleep", "use"];
    private static readonly string[] ComfortGestures = ["hug_self", "wipe_brow"];

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
    public void ColdHugsAndShiversHeatWipesTheBrowAndNeitherIsAnActionsGesture()
    {
        // A1729 (A1379 rest): the comfort level shows through its own gestures, never through an action's choice.
        var figures = Figures();
        var cold = GestureCadence.ForComfort(figures.Comfort, figures.Gestures, "cold");
        var hot = GestureCadence.ForComfort(figures.Comfort, figures.Gestures, "hot");
        Assert.Equal("hug_self", cold.Gesture?.Id);
        Assert.True(cold.ShiverM > 0 && cold.ShiverHz > 0);
        Assert.Equal("wipe_brow", hot.Gesture?.Id);
        Assert.Equal(0, hot.ShiverM);
        Assert.Same(ComfortLook.None, GestureCadence.ForComfort(figures.Comfort, figures.Gestures, "comfortable"));
        Assert.Same(ComfortLook.None, GestureCadence.ForComfort(figures.Comfort, figures.Gestures, null));
        foreach (var action in SomeActions)
        {
            Assert.DoesNotContain(GestureCadence.Choose(figures.Gestures, action, null)?.Id, ComfortGestures);
        }

        // Calm: a small sideways shiver, scaled by the figure, back and forth around the place.
        var peak = Enumerable.Range(0, 200).Select(i => Math.Abs(GestureCadence.ShiverAt(cold, i * 0.005, 1.0))).Max();
        Assert.InRange(peak, cold.ShiverM * 0.9, cold.ShiverM);
        Assert.Equal(0, GestureCadence.ShiverAt(hot, 0.3, 1.0));
    }

    [Fact]
    public void EveryComfortEntryNamesAKnownLevelAndAnExistingGesture()
    {
        var figures = Figures();
        var levels = ContractSerializer.Deserialize<Vocabulary>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "vocabulary", "comfort_levels.json")))
            .Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(figures.Comfort!);
        Assert.All(figures.Comfort!, c =>
        {
            Assert.Contains(c.Level, levels);
            Assert.True(c.Gesture is null || figures.Gestures!.Any(g => g.Id == c.Gesture), c.Gesture);
        });
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
