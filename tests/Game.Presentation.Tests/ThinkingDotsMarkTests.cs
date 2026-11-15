using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1402 (B4a-S7, TL #19921): a "?" over the head while a question puzzles, the dots while only the effect is open.</summary>
public sealed class ThinkingDotsMarkTests
{
    private static readonly FigureThinkingDots Look = ContractSerializer.Deserialize<LookFigures>(
        File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "figures.json"))).ThinkingDots;

    [Fact]
    public void AQuestionMarkWhileItPuzzlesThenTheDotsThenNothing()
    {
        Assert.Equal(HeadMark.Question, ThinkingDots.Mark(Look, 1, 1));
        Assert.Equal(HeadMark.Dots, ThinkingDots.Mark(Look, 1, 0)); // it has looked at the thing: only the effect is open
        Assert.Equal(HeadMark.None, ThinkingDots.Mark(Look, 0, 0));
        Assert.Equal(HeadMark.None, ThinkingDots.Mark(Look, 2, 1, lying: true)); // never over a sleeper (D1112 #8)

        var dotsOnly = new FigureThinkingDots { AboveHeadM = Look.AboveHeadM, FontPx = Look.FontPx, SpacingPx = Look.SpacingPx, PeriodS = Look.PeriodS, AlphaMin = Look.AlphaMin, AlphaMax = Look.AlphaMax };
        Assert.Equal(HeadMark.Dots, ThinkingDots.Mark(dotsOnly, 1, 1)); // without a size in the data: dots only
    }
}
