using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.Ui;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>The inhabitant card on the right (A1683, D957 v4 section 2): bar rule, top question, diary and conversation lines.</summary>
public sealed class CardModelTests
{
    private static readonly UiTokens Tokens = ContractSerializer.Deserialize<UiTokens>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "tokens.json")));
    private static readonly Bars Bars = Tokens.Bars;

    [Theory]
    [InlineData("vocab.need.hunger", BarKind.State)]
    [InlineData("ui.card.will_to_live", BarKind.State)]
    [InlineData("vocab.trait.courage", BarKind.Neutral)]
    [InlineData("vocab.mood.joy", BarKind.Uplift)]
    [InlineData("vocab.mood.fear", BarKind.Burden)]
    [InlineData("vocab.mood.not_in_the_data", BarKind.Neutral)]
    public void EveryBarHasItsKindFromTheKeyAndTheData(string key, BarKind kind) =>
        Assert.Equal(kind, CardModel.KindOf(key, Bars));

    [Fact]
    public void EveryFeelingOfTheVocabularyIsNamedInTheData()
    {
        var moods = File.ReadAllText(Path.Combine(TestPaths.Root, "content", "i18n", "de.json"));
        var ids = System.Text.RegularExpressions.Regex.Matches(moods, "\"vocab\\.mood\\.([a-z_]+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.Contains(Bars.Moods, m => m.Id == id));
    }

    [Fact]
    public void AStateTurnsBadBelowTheThresholdAndGoodFromTheOther()
    {
        Assert.Equal(BarLook.Bad, CardModel.Judge(BarKind.State, Bars.StateBadBelow - 1, Bars));
        Assert.Equal(BarLook.Calm, CardModel.Judge(BarKind.State, Bars.StateBadBelow, Bars));
        Assert.Equal(BarLook.Calm, CardModel.Judge(BarKind.State, Bars.StateGoodFrom - 1, Bars));
        Assert.Equal(BarLook.Good, CardModel.Judge(BarKind.State, Bars.StateGoodFrom, Bars));
        Assert.Equal(BarLook.Good, CardModel.Judge(BarKind.State, 100, Bars));
    }

    [Fact]
    public void ABurdenIsBadWhenStrongAndNeverGood()
    {
        Assert.Equal(BarLook.Calm, CardModel.Judge(BarKind.Burden, Bars.BurdenBadFrom - 1, Bars));
        Assert.Equal(BarLook.Bad, CardModel.Judge(BarKind.Burden, Bars.BurdenBadFrom, Bars));
        Assert.Equal(BarLook.Bad, CardModel.Judge(BarKind.Burden, 100, Bars));
    }

    [Fact]
    public void UpliftIsNeverBadAndCharacterIsNeverJudged()
    {
        Assert.Equal(BarLook.Calm, CardModel.Judge(BarKind.Uplift, 0, Bars));
        Assert.Equal(BarLook.Good, CardModel.Judge(BarKind.Uplift, 100, Bars));
        foreach (var percent in new[] { 0, 10, 50, 90, 100 })
        {
            Assert.Equal(BarLook.Calm, CardModel.Judge(BarKind.Neutral, percent, Bars));
        }
    }

    [Fact]
    public void TheTopQuestionStaysUntilItIsGone()
    {
        var old = Question("q1", 10);
        var newer = Question("q2", 20);
        var newest = Question("q3", 5);

        // Without one shown so far: the oldest.
        Assert.Equal("q3", CardModel.TopQuestion([old, newer, newest], null)?.Id);

        // A question shown on top stays while it is open, even when an older one comes in (B2595: nothing jumps).
        Assert.Equal("q2", CardModel.TopQuestion([old, newer, newest], "q2")?.Id);

        // Answered or solved: the next oldest moves up; none left: none.
        Assert.Equal("q1", CardModel.TopQuestion([old, newer], "q3")?.Id);
        Assert.Null(CardModel.TopQuestion([], "q1"));
    }

    [Fact]
    public void TheOtherQuestionsShowEachThingOnce()
    {
        var top = Question("q1", 1, "flint");
        var same = Question("q2", 2, "flint");
        var reed = Question("q3", 3, "reed");
        var reedAgain = Question("q4", 4, "reed");
        var who = Question("q5", 5, null, "who");

        var others = CardModel.OtherQuestions([who, reedAgain, reed, same, top], top);
        Assert.Equal(["q3", "q5"], others.Select(q => q.Id));
    }

    [Fact]
    public void TheDiaryShowsTheNewestAndTheTalkTheLastLines()
    {
        Assert.Equal([9, 8, 7], CardModel.Newest([9, 8, 7, 6, 5], Tokens.Window.CardDiaryLines));
        Assert.Equal([4, 5], CardModel.Newest([4, 5], 3));
        Assert.Equal([3, 4, 5], CardModel.Last([1, 2, 3, 4, 5], 3));
        Assert.Empty(CardModel.Last([1, 2], 0));
    }

    private static QuestionRow Question(string id, long tick, string? type = "flint", string kind = "what") =>
        new(id, "inh-000001", kind, type, tick);
}
