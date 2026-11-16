using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1356 (B8-11): written things say what they hold in DE and EN; their marks fade with the text.</summary>
public sealed class WritingLookTests
{
    private static T Read<T>(params string[] path) where T : class =>
        ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine([TestPaths.Root, "content", .. path])));

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void AWrittenBeliefIsSaidInThePlayersLanguage(string locale)
    {
        var texts = Read<I18n>("i18n", locale + ".json").Entries.ToDictionary(e => e.Key, e => e.Text);
        string Tr(string key) => texts.TryGetValue(key, out var text) ? text : key;
        var says = WritingLook.Says("belief:red_berry|poisonous|true", Tr, type => Tr(VocabKeys.ObjectType(type)));
        Assert.NotNull(says);
        Assert.Contains(Tr(VocabKeys.ObjectType("red_berry")), says);
        Assert.DoesNotContain("vocab.", says);
        var not = WritingLook.Says("belief:red_berry|poisonous|false", Tr, type => Tr(VocabKeys.ObjectType(type)));
        Assert.NotEqual(says, not);
        Assert.Null(WritingLook.Says("story:42", Tr, type => type));
        foreach (var key in new[] { "ui.writing.on", "ui.writing.legible", "ui.writing.faint", "ui.writing.hard", "ui.writing.unknown" })
        {
            Assert.True(texts.ContainsKey(key), key);
        }
    }

    [Fact]
    public void TheMarksFadeWithTheTextAndEveryCarrierOfTheDataHasThem()
    {
        var look = Read<LookWriting>("look", "writing.json");
        Assert.Equal(1.0, WritingLook.Alpha(0.9, look.FaintBelow));
        Assert.True(WritingLook.Alpha(0.1, look.FaintBelow) < WritingLook.Alpha(0.4, look.FaintBelow));
        Assert.Equal("ui.writing.hard", WritingLook.LegibilityKey(0.1, look.FaintBelow, look.HardBelow));
        Assert.Equal("ui.writing.faint", WritingLook.LegibilityKey(0.4, look.FaintBelow, look.HardBelow));
        Assert.Equal("ui.writing.legible", WritingLook.LegibilityKey(0.9, look.FaintBelow, look.HardBelow));

        var media = Read<WritingMedia>("writing", "media.json").Media.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var palette = Read<LookPalette>("look", "palette.json").Colors.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(look.Media, m =>
        {
            Assert.Contains(m.Medium, media);
            Assert.Contains(m.Color, palette);
        });
        Assert.All(media, id => Assert.Contains(look.Media, m => m.Medium == id));
    }
}
