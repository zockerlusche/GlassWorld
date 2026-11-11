using System.Text.Json;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

public class AddressKeysTests
{
    [Theory]
    [InlineData("female", "ui.card.musing.female")]
    [InlineData("male", "ui.card.musing.male")]
    [InlineData(null, "ui.card.musing")]
    [InlineData("", "ui.card.musing")]
    [InlineData("other", "ui.card.musing")]
    public void ATextThatSpeaksOfAnInhabitantHasAFormPerSexAndThePlainOneOtherwise(string? sex, string expected)
    {
        Assert.Equal(expected, AddressKeys.For("ui.card.musing", sex));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void EverySexedKeyHasBothFormsAndItsPlainTextInTheCatalogue(string language)
    {
        // The forms are built from the plain key, so the check of used keys does not see them (D747 A4).
        var texts = Texts(language);
        foreach (var key in AddressKeys.Sexed)
        {
            Assert.Contains(key, texts.Keys);
            Assert.Contains(key + ".female", texts.Keys);
            Assert.Contains(key + ".male", texts.Keys);
        }
    }

    [Fact]
    public void GermanSpeaksOfAWomanAsSheAndOfAManAsHe()
    {
        var texts = Texts("de");
        foreach (var key in AddressKeys.Sexed)
        {
            var female = " " + texts[key + ".female"].ToLowerInvariant() + " ";
            var male = " " + texts[key + ".male"].ToLowerInvariant() + " ";
            Assert.True(female.Contains(" sie ", StringComparison.Ordinal) || female.Contains(" ihr ", StringComparison.Ordinal), $"{key}.female: {female}");
            Assert.True(male.Contains(" er ", StringComparison.Ordinal) || male.Contains(" ihn ", StringComparison.Ordinal) || male.Contains(" ihm ", StringComparison.Ordinal), $"{key}.male: {male}");
            Assert.DoesNotContain(" er ", female, StringComparison.Ordinal);
            Assert.DoesNotContain(" ihn ", female, StringComparison.Ordinal);
            Assert.DoesNotContain(" ihm ", female, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoOtherTextOfTheCatalogueHasASexedFormWithoutBeingListed()
    {
        // A form added to the catalogue without its entry in the list would never be shown.
        var listed = AddressKeys.Sexed.SelectMany(k => new[] { k + ".female", k + ".male" }).ToHashSet(StringComparer.Ordinal);
        foreach (var key in Texts("de").Keys.Where(k => k.EndsWith(".female", StringComparison.Ordinal) || k.EndsWith(".male", StringComparison.Ordinal)))
        {
            Assert.Contains(key, listed);
        }
    }

    private static Dictionary<string, string> Texts(string language)
    {
        using var catalogue = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "i18n", language + ".json")));
        return catalogue.RootElement.GetProperty("entries").EnumerateArray()
            .ToDictionary(e => e.GetProperty("key").GetString()!, e => e.GetProperty("text").GetString()!, StringComparer.Ordinal);
    }
}
