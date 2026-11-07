using GlassWorld.Presentation.Ui;

namespace GlassWorld.Presentation.Tests;

/// <summary>Key recognition for the raw-key check (B6 acceptance 11).</summary>
public sealed class TextKeysTests
{
    [Theory]
    [InlineData("ui.menu.quit", true)]
    [InlineData("vocab.need.hunger", true)]
    [InlineData("msg.save.failed", true)]
    [InlineData("Beenden", false)]
    [InlineData("Bewohner 3", false)]
    [InlineData("125 %", false)]
    [InlineData("ui", false)]
    [InlineData("1.5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void KeysAreRecognised(string? text, bool expected) => Assert.Equal(expected, TextKeys.LooksLikeKey(text));
}
