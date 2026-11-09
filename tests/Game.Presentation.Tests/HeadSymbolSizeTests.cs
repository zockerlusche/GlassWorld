using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.Ui;

namespace GlassWorld.Presentation.Tests;

/// <summary>Head symbols one size, readable at every zoom (F5284).</summary>
public sealed class HeadSymbolSizeTests
{
    private static readonly UiTokens Tokens = ContractSerializer.Deserialize<UiTokens>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "tokens.json")));

    private static readonly EmotionSymbols Feelings = ContractSerializer.Deserialize<EmotionSymbols>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "emotion_symbols.json")));

    [Theory]
    [InlineData(2)]
    [InlineData(40)]
    [InlineData(400)]
    [InlineData(4000)]
    public void OnScreenEverySymbolStaysBetweenFloorAndCeiling(double distanceM)
    {
        var perMetre = HeadSymbolSize.PixelsPerMetre(distanceM, 70, Tokens.BaseHeight);
        var look = Tokens.HeadSymbol;
        foreach (var size in Feelings.Symbols.Select(s => s.SizeM).Append(look.SizeM))
        {
            var px = size * HeadSymbolSize.Factor(size, perMetre, look.MinPx, look.MaxPx) * perMetre;
            Assert.InRange(px, look.MinPx - 1e-6, look.MaxPx + 1e-6);
        }
    }

    [Fact]
    public void FarAwayTheQuestionMarkAndTheBulbLookTheSameSize()
    {
        // Marcel F5284: the bulb was only seen close up, question marks were bigger than the rest.
        var perMetre = HeadSymbolSize.PixelsPerMetre(40, 70, Tokens.BaseHeight);
        var look = Tokens.HeadSymbol;
        double Px(double size) => size * HeadSymbolSize.Factor(size, perMetre, look.MinPx, look.MaxPx) * perMetre;
        var bulb = Feelings.Symbols.Single(s => s.Id == "bulb").SizeM;
        Assert.Equal(Px(look.SizeM), Px(bulb), 6);
    }

    [Fact]
    public void BetweenTheLimitsTheWorldSizeIsKept()
    {
        var look = Tokens.HeadSymbol;
        var perMetre = (look.MinPx + look.MaxPx) / 2 / look.SizeM;
        Assert.Equal(1, HeadSymbolSize.Factor(look.SizeM, perMetre, look.MinPx, look.MaxPx), 9);
    }

    [Fact]
    public void AllHeadSymbolsShareOneOrderOfSize()
    {
        var look = Tokens.HeadSymbol;
        Assert.All(Feelings.Symbols.Where(s => s.Id != "stars"), s => Assert.InRange(s.SizeM, look.SizeM / 2, look.SizeM * 2));
    }

    [Fact]
    public void ACameraInsideTheSymbolHidesIt() => Assert.Equal(0, HeadSymbolSize.Factor(0.3, HeadSymbolSize.PixelsPerMetre(0, 70, 1080), 30, 60));
}
