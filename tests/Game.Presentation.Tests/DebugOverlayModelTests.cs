using System.Text.Json;
using GlassWorld.Presentation.Ui;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Look;

namespace GlassWorld.Presentation.Tests;

/// <summary>The debug view without Godot (B2-6 R31): its lines against the real core and the real texts.</summary>
public sealed class DebugOverlayModelTests
{
    private static readonly string Content = Path.Combine(TestPaths.Root, "content");

    private static SimulationHost Island(bool debug = false) =>
        SimulationHost.CreateGeneratedWorld(Content, 7, new MockDecisionPort(), "de", "start_island", options: new SimulationHostOptions(debug));

    private static Func<string, string> Texts(string locale)
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(Content, "i18n", locale + ".json")));
        var texts = catalog.RootElement.GetProperty("entries").EnumerateArray()
            .ToDictionary(e => e.GetProperty("key").GetString()!, e => e.GetProperty("text").GetString()!, StringComparer.Ordinal);
        return key => texts.GetValueOrDefault(key, key);
    }

    private static (double X, double Z) SomeCell(SimulationHost host)
    {
        var centre = host.Map!.CentreOf(host.Map.Map.WidthCells / 2, host.Map.Map.DepthCells / 2);
        return (centre.X, centre.Z);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void EveryValueOfAnIslandIsShownWithoutRawKeys(string locale)
    {
        var host = Island(debug: true);
        var hash = host.World.StateHash();
        var data = DebugOverlayModel.Read(host, hash, host.World.Tick, 59.6, SomeCell(host), ("PageDown", "PageUp"));
        var lines = DebugOverlayModel.Lines(data, Texts(locale));

        Assert.Equal(11, lines.Count);
        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line)));
        Assert.Empty(DebugOverlayModel.RawKeys(lines));
        Assert.EndsWith(" 7", lines[0], StringComparison.Ordinal);
        Assert.Contains(host.Map!.IslandHash[..DebugOverlayModel.HashLength], lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain(host.Map.IslandHash, lines[1], StringComparison.Ordinal);
        Assert.Contains(hash[..DebugOverlayModel.HashLength], lines[2], StringComparison.Ordinal);
        Assert.Contains(Texts(locale)("time.season." + data.Time.SeasonId), lines[4], StringComparison.Ordinal);
        Assert.Contains($"({data.Weather!.Weather})", lines[5], StringComparison.Ordinal);
        Assert.Contains($"({host.LookEpoch.Current})", lines[6], StringComparison.Ordinal);
        Assert.EndsWith(" 60", lines[7], StringComparison.Ordinal);
        Assert.EndsWith(" " + host.Inhabitants.All.Count(), lines[8], StringComparison.Ordinal);
        Assert.Contains($"({data.Biome})", lines[9], StringComparison.Ordinal);
        Assert.Contains("PageDown", lines[10], StringComparison.Ordinal);
    }

    [Fact]
    public void TheJumpsLineNeedsDebugCommands()
    {
        var host = Island();
        var data = DebugOverlayModel.Read(host, host.World.StateHash(), host.World.Tick, 60, null, ("PageDown", "PageUp"));

        Assert.Null(data.EpochKeys);
        Assert.Equal(10, DebugOverlayModel.Lines(data, Texts("en")).Count);
    }

    [Fact]
    public void AWorldWithoutAMapShowsNoHashAndNoBiome()
    {
        var host = SimulationHost.CreatePlaceholderWorld(Content, 1, new MockDecisionPort(), "de");
        var data = DebugOverlayModel.Read(host, host.World.StateHash(), host.World.Tick, 60, (1, 1));
        var lines = DebugOverlayModel.Lines(data, Texts("de"));

        Assert.Null(data.WorldHash);
        Assert.Null(data.Biome);
        Assert.EndsWith(DebugOverlayModel.None, lines[1], StringComparison.Ordinal);
        Assert.EndsWith(DebugOverlayModel.None, lines[9], StringComparison.Ordinal);
        Assert.Empty(DebugOverlayModel.RawKeys(lines));
    }

    [Fact]
    public void ThePointerOutsideTheMapHasNoBiome()
    {
        var host = Island();
        Assert.Null(DebugOverlayModel.BiomeAt(host.Map, -5, -5));
        Assert.Contains(DebugOverlayModel.BiomeAt(host.Map, SomeCell(host).X, SomeCell(host).Z), host.Map!.Map.BiomeIds);
    }

    [Fact]
    public void AMissingTextShowsAsRawKey()
    {
        var host = Island();
        var data = DebugOverlayModel.Read(host, host.World.StateHash(), host.World.Tick, 60, null);

        Assert.Contains("ui.debug.seed", DebugOverlayModel.RawKeys(DebugOverlayModel.Lines(data, key => key)));
        Assert.Equal(["{season}"], DebugOverlayModel.RawKeys(["Season: {season}", "Seed: 7", "06:30 (rain)"]));
    }

    [Fact]
    public void EpochStepsFollowTheDataAndStopAtTheEnds()
    {
        string[] epochs = ["a", "b", "c"];

        Assert.Equal("b", DebugOverlayModel.StepEpoch(epochs, "a", 1));
        Assert.Equal("b", DebugOverlayModel.StepEpoch(epochs, "c", -1));
        Assert.Null(DebugOverlayModel.StepEpoch(epochs, "c", 1));
        Assert.Null(DebugOverlayModel.StepEpoch(epochs, "a", -1));
        Assert.Null(DebugOverlayModel.StepEpoch(epochs, "x", 1));
        Assert.Null(DebugOverlayModel.StepEpoch(epochs, null, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheCoreAcceptsAnEpochStepOnlyWithDebugCommands(bool debug)
    {
        var host = Island(debug);
        var next = DebugOverlayModel.StepEpoch([.. host.Content.LookEpochs.Epochs.Select(e => e.Id)], host.LookEpoch.Current, 1);

        Assert.NotNull(next);
        Assert.Equal(debug, host.Decisions.Submit(new PlayerCommand(LookEpochSystem.DebugSetLookEpoch, null) { TypeId = next }).Accepted);
    }
}
