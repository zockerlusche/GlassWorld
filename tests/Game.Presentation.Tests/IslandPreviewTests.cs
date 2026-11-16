using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Presentation.Tests;

public sealed class IslandPreviewTests
{
    private static readonly string Content = Path.Combine(TestPaths.Root, "content");
    private static readonly Lazy<ContentSet> Set = new(() => ContentSet.Load(Content));

    private static T Read<T>(params string[] path)
        where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine([Content, .. path])));

    private static (GeneratedIsland Island, TerrainLook Look, PreviewLook Preview) Island(ulong seed)
    {
        var island = WorldGenerator.Generate(Set.Value.WorldGen, Set.Value.Objects, "start_island", seed);
        var terrain = Read<LookTerrain>("look", "terrain.json");
        var look = TerrainLook.From(terrain, Read<LookPalette>("look", "palette.json"), island.Map, Set.Value.WorldGen.Presets["start_island"]);
        return (island, look, PreviewLook.From(terrain));
    }

    private static float Luma((byte R, byte G, byte B) c) => (0.299f * c.R) + (0.587f * c.G) + (0.114f * c.B);

    [Fact]
    public void ThePreviewIsSmallFixedAndTheSameForTheSameIsland()
    {
        // A1769 (B2805): the picture's longest side comes from the data; the same island gives the same picture.
        var (island, look, preview) = Island(42);
        var a = IslandPreview.Draw(island.Map, look, preview);
        var b = IslandPreview.Draw(island.Map, look, preview);
        Assert.True(Math.Max(a.Width, a.Height) <= preview.MaxSidePx);
        Assert.True(Math.Max(a.Width, a.Height) >= preview.MaxSidePx / 2);
        Assert.Equal(a.Width * a.Height * 3, a.Rgb.Length);
        Assert.Equal(a.Rgb, b.Rgb);
    }

    [Fact]
    public void SeaAndLandDifferInBrightnessNotOnlyInHue()
    {
        // Marcel is colour-blind: the open sea at the corner and the land at the start places must differ in brightness.
        var (island, look, preview) = Island(7);
        var image = IslandPreview.Draw(island.Map, look, preview);
        var step = (double)island.Map.WidthCells / image.Width;
        var sea = Luma(image.At(0, 0));
        Assert.Equal(WaterKind.Salt, island.Map.Cell(0, 0).Water);
        foreach (var start in island.Starts)
        {
            var land = Luma(image.At((int)(start.X / step), (int)(start.Z / step)));
            Assert.True(Math.Abs(land - sea) >= 30, $"sea {sea} vs land {land} at {start.X},{start.Z}");
        }
    }

    [Fact]
    public async Task OnlyTheLatestRunCounts()
    {
        // A slider moved again while the generator ran: the older island is stale and not shown.
        var latest = new LatestOnly<int>();
        using var gate = new ManualResetEventSlim();
        var first = latest.RunAsync(() =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return 1;
        });
        var second = latest.RunAsync(() => 2);
        gate.Set();
        Assert.False((await first).Latest);
        Assert.Equal((true, 2), await second);
    }
}
