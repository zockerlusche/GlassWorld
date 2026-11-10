using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// The shore cut of a cell beside still water (A1455): the shore line runs across the cell where the
/// ground reaches the water surface, instead of along the cell's edges.
/// </summary>
public sealed class ShoreCutTests
{
    // Corner order of the mesher: (0,0), (1,0), (1,1), (0,1).
    private static readonly (float U, float V)[] CornerAt = [(0, 0), (1, 0), (1, 1), (0, 1)];

    [Fact]
    public void ASlopeCrossedByTheSurfaceIsCutOnTheLineWhereGroundMeetsWater()
    {
        // Ground rises from 0 (near edge) to 4 (far edge); water at 2 reaches half way up the cell.
        int[] ground = [0, 0, 4, 4];
        var cut = TerrainMesher.CutShore(ground, 2);

        Assert.Equal(0.5f, cut.Wet.Sum(Area), 4);
        Assert.Equal(0.5f, cut.Dry.Sum(Area), 4);

        // The shore line ends on the cell's left and right edge, exactly where the ground is at the surface.
        var points = cut.Wet.SelectMany(p => p).ToList();
        Assert.Contains((0f, 0.5f), points);
        Assert.Contains((1f, 0.5f), points);

        // Wet and dry share the shore line: every cut point of the wet side is a point of the dry side.
        var dryPoints = cut.Dry.SelectMany(p => p).ToHashSet();
        Assert.All(points.Where(p => p.V == 0.5f), p => Assert.Contains(p, dryPoints));

        // Water only where the ground lies below the surface, dry ground only where it does not.
        Assert.All(points, p => Assert.True(GroundAt(ground, p) <= 2f + 1e-4f));
        Assert.All(dryPoints, p => Assert.True(GroundAt(ground, p) >= 2f - 1e-4f));
    }

    [Theory]
    [InlineData(0, 0, 4, 4, 2)]
    [InlineData(0, 3, 5, 1, 2)]
    [InlineData(1, 5, 2, 6, 3)]
    [InlineData(4, 0, 0, 0, 1)]
    [InlineData(0, 4, 4, 4, 3)]
    [InlineData(2, 2, 2, 1, 2)]
    [InlineData(7, 1, 6, 0, 4)]
    public void WetAndDryPartsTogetherCoverTheCell(int h0, int h1, int h2, int h3, int surface)
    {
        var cut = TerrainMesher.CutShore([h0, h1, h2, h3], surface);

        Assert.Equal(1f, cut.Wet.Sum(Area) + cut.Dry.Sum(Area), 4);
        Assert.All(cut.Wet.Concat(cut.Dry), part => Assert.True(part.Length >= 3 && Area(part) > 0f));
        Assert.All(cut.Wet.Concat(cut.Dry).SelectMany(p => p), p => Assert.InRange(p.U, 0f, 1f));
        Assert.All(cut.Wet.Concat(cut.Dry).SelectMany(p => p), p => Assert.InRange(p.V, 0f, 1f));
    }

    [Fact]
    public void TheShoreLineRunsOnAcrossNeighbouringCells()
    {
        // Two cells side by side share the corners (1,0)/(1,1) of the left one, heights 3 and 5.
        int[] left = [0, 3, 5, 1];
        int[] right = [3, 6, 7, 5];
        const int Surface = 4;

        var leftPoints = TerrainMesher.CutShore(left, Surface).Wet.SelectMany(p => p).ToList();
        var rightPoints = TerrainMesher.CutShore(right, Surface).Wet.SelectMany(p => p).ToList();

        // On the shared edge the ground reaches the surface half way (3 -> 5): the same point for both.
        Assert.Contains((1f, 0.5f), leftPoints);
        Assert.Contains((0f, 0.5f), rightPoints);
    }

    [Fact]
    public void ACellWhollyAboveOrBelowTheSurfaceIsNotCut()
    {
        var above = TerrainMesher.CutShore([5, 6, 7, 6], 4);
        Assert.Empty(above.Wet);
        Assert.Equal(1f, above.Dry.Sum(Area), 4);

        var below = TerrainMesher.CutShore([1, 2, 3, 2], 4);
        Assert.Empty(below.Dry);
        Assert.Equal(1f, below.Wet.Sum(Area), 4);
    }

    [Fact]
    public void AShoreIsACellTheSurfaceCrossesOrTouchesFromBelow()
    {
        Assert.True(TerrainMesher.IsShore([0, 0, 4, 4], 2));
        Assert.True(TerrainMesher.IsShore([2, 2, 2, 1], 2)); // dips below the surface in one corner
        Assert.False(TerrainMesher.IsShore([2, 2, 2, 2], 2)); // level with the water: nothing below it
        Assert.False(TerrainMesher.IsShore([3, 4, 5, 3], 2)); // wholly above
        Assert.False(TerrainMesher.IsShore([0, 1, 1, 0], 2)); // wholly below: would look flooded
    }

    /// <summary>Area of a polygon in cell coordinates (shoelace formula, orientation ignored).</summary>
    private static float Area((float U, float V)[] polygon)
    {
        var sum = 0f;
        for (var i = 0; i < polygon.Length; i++)
        {
            var (u0, v0) = polygon[i];
            var (u1, v1) = polygon[(i + 1) % polygon.Length];
            sum += (u0 * v1) - (u1 * v0);
        }

        return MathF.Abs(sum) / 2f;
    }

    /// <summary>Ground height at a point of the cell, bilinear over the corner heights (exact on the cell's edges).</summary>
    private static float GroundAt(int[] ground, (float U, float V) p)
    {
        var total = 0f;
        for (var i = 0; i < 4; i++)
        {
            var (cu, cv) = CornerAt[i];
            total += ground[i] * (cu == 1f ? p.U : 1f - p.U) * (cv == 1f ? p.V : 1f - p.V);
        }

        return total;
    }
}
