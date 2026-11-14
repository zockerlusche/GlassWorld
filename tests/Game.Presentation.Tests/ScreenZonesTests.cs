using GlassWorld.Presentation.Ui;

namespace GlassWorld.Presentation.Tests;

public sealed class ScreenZonesTests
{
    [Fact]
    public void EverySideWindowButTheLogAndHelpBelongsToExactlyOneRoundButton()
    {
        string[] windows = ["inhabitants", "card", "questions", "log", "chronicle", "timeline", "help", "mailbox"];
        foreach (var window in windows.Where(w => w is not ScreenZones.Pinned and not ScreenZones.FromMenu))
        {
            Assert.Single(ScreenZones.Themes, t => t.Panels.Contains(window));
        }

        // Help and gifts open from the menu (TL #23224); the log stays pinned.
        Assert.Null(ScreenZones.ThemeOf(ScreenZones.FromMenu));
        Assert.Null(ScreenZones.ThemeOf(ScreenZones.Pinned));
        Assert.Null(ScreenZones.ThemeOf(null));
    }

    [Fact]
    public void TheRoundButtonsComeInTheOrderOfTheConcept()
    {
        // D957 section 1: menu (its own button), then questions, mail, chronicle, inhabitants.
        Assert.Equal(["questions", "mailbox", "chronicle", "people"], ScreenZones.Themes.Select(t => t.Id));
        Assert.Equal(ScreenZones.Themes.Count, ScreenZones.Themes.Select(t => t.Icon).Distinct().Count());
    }

    [Fact]
    public void ARoundButtonOpensTheLastWindowOfItsThemeAndTogglesItsOwn()
    {
        var chronicle = ScreenZones.Themes.Single(t => t.Id == "chronicle");
        Assert.Equal("chronicle", ScreenZones.Open(chronicle, null, null));
        Assert.Equal(ScreenZones.Timeline, ScreenZones.Open(chronicle, "inhabitants", ScreenZones.Timeline));
        Assert.Equal("chronicle", ScreenZones.Open(chronicle, null, "mailbox"));
        Assert.Null(ScreenZones.Open(chronicle, ScreenZones.Timeline, ScreenZones.Timeline));

        // The card is a tab of the people theme: its button closes it, and opens it again when it was last shown.
        var people = ScreenZones.Themes.Single(t => t.Id == "people");
        Assert.Null(ScreenZones.Open(people, ScreenZones.Card, ScreenZones.Card));
        Assert.Equal(ScreenZones.Card, ScreenZones.Open(people, null, ScreenZones.Card));

        // Help from the menu stands in the same place: a round button replaces it.
        var questions = ScreenZones.Themes.Single(t => t.Id == "questions");
        Assert.Equal("questions", ScreenZones.Open(questions, ScreenZones.FromMenu, null));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void EveryRoundButtonAndTabHasItsText(string language)
    {
        // The keys are built from the panel's name, so the check of used keys does not see them: a panel
        // added to a theme without its texts would show the raw key (review GW1 #226).
        using var catalogue = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "i18n", language + ".json")));
        var keys = catalogue.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToHashSet();
        foreach (var theme in ScreenZones.Themes)
        {
            Assert.Contains(theme.TitleKey, keys);
            foreach (var panel in theme.Panels)
            {
                Assert.Contains(ScreenZones.TitleKey(panel), keys);
                if (theme.Panels.Count > 1)
                {
                    Assert.Contains(ScreenZones.TabKey(panel), keys);
                }
            }
        }
    }
}
