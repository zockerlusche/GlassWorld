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
    public void AShoreIsACellTheSurfaceCrosses()
    {
        Assert.True(TerrainMesher.IsShore([0, 0, 4, 4], 2));
        Assert.True(TerrainMesher.IsShore([1, 2, 3, 2], 2)); // a corner exactly at the surface lies on the line
        Assert.False(TerrainMesher.IsShore([2, 2, 2, 1], 2)); // nowhere above the surface: would look flooded
        Assert.False(TerrainMesher.IsShore([2, 2, 2, 2], 2)); // level with the water: nothing below it
        Assert.False(TerrainMesher.IsShore([3, 4, 5, 3], 2)); // wholly above
        Assert.False(TerrainMesher.IsShore([0, 1, 1, 0], 2)); // wholly below: would look flooded

        // A1455: with a flood margin, a cell wholly at or below the surface by at most that much carries the water
        // (a shallow margin instead of a notch); a deeper hollow stays bare; level with the water stays bare.
        Assert.True(TerrainMesher.IsShore([2, 2, 2, 1], 2, floodUnits: 3));
        Assert.True(TerrainMesher.IsShore([0, 1, 1, 0], 2, floodUnits: 3));
        Assert.False(TerrainMesher.IsShore([0, 1, 1, 0], 5, floodUnits: 3));
        Assert.False(TerrainMesher.IsShore([2, 2, 2, 2], 2, floodUnits: 3));
        Assert.True(TerrainMesher.IsShore([0, 0, 4, 4], 2, floodUnits: 3));
        Assert.False(TerrainMesher.IsShore([3, 4, 5, 3], 2, floodUnits: 3));
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

    [Theory]
    [InlineData(7UL)]
    [InlineData(1UL)]
    [InlineData(42UL)]
    public void OnRealIslandsAlmostEveryLowDryCellBesideALakeCarriesTheWater(ulong seed)
    {
        // A1455: the generator leaves dry cells beside lakes whose ground lies a little below the surface (their
        // corners share the lower lake bed). Left bare, each pulls the lake plate down to its ground: the notches
        // of D740. With the shipped flood margin nearly all of them carry the water.
        var content = GlassWorld.Simulation.Content.ContentSet.Load(Path.Combine(TestPaths.Root, "content"));
        var map = GlassWorld.Simulation.Map.WorldGenerator.Generate(content.WorldGen, content.Objects, "start_island", seed).Map;
        var look = TerrainLook.From(Read<GlassWorld.Contracts.LookTerrain>("look", "terrain.json"), Read<GlassWorld.Contracts.LookPalette>("look", "palette.json"), map, content.WorldGen.Presets["start_island"]);
        Assert.True(look.BankFloodUnits > 0);
        int low = 0, carried = 0;
        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                if (map.Cell(x, z).Water != GlassWorld.Simulation.Map.WaterKind.None || StillSurfaceBeside(map, look, x, z) is not { } surface)
                {
                    continue;
                }

                var h = TerrainMesher.CornerHeights(map, x, z, look);
                if (h.Max() <= surface && h.Min() < surface)
                {
                    low++;
                    carried += TerrainMesher.IsShore(h, surface, look.BankFloodUnits) ? 1 : 0;
                }
            }
        }

        Assert.True(low > 0, "the islands still have low dry cells beside lakes (else this test checks nothing)");
        Assert.True(carried * 100 >= low * 95, $"seed {seed}: {carried} of {low} low cells carry the water");
    }

    private static T Read<T>(params string[] path)
        where T : class => GlassWorld.Contracts.Validation.ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine([TestPaths.Root, "content", .. path])));

    /// <summary>The highest still, non-swamp fresh water surface among the eight neighbours, as the mesher's banks see it.</summary>
    private static int? StillSurfaceBeside(GlassWorld.Simulation.Map.WorldMap map, TerrainLook look, int x, int z)
    {
        int? best = null;
        foreach (var (dx, dz) in GlassWorld.Simulation.Map.FlowDirections.All)
        {
            if (!map.Contains(x + dx, z + dz))
            {
                continue;
            }

            var n = map.Cell(x + dx, z + dz);
            if (n.Water == GlassWorld.Simulation.Map.WaterKind.Fresh && n.Flow < 0 && !look.IsSwampGround(n))
            {
                best = Math.Max(best ?? int.MinValue, n.Height + n.WaterDepth);
            }
        }

        return best;
    }
}
