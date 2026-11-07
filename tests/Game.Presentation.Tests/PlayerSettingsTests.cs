using GlassWorld.Presentation.Ui;

namespace GlassWorld.Presentation.Tests;

/// <summary>Player settings (B6 R4): round trip, defaults, clamping, broken files.</summary>
public sealed class PlayerSettingsTests
{
    private static readonly GlassWorld.Contracts.UiTokens Tokens = UiStyle.FromJson(
        File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "tokens.json")),
        File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "frames.json"))).Tokens;

    private static readonly GlassWorld.Contracts.MixPlayerRange Volume = GlassWorld.Contracts.Validation.ContractSerializer
        .Deserialize<GlassWorld.Contracts.SoundMix>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "sound", "mix.json"))).PlayerVolumeDb;

    private static readonly SettingsLimits Limits = new(Tokens, ["de", "en"], Volume, 24);

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
        var back = PlayerSettings.Parse(settings.ToJson(), Limits);
        Assert.Equal(settings.ToJson(), back.ToJson());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("""{ "version": 99, "ui_scale": 2 }""")]
    public void MissingBrokenOrNewerFilesGiveDefaults(string? json)
    {
        var settings = PlayerSettings.Parse(json, Limits);
        Assert.Equal(1, settings.UiScale);
        Assert.Null(settings.Language);
    }

    [Fact]
    public void HandEditedValuesAreClamped()
    {
        var settings = PlayerSettings.Parse("""{ "language": "fr", "ui_scale": 9, "text_scale": 0.1, "hint_duration_factor": 500, "bus_volumes_db": { "music": 40 } }""", Limits);
        Assert.Null(settings.Language);
        Assert.Equal(2, settings.UiScale);
        Assert.Equal(0.75, settings.TextScale);
        Assert.Equal(Tokens.Toast.MaxDurationFactor, settings.HintDurationFactor);
        Assert.Equal(Volume.Max, settings.BusVolumesDb["music"]);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  Mara  ", "Mara")]
    [InlineData("Ma\u0007ra", "Mara")]
    [InlineData("Abcdefghijklmnopqrstuvwxyz", "Abcdefghijklmnopqrstuvwx")]
    public void PlayerNameFromAHandEditedFileIsCleaned(string? name, string? expected)
        => Assert.Equal(expected, (new PlayerSettings { PlayerName = name }).Clamp(Limits).PlayerName);

    [Fact]
    public void NullVolumesFromAHandEditedFileAreEmpty()
    {
        var settings = PlayerSettings.Parse("""{ "bus_volumes_db": null }""", Limits);
        Assert.Empty(settings.BusVolumesDb);
    }

    [Fact]
    public void PartsMapToUiAndHints()
    {
        var settings = new PlayerSettings { UiScale = 1.5, ReduceMotion = true, HintDurationFactor = 3, HintsUntilDismissed = true };
        Assert.Equal(new UiSettings(1.5, 1, false, true), settings.ToUiSettings());
        Assert.Equal(new ToastSettings(3, true), settings.ToToastSettings());
    }
}
