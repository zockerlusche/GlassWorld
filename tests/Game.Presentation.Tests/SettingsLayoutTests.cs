using System.Text.Json.Nodes;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1737 (D957 v4 section 10): the settings window's tabs and rows as data, every row with its words in DE and EN.</summary>
public sealed class SettingsLayoutTests
{
    private static readonly SettingsLayout Layout = ContractSerializer.Deserialize<SettingsLayout>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "settings.json")));

    private static HashSet<string> Keys(string lang) =>
        [.. JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "i18n", lang + ".json")))!["entries"]!.AsArray().Select(e => (string)e!["key"]!)];

    [Fact]
    public void SevenTabsInTheOrderOfTheConcept()
    {
        // Game, graphics, sound, interface, controls, accessibility, AI - like Cities: Skylines II, RimWorld and The Sims 4.
        Assert.Equal(["game", "graphics", "audio", "interface", "controls", "accessibility", "ai"], Layout.Tabs.Select(t => t.Id));
    }

    [Fact]
    public void EverySettingStandsInExactlyOneTab()
    {
        var rows = Layout.Tabs.SelectMany(t => t.Rows).ToList();
        Assert.Equal(rows.Count, rows.Distinct(StringComparer.Ordinal).Count());
        foreach (var setting in new[] { "language", "player_name", "volume", "ui_scale", "text_scale", "feeling_symbols", "hint_duration", "hints_until_dismissed", "hide_ui", "high_contrast", "reduce_motion", "ai", "data_folder", "window_mode", "resolution", "vsync", "max_fps", "quality" })
        {
            Assert.Contains(setting, rows);
        }

        // Accessibility apart from the interface (D957: contrast and motion were mixed with "hide UI" before).
        Assert.Equal(["high_contrast", "reduce_motion"], Layout.Tabs.Single(t => t.Id == "accessibility").Rows);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void EveryTabAndRowHasItsWordsAndItsDescription(string lang)
    {
        var keys = Keys(lang);
        foreach (var tab in Layout.Tabs)
        {
            Assert.Contains("ui.settings.tab." + tab.Id, keys);
            foreach (var row in tab.Rows.Where(r => r != "ai"))
            {
                // The volume row stands for one slider per channel (ui.settings.volume.<bus>), all with one description.
                if (row != "volume")
                {
                    Assert.Contains("ui.settings." + row, keys);
                }

                Assert.Contains("ui.settings." + row + ".desc", keys);
            }
        }

        Assert.Contains("ui.settings.restore", keys);
        Assert.Contains("ui.settings.coming", keys);
    }
}
