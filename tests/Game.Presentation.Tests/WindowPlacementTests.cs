using GlassWorld.Presentation.Ui;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1789 (B2826): windows remember their place, pulled into the safe area when it no longer fits.</summary>
public sealed class WindowPlacementTests
{
    [Fact]
    public void ARememberedPlaceThatFitsIsKept()
    {
        Assert.Equal((300f, 200f), WindowPlacement.Restore((300, 200), (400, 300), (1920, 1080), 24));
    }

    [Fact]
    public void APlaceThatNoLongerFitsIsPulledInsideTheSafeArea()
    {
        // The window grew or the picture got smaller: it stays whole and inside the margin.
        Assert.Equal((1920f - 24 - 400, 1080f - 24 - 300), WindowPlacement.Restore((1800, 1000), (400, 300), (1920, 1080), 24));
        Assert.Equal((24f, 24f), WindowPlacement.Restore((-50, 5), (400, 300), (1920, 1080), 24));
    }

    [Fact]
    public void PlacesSurviveTheSettingsFileAndBadOnesAreDropped()
    {
        Assert.Equal("301,-2", WindowPlacement.Format((300.6f, -2.4f)));
        Assert.Equal((301f, -2f), WindowPlacement.Parse("301,-2"));
        Assert.Null(WindowPlacement.Parse("left"));
        var clean = WindowPlacement.Clean(new Dictionary<string, string> { ["ui.memory.title"] = "10,20", ["ui.thing.title"] = "x", [" "] = "1,1" });
        Assert.Equal(new Dictionary<string, string> { ["ui.memory.title"] = "10,20" }, clean);
    }
}
