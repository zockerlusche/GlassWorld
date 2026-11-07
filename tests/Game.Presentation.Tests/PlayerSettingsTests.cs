using GlassWorld.Presentation.Ui;

namespace GlassWorld.Presentation.Tests;

/// <summary>Player settings (B6 R4): round trip, defaults, clamping, broken files.</summary>
public sealed class PlayerSettingsTests
{
    private static readonly GlassWorld.Contracts.UiTokens Tokens = UiStyle.FromJson(
        File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "tokens.json")),
        File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "frames.json"))).Tokens;

    [Fact]
    public void RoundTripKeepsEverything()
    {
        var settings = new PlayerSettings
        {
            Language = "de",
            UiScale = 1.25,
            TextScale = 1.5,
            HighContrast = true,
            ReduceMotion = true,
            HintDurationFactor = 4,
            HintsUntilDismissed = true,
            HideUi = true,
            BusVolumesDb = new(StringComparer.Ordinal) { ["music"] = -12 },
        };
        var back = PlayerSettings.Parse(settings.ToJson(), Tokens);
        Assert.Equal(settings.ToJson(), back.ToJson());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("""{ "version": 99, "ui_scale": 2 }""")]
    public void MissingBrokenOrNewerFilesGiveDefaults(string? json)
    {
        var settings = PlayerSettings.Parse(json, Tokens);
        Assert.Equal(1, settings.UiScale);
        Assert.Null(settings.Language);
    }

    [Fact]
    public void HandEditedValuesAreClamped()
    {
        var settings = PlayerSettings.Parse("""{ "language": "fr", "ui_scale": 9, "text_scale": 0.1, "hint_duration_factor": 500, "bus_volumes_db": { "music": 40 } }""", Tokens);
        Assert.Null(settings.Language);
        Assert.Equal(2, settings.UiScale);
        Assert.Equal(0.75, settings.TextScale);
        Assert.Equal(Tokens.Toast.MaxDurationFactor, settings.HintDurationFactor);
        Assert.Equal(6, settings.BusVolumesDb["music"]);
    }

    [Fact]
    public void PartsMapToUiAndHints()
    {
        var settings = new PlayerSettings { UiScale = 1.5, ReduceMotion = true, HintDurationFactor = 3, HintsUntilDismissed = true };
        Assert.Equal(new UiSettings(1.5, 1, false, true), settings.ToUiSettings());
        Assert.Equal(new ToastSettings(3, true), settings.ToToastSettings());
    }
}
