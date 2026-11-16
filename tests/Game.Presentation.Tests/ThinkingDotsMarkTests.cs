using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1402, A1788: the screen's one "?" while a question puzzles (the figure then shows no dots), the dots while only the effect is open.</summary>
public sealed class ThinkingDotsMarkTests
{
    [Fact]
    public void AQuestionMarkWhileItPuzzlesThenTheDotsThenNothing()
    {
        Assert.Equal(HeadMark.Question, ThinkingDots.Mark(1, 1));
        Assert.Equal(HeadMark.Dots, ThinkingDots.Mark(1, 0)); // it has looked at the thing: only the effect is open
        Assert.Equal(HeadMark.None, ThinkingDots.Mark(0, 0));
        Assert.Equal(HeadMark.None, ThinkingDots.Mark(2, 1, lying: true)); // never over a sleeper (D1112 #8)
    }
}
