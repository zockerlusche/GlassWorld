using GlassWorld.Presentation.Ui;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1815 (A1749 rest): keys laid anew - a choice replaces the defaults, conflicts are found, Escape stays the menu's.</summary>
public sealed class KeyBindingsTests
{
    private static readonly (string Action, IReadOnlyList<string> Keys)[] Defaults =
    [
        ("gw_camera_pan_forward", ["W", "Up"]),
        ("gw_camera_pan_left", ["A", "Left"]),
        ("gw_time_pause", ["Space", "P"]),
        ("gw_time_normal", ["1"]),
    ];

    [Fact]
    public void WithoutAChoiceTheDefaultsHoldAndNothingClashes()
    {
        var effective = KeyBindings.Effective(Defaults, new Dictionary<string, string>());
        Assert.Equal(["W", "Up"], effective[0].Keys);
        Assert.Empty(KeyBindings.Conflicts(effective));
    }

    [Fact]
    public void AChosenKeyReplacesTheDefaultsAndAClashIsNamed()
    {
        var effective = KeyBindings.Effective(Defaults, new Dictionary<string, string> { ["gw_time_normal"] = "W" });
        Assert.Equal(["W"], effective[3].Keys);
        var clash = Assert.Single(KeyBindings.Conflicts(effective));
        Assert.Equal("W", clash.Key);
        Assert.Equal(["gw_camera_pan_forward", "gw_time_normal"], clash.Actions);
    }

    [Fact]
    public void AHandEditedFileKeepsOnlyKnownActionsAndNeverEscape()
    {
        var clean = KeyBindings.Clean(new Dictionary<string, string> { ["gw_time_pause"] = "Escape", ["gw_nothing"] = "K", ["gw_time_normal"] = "N", ["gw_camera_pan_left"] = " " }, [.. Defaults.Select(d => d.Action)]);
        Assert.Equal(new Dictionary<string, string> { ["gw_time_normal"] = "N" }, clean);
    }

    [Fact]
    public void KeyRowsAreFoundInTheData()
    {
        Assert.Equal("gw_camera_reset", KeyBindings.ActionOfRow("key.gw_camera_reset"));
        Assert.Null(KeyBindings.ActionOfRow("vsync"));
        Assert.Null(KeyBindings.ActionOfRow("key."));
    }
}
