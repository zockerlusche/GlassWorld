using GlassWorld.Presentation.Ui;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1749 part 2a (D957 v4 section 10): autosave, start speed, pause on knock - defaults as before, only known values.</summary>
public sealed class GameOptionsTests
{
    private static SettingsLimits Limits()
    {
        var tokens = UiStyle.FromJson(
            File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "tokens.json")),
            File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "frames.json"))).Tokens;
        var volume = GlassWorld.Contracts.Validation.ContractSerializer
            .Deserialize<GlassWorld.Contracts.SoundMix>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "sound", "mix.json"))).PlayerVolumeDb;
        var guard = GlassWorld.Simulation.Safety.TextGuard.FromJson(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "safety", "rules.json")));
        return new SettingsLimits(tokens, ["de", "en"], volume, 24, guard) { SpeedChoices = [0, 1, 4, 16] };
    }

    [Fact]
    public void TheDefaultsAreAsBefore()
    {
        var start = PlayerSettings.Parse(null, Limits());
        Assert.Equal((GameOptions.AutoSaveDay, (int?)null, false), (start.AutoSave, start.StartSpeed, start.PauseOnKnock));
    }

    [Fact]
    public void OnlyKnownModesAndSpeedsSurvive()
    {
        var odd = new PlayerSettings { AutoSave = "hourly", StartSpeed = 7 }.Clamp(Limits());
        Assert.Equal((GameOptions.AutoSaveDay, (int?)null), (odd.AutoSave, odd.StartSpeed));
        var fine = new PlayerSettings { AutoSave = GameOptions.AutoSaveNight, StartSpeed = 0, PauseOnKnock = true }.Clamp(Limits());
        Assert.Equal((GameOptions.AutoSaveNight, (int?)0, true), (fine.AutoSave, fine.StartSpeed, fine.PauseOnKnock));
        Assert.Equal(fine.AutoSave, PlayerSettings.Parse(fine.ToJson(), Limits()).AutoSave);
    }

    [Theory]
    [InlineData("day", true, false, true)]
    [InlineData("day", false, true, false)]
    [InlineData("night", false, true, true)]
    [InlineData("night", true, false, false)]
    [InlineData("off", true, true, false)]
    public void TheWorldSavesWhenItsModeSaysSo(string mode, bool newDay, bool nightBegan, bool saves) =>
        Assert.Equal(saves, GameOptions.SavesNow(mode, newDay, nightBegan));
}
