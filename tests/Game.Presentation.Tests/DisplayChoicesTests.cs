using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.Ui;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1748 (D957 v4 section 10): the graphics settings - only the data's choices, and 'Behalten?' falls back after its seconds.</summary>
public sealed class DisplayChoicesTests
{
    private static readonly UiTokens Tokens = UiStyle.FromJson(
        File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "tokens.json")),
        File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "frames.json"))).Tokens;

    private static readonly MixPlayerRange Volume =
        ContractSerializer.Deserialize<SoundMix>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "sound", "mix.json"))).PlayerVolumeDb;

    private static readonly GlassWorld.Simulation.Safety.TextGuard Guard =
        GlassWorld.Simulation.Safety.TextGuard.FromJson(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "safety", "rules.json")));

    private static SettingsLimits Limits() =>
        new(Tokens, ["de", "en"], Volume, 24, Guard)
        {
            QualityLevels = ["low", "medium", "high"],
            QualityDefault = "high",
        };

    [Fact]
    public void OnlyTheChoicesOfTheDataSurviveAHandEditedFile()
    {
        var limits = Limits();
        var fine = new PlayerSettings { WindowMode = "borderless", Resolution = Tokens.Graphics.Resolutions.First(), MaxFps = Tokens.Graphics.FpsChoices.First(), Quality = "low", Vsync = false }.Clamp(limits);
        Assert.Equal(("borderless", Tokens.Graphics.Resolutions.First(), Tokens.Graphics.FpsChoices.First(), "low", false), (fine.WindowMode, fine.Resolution, fine.MaxFps, fine.Quality, fine.Vsync));

        var odd = new PlayerSettings { WindowMode = "huge", Resolution = "123x45", MaxFps = 77, Quality = "ultra" }.Clamp(limits);
        Assert.Equal(((string?)null, (string?)null, 0, (string?)null), (odd.WindowMode, odd.Resolution, odd.MaxFps, odd.Quality));

        // The defaults change nothing: the window as it starts, V-Sync on, no limit, the data's quality.
        var start = PlayerSettings.Parse(null, limits);
        Assert.Equal(((string?)null, (string?)null, true, 0, (string?)null), (start.WindowMode, start.Resolution, start.Vsync, start.MaxFps, start.Quality));
    }

    [Fact]
    public void OnlyAChangeOfModeOrSizeAsksAndFallingBackKeepsTheRest()
    {
        var before = new PlayerSettings { WindowMode = "windowed", Resolution = "1600x900", MaxFps = 60 };
        Assert.False(DisplayChoices.NeedsKeep(before, before with { MaxFps = 30, Vsync = false, Quality = "low" }));
        Assert.True(DisplayChoices.NeedsKeep(before, before with { WindowMode = "fullscreen" }));
        Assert.True(DisplayChoices.NeedsKeep(before, before with { Resolution = "1920x1080" }));

        var now = before with { WindowMode = "fullscreen", Resolution = "1920x1080", MaxFps = 144 };
        var back = DisplayChoices.FallBack(now, before);
        Assert.Equal(("windowed", "1600x900", 144), (back.WindowMode, back.Resolution, back.MaxFps));
    }

    [Fact]
    public void BehaltenFallsBackAfterItsSecondsUnlessKept()
    {
        // D957 v4 test: 'Behalten?' falls back after 10 s.
        Assert.Equal(10, Tokens.Graphics.KeepSeconds);
        var countdown = new KeepCountdown(Tokens.Graphics.KeepSeconds);
        Assert.Equal(10, countdown.SecondsLeft);
        var fell = 0;
        for (var i = 0; i < 39; i++)
        {
            fell += countdown.Tick(0.25) ? 1 : 0;
        }

        Assert.Equal((0, 1), (fell, countdown.SecondsLeft)); // 9.75 s passed: still asking, '1 s' left
        Assert.True(countdown.Tick(0.25)); // at 10 s it falls back - once
        Assert.False(countdown.Tick(0.25));
        Assert.True(countdown.Over);
    }

    [Fact]
    public void OnlySizesThatFitTheScreenAreOffered()
    {
        Assert.Equal(["1280x720", "1366x768", "1600x900", "1920x1080"], DisplayChoices.Fitting(Tokens.Graphics.Resolutions, 1920, 1080));
        Assert.Equal((2560, 1440), DisplayChoices.Size("2560x1440"));
        Assert.Null(DisplayChoices.Size("big"));
    }
}
