using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1823 (B2736): a fire is drawn as big and bright as it burns; embers lie low with a red glow.</summary>
public sealed class FireLookTests
{
    private static readonly ObjectGlow Glow = ContractSerializer.Deserialize<LookObjects>(
        File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "objects.json"))).Visuals.First(v => v.Glow?.MinScale is not null).Glow!;

    [Fact]
    public void AFireGrowsWithItsStrengthAndEmbersLieLow()
    {
        var weak = FireLook.Shown(100, false, Glow);
        var full = FireLook.Shown(1000, false, Glow);
        Assert.Equal(1.0, full.Scale, 9);
        Assert.True(weak.Scale < full.Scale && weak.Scale >= Glow.MinScale!.Value);
        Assert.True(weak.Light < full.Light);

        var embers = FireLook.Shown(150, true, Glow);
        Assert.True(embers.Embers);
        Assert.Equal(Glow.EmbersScale!.Value, embers.Scale, 9);
        Assert.Equal(Glow.EmbersEnergy!.Value, embers.Light, 9);
    }

    [Fact]
    public void WithoutStrengthDataAGlowStaysFull()
    {
        var plain = new ObjectGlow { LightColor = "flame", LightEnergy = 1, LightRangeM = 2, LightHeightM = 0.3 };
        Assert.Equal((1.0, 1.0, false), FireLook.Shown(100, false, plain));
    }

    [Fact]
    public void TheEmbersColourIsAPaletteColour()
    {
        var palette = ContractSerializer.Deserialize<LookPalette>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "palette.json")));
        Assert.Contains(palette.Colors, c => c.Id == Glow.EmbersLightColor);
    }
}
