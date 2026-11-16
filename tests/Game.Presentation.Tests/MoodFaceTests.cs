using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1802 (Marcel W2245): the mouth moves as long as a sentence takes; the face rests by the strongest mood.</summary>
public sealed class MoodFaceTests
{
    private static readonly LookFigures Figures = ContractSerializer.Deserialize<LookFigures>(
        File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "figures.json")));

    [Fact]
    public void ALongerSentenceMovesTheMouthLongerWithinItsLimits()
    {
        var data = Figures.Greeting;
        Assert.Equal(data.TalkS, GreetingReaction.TalkSeconds(data, 3));
        Assert.True(GreetingReaction.TalkSeconds(data, 60) > GreetingReaction.TalkSeconds(data, 30));
        Assert.Equal(data.TalkMaxS!.Value, GreetingReaction.TalkSeconds(data, 10_000));
        Assert.Equal(GreetingReaction.TalkSeconds(data, 60), GreetingReaction.For(data, "said", null, hasText: true, textLength: 60)!.Value.TalkS);
        Assert.Equal(0, GreetingReaction.For(data, "said", null, hasText: false, textLength: 60)!.Value.TalkS);
    }

    [Fact]
    public void TheBrowsGoWithTheMood()
    {
        // GW1 #553: the figure models carry brow_raise and brow_frown; worry raises them, frustration frowns.
        var faces = Figures.MoodFaces;
        Assert.Equal("brow_raise", MoodFace.For(faces, new Dictionary<string, double> { ["sadness"] = 0.9 })!.Value.Brow);
        Assert.Equal("brow_frown", MoodFace.For(faces, new Dictionary<string, double> { ["frustration"] = 0.95 })!.Value.Brow);
        Assert.Null(MoodFace.For(faces, new Dictionary<string, double> { ["joy"] = 0.95 })!.Value.Brow);
    }

    [Fact]
    public void AShownFaceHoldsAMarginBelowItsThreshold()
    {
        // Review GW5 #547: a mood hovering at the threshold does not switch the face on and off.
        var faces = Figures.MoodFaces;
        var joy = faces!.First(f => f.Mood == "joy");
        var hold = Figures.MoodFaceHold!.Value;
        var justBelow = new Dictionary<string, double> { ["joy"] = joy.ShowsFrom - (hold / 2) };
        Assert.Null(MoodFace.For(faces, justBelow));
        var shown = MoodFace.For(faces, new Dictionary<string, double> { ["joy"] = joy.ShowsFrom + 0.01 });
        Assert.Equal(shown, MoodFace.For(faces, justBelow, shown, hold));
        Assert.Null(MoodFace.For(faces, new Dictionary<string, double> { ["joy"] = joy.ShowsFrom - hold - 0.01 }, shown, hold));
    }

    [Fact]
    public void TheStrongestMoodAboveItsThresholdShowsItsMouth()
    {
        var faces = Figures.MoodFaces;
        Assert.Null(MoodFace.For(faces, new Dictionary<string, double> { ["joy"] = 0.3, ["sadness"] = 0.2 }));
        Assert.Equal("smile", MoodFace.For(faces, new Dictionary<string, double> { ["joy"] = 0.9, ["sadness"] = 0.55 })!.Value.Face);
        Assert.Equal("sad", MoodFace.For(faces, new Dictionary<string, double> { ["joy"] = 0.6, ["sadness"] = 0.95 })!.Value.Face);
        Assert.Null(MoodFace.For(null, new Dictionary<string, double> { ["joy"] = 1 }));
        Assert.All(faces!, f => Assert.InRange(f.Weight, 0, 1));
    }
}
