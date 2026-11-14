using System.Text.Json;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.Ui;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>The corners of Variante A (A1682, D957 v4 section 1): portrait list, symbols, counts, the free middle.</summary>
public sealed class CornerModelTests
{
    private static readonly UiTokens Tokens = ContractSerializer.Deserialize<UiTokens>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "tokens.json")));

    [Fact]
    public void TheListTurnsCompactFromTheDatasNumberOfInhabitants()
    {
        var from = Tokens.Zones.PortraitsCompactFrom;
        Assert.False(CornerModel.Compact(from - 1, Tokens.Zones));
        Assert.True(CornerModel.Compact(from, Tokens.Zones));
        Assert.True(CornerModel.Compact(from * 20, Tokens.Zones));
    }

    [Fact]
    public void OnlyTheRowsInViewAndOneBeyondRenderTheirPortrait()
    {
        // 100 rows of 50 px, 200 px visible: the top shows rows 0-3 and renders row 4 ahead.
        Assert.Equal((0, 4), CornerModel.InView(100, 50, 200, 0));

        // Scrolled to row 40: one row before and after the visible ones.
        Assert.Equal((39, 44), CornerModel.InView(100, 50, 200, 2000));

        // The end of the list and an empty list.
        Assert.Equal((97, 99), CornerModel.InView(100, 50, 200, 4900));
        Assert.Equal((0, -1), CornerModel.InView(0, 50, 200, 0));
    }

    [Fact]
    public void EverySeasonAndWeatherOfTheDataHasItsOwnSymbol()
    {
        using var time = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "world", "time.json")));
        foreach (var season in time.RootElement.GetProperty("seasons").EnumerateArray().Select(s => s.GetProperty("id").GetString()))
        {
            Assert.NotEqual(UiIconChoiceIcon.Unknown, CornerModel.IconFor(Tokens.Zones.SeasonIcons, season));
        }

        using var weather = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "vocabulary", "weather.json")));
        foreach (var id in weather.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetString()))
        {
            Assert.NotEqual(UiIconChoiceIcon.Unknown, CornerModel.IconFor(Tokens.Zones.WeatherIcons, id));
        }

        Assert.Equal(UiIconChoiceIcon.Unknown, CornerModel.IconFor(Tokens.Zones.WeatherIcons, "meteor_shower"));
        Assert.Equal(UiIconChoiceIcon.Unknown, CornerModel.IconFor(Tokens.Zones.WeatherIcons, null));
        Assert.Equal("autumn", CornerModel.SeasonId("time.season.autumn"));
    }

    [Fact]
    public void EachFasterSpeedDrawsOneArrowMore()
    {
        var none = new Dictionary<string, string>();
        TimeChoice[] choices =
        [
            new("||", "ui.time.pause", none, 0),
            new(">", "ui.time.normal", none, 1),
            new(">> ×10", "ui.time.fast", none, 10),
            new(">> ×3", "ui.time.fast", none, 3),
        ];
        Assert.Equal((UiIconChoiceIcon.Pause, 0), CornerModel.SpeedIcon(choices, choices[0]));
        Assert.Equal((UiIconChoiceIcon.Play, 1), CornerModel.SpeedIcon(choices, choices[1]));
        Assert.Equal((UiIconChoiceIcon.Fast, 2), CornerModel.SpeedIcon(choices, choices[3]));
        Assert.Equal((UiIconChoiceIcon.Fast, 3), CornerModel.SpeedIcon(choices, choices[2]));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(-3, "")]
    [InlineData(7, "7")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    public void ARoundButtonShowsItsCountAndNothingForNone(int count, string expected) =>
        Assert.Equal(expected, CornerModel.Badge(count));

    [Fact]
    public void TheFreeMiddleIsCentred()
    {
        var (x, y, w, h) = CornerModel.Middle(1920, 1080, Tokens.Zones.MiddleShare);
        Assert.Equal(1920 * Tokens.Zones.MiddleShare, w, 6);
        Assert.Equal(1080 * Tokens.Zones.MiddleShare, h, 6);
        Assert.Equal(1920, (2 * x) + w, 6);
        Assert.Equal(1080, (2 * y) + h, 6);
    }

    [Theory]
    [InlineData("ada", "A")]
    [InlineData("  Émile", "É")]
    [InlineData("", "?")]
    public void APortraitWithoutPictureShowsTheFirstLetter(string name, string expected) =>
        Assert.Equal(expected, CornerModel.Initial(name));

    [Fact]
    public void ThePortraitBroodsWhileAQuestionPuzzles()
    {
        Assert.True(CornerModel.Brooding(new InhabitantRow("a", 1, 80, false, null, 2, PuzzlingQuestions: 1)));
        Assert.False(CornerModel.Brooding(new InhabitantRow("a", 1, 80, false, null, 2, PuzzlingQuestions: 0)));
    }
}
