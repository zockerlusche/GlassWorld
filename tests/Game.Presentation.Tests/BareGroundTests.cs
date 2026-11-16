using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1385 (B5-8): bare field cells as a mask for the ground, wet clothes darker by the wetness.</summary>
public sealed class BareGroundTests
{
    [Fact]
    public void TheMaskMarksBareCellsAndLeavesOutWhatLiesOutside()
    {
        var mask = BareGround.Mask(4, 3, [(1, 0), (3, 2), (9, 9), (-1, 0)]);
        Assert.Equal(12, mask.Length);
        Assert.Equal(255, mask[1]);
        Assert.Equal(255, mask[(2 * 4) + 3]);
        Assert.Equal(2, mask.Count(b => b == 255));
    }

    [Theory]
    [InlineData(null, 1000, 1.0)]
    [InlineData(0.7, null, 1.0)]
    [InlineData(0.7, 0, 1.0)]
    [InlineData(0.7, 1000, 0.7)]
    [InlineData(0.7, 500, 0.85)]
    public void WetClothesGetDarkerByTheWetness(double? shade, int? wet, double expected) =>
        Assert.Equal(expected, BareGround.WetShade(shade, wet), 9);

    [Fact]
    public void TheBareGroundColourIsAPaletteColour()
    {
        // A1385: look/nature.json names its bare-ground colour by palette id; an unknown id would fall back silently.
        static T Read<T>(string file) where T : class => GlassWorld.Contracts.Validation.ContractSerializer.Deserialize<T>(
            File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", file)));
        var bare = Read<GlassWorld.Contracts.LookNature>("nature.json").Bare;
        Assert.NotNull(bare);
        Assert.Contains(Read<GlassWorld.Contracts.LookPalette>("palette.json").Colors, c => c.Id == bare.Color);
    }

    [Fact]
    public void TheShadeMovesInSteps()
    {
        // 20 steps: 1 % more wetness does not set the shader again.
        Assert.Equal(BareGround.WetShade(0.7, 500), BareGround.WetShade(0.7, 510), 9);
        Assert.NotEqual(BareGround.WetShade(0.7, 500), BareGround.WetShade(0.7, 560), 9);
    }
}
