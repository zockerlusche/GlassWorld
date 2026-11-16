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

        Assert.Equal(11 + JobLines(data), lines.Count); // A1801: the AI's jobs follow
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

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void TheChosenInhabitantsKnowledgeShowsWhereItCameFrom(string locale)
    {
        // A1402 (B4a-S7): the origin of what an inhabitant believes, in the debug view - who told it, how sure it is.
        var host = Island();
        var who = host.Inhabitants.All.First();
        var teller = host.Inhabitants.All.Skip(1).First();
        who.Beliefs.Set("berry_bush", "edible", true, 0.7, Simulation.Objects.KnowledgeSources.Heard, teller.Id, host.World.Tick + 1);
        var texts = Texts(locale);

        var none = DebugOverlayModel.Lines(DebugOverlayModel.Read(host, "0", 0, 60, null), texts);
        var lines = DebugOverlayModel.Lines(DebugOverlayModel.Read(host, "0", 0, 60, null, chosen: who.Id), texts);
        Assert.Empty(DebugOverlayModel.RawKeys(lines));
        Assert.Equal(none.Count + 1 + Math.Min(who.Beliefs.Count, DebugOverlayModel.MaxBeliefs), lines.Count);
        var name = host.Snapshot().Inhabitants.Single(i => i.Id == teller.Id).Name!;
        var newest = lines[none.Count + 1];
        Assert.Contains("(berry_bush)", newest, StringComparison.Ordinal);
        Assert.Contains(name, newest, StringComparison.Ordinal);
        Assert.EndsWith("70 %", newest, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJumpsLineNeedsDebugCommands()
    {
        var host = Island();
        var data = DebugOverlayModel.Read(host, host.World.StateHash(), host.World.Tick, 60, null, ("PageDown", "PageUp"));

        Assert.Null(data.EpochKeys);
        Assert.Equal(10 + JobLines(data), DebugOverlayModel.Lines(data, Texts("en")).Count);
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

    /// <summary>A1801: the lines of the AI's jobs - a count line and one per open job, at most the view's number.</summary>
    private static int JobLines(DebugOverlayData data) => data.Jobs is { } jobs ? 1 + Math.Min(jobs.Count, DebugOverlayModel.MaxJobs) : 0;
}
