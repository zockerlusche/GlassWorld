using GlassWorld.Presentation.Ui;

namespace GlassWorld.Presentation.Tests;

public sealed class ScreenZonesTests
{
    [Fact]
    public void EverySideWindowButTheLogBelongsToExactlyOneTheme()
    {
        string[] windows = ["inhabitants", "card", "questions", "log", "chronicle", "timeline", "help", "mailbox"];
        foreach (var window in windows.Where(w => w != ScreenZones.Pinned))
        {
            Assert.Single(ScreenZones.Themes, t => t.Panels.Contains(window));
        }

        Assert.Null(ScreenZones.ThemeOf(ScreenZones.Pinned));
        Assert.Null(ScreenZones.ThemeOf(null));
    }

    [Fact]
    public void AThemeButtonOpensTheLastWindowOfItsThemeAndTogglesItsOwn()
    {
        var contact = ScreenZones.Themes.Single(t => t.Id == "contact");
        Assert.Equal("questions", ScreenZones.Open(contact, null, null));
        Assert.Equal("mailbox", ScreenZones.Open(contact, "inhabitants", "mailbox"));
        Assert.Equal("questions", ScreenZones.Open(contact, null, "chronicle"));
        Assert.Null(ScreenZones.Open(contact, "help", "help"));

        // The card is a tab of the people theme: its button closes it, and opens it again when it was last shown.
        var people = ScreenZones.Themes.Single(t => t.Id == "people");
        Assert.Null(ScreenZones.Open(people, ScreenZones.Card, ScreenZones.Card));
        Assert.Equal(ScreenZones.Card, ScreenZones.Open(people, null, ScreenZones.Card));
    }
}
