using GlassWorld.Presentation.Ui;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1749 part 2a (D957 v4 section 10): autosave and pause on knock - defaults as before, only known values (no start speed: Marcel W2270).</summary>
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
        return new SettingsLimits(tokens, ["de", "en"], volume, 24, guard);
    }

    [Fact]
    public void TheDefaultsAreAsBefore()
    {
        var start = PlayerSettings.Parse(null, Limits());
        Assert.Equal((GameOptions.AutoSaveDay, false), (start.AutoSave, start.PauseOnKnock));
    }

    [Fact]
    public void OnlyKnownModesAndSpeedsSurvive()
    {
        var odd = new PlayerSettings { AutoSave = "hourly" }.Clamp(Limits());
        Assert.Equal(GameOptions.AutoSaveDay, odd.AutoSave);
        var fine = new PlayerSettings { AutoSave = GameOptions.AutoSaveNight, PauseOnKnock = true }.Clamp(Limits());
        Assert.Equal((GameOptions.AutoSaveNight, true), (fine.AutoSave, fine.PauseOnKnock));
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
