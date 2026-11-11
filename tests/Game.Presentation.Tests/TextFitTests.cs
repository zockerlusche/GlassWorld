using GlassWorld.Presentation.Ui;

namespace GlassWorld.Presentation.Tests;

public class TextFitTests
{
    [Theory]
    [InlineData(260, 147, 1920, 260)] // a short hint: the least width
    [InlineData(260, 392, 1920, 392)] // the hint fits: as wide as the hint
    [InlineData(260, 900, 1920, 768)] // more than the share of the picture: the share
    [InlineData(260, 900, 500, 260)] // a small picture: the least width wins over the share
    public void AFieldIsAsWideAsItsHintBetweenTheLeastWidthAndItsShareOfThePicture(double least, double wanted, double picture, double expected)
    {
        Assert.Equal(expected, TextFit.FieldWidth(least, wanted, picture, 0.4));
    }

    [Fact]
    public void WithoutAKnownPictureAFieldTakesWhatItsHintNeeds()
    {
        Assert.Equal(900, TextFit.FieldWidth(260, 900, null, 0.4));
        Assert.Null(TextFit.FieldLimit(260, null, 0.4));
    }

    [Fact]
    public void AHintIsCutOnlyWhileTheFieldHasNotReachedItsLimit()
    {
        Assert.False(TextFit.HintCut(392, 392, 768));
        Assert.True(TextFit.HintCut(300, 392, 768)); // room left, yet too narrow: a layout fault
        Assert.False(TextFit.HintCut(768, 900, 768)); // at the limit: the end of the room
        Assert.False(TextFit.HintCut(260, 392, TextFit.FieldLimit(260, 500, 0.4))); // the least width is the limit
        Assert.True(TextFit.HintCut(300, 392, null));
    }

    [Fact]
    public void ALabelIsAsWideAsItsLongestWord()
    {
        static double Measure(string word) => word.Length * 10;
        Assert.Equal(160, TextFit.LongestWord("Gesamtlautstärke (alles)", Measure));
        Assert.Equal(0, TextFit.LongestWord("  ", Measure));
        Assert.True(TextFit.WordBroken(140, 160));
        Assert.False(TextFit.WordBroken(160, 160));
    }
}
