using GlassWorld.Presentation.Ui;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1749 (B2825, Marcel W2239): the mouse axes can be turned round, each on its own; the default is as before.</summary>
public sealed class MouseAxesTests
{
    [Fact]
    public void TheDefaultTurnsNothingRound()
    {
        var axes = new PlayerSettings().ToMouseAxes();
        Assert.Equal((5.0, -3.0), axes.Turn(5, -3));
        Assert.Equal((0.2, 0.1), axes.PanTo((0.1, 0.3), (0.2, 0.1)));
    }

    [Fact]
    public void EachAxisTurnsRoundOnItsOwn()
    {
        Assert.Equal((-5.0, -3.0), new MouseAxes(TurnX: true).Turn(5, -3));
        Assert.Equal((5.0, 3.0), new MouseAxes(TurnY: true).Turn(5, -3));

        // Moving: the step from (0.1, 0.3) to (0.2, 0.1) goes the other way on the turned axis only.
        var across = new MouseAxes(PanX: true).PanTo((0.1, 0.3), (0.2, 0.1));
        Assert.Equal(0.0, across.X, 9);
        Assert.Equal(0.1, across.Y, 9);
        var upDown = new MouseAxes(PanY: true).PanTo((0.1, 0.3), (0.2, 0.1));
        Assert.Equal(0.2, upDown.X, 9);
        Assert.Equal(0.5, upDown.Y, 9);
    }

    [Fact]
    public void TheChoiceIsKeptInTheSettingsFile()
    {
        var tokens = GlassWorld.Presentation.Ui.UiStyle.FromJson(
            File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "tokens.json")),
            File.ReadAllText(Path.Combine(TestPaths.Root, "content", "ui", "frames.json"))).Tokens;
        var volume = GlassWorld.Contracts.Validation.ContractSerializer
            .Deserialize<GlassWorld.Contracts.SoundMix>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "sound", "mix.json"))).PlayerVolumeDb;
        var guard = GlassWorld.Simulation.Safety.TextGuard.FromJson(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "safety", "rules.json")));
        var limits = new SettingsLimits(tokens, ["de", "en"], volume, 24, guard);
        var saved = new PlayerSettings { InvertTurnY = true, InvertPanX = true }.ToJson();
        var read = PlayerSettings.Parse(saved, limits);
        Assert.Equal(new MouseAxes(TurnY: true, PanX: true), read.ToMouseAxes());
    }
}
