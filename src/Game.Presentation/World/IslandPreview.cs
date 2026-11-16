using GlassWorld.Contracts;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Presentation.World;

/// <summary>A small picture of an island: <paramref name="Rgb"/> holds three bytes per pixel, row by row from the north-west.</summary>
/// <param name="Width">Pixels across (west to east).</param>
/// <param name="Height">Pixels down (north to south).</param>
/// <param name="Rgb">Red, green and blue per pixel (0..255).</param>
public sealed record PreviewImage(int Width, int Height, byte[] Rgb)
{
    /// <summary>The colour of one pixel.</summary>
    public (byte R, byte G, byte B) At(int x, int y)
    {
        var i = ((y * Width) + x) * 3;
        return (Rgb[i], Rgb[i + 1], Rgb[i + 2]);
    }
}

/// <summary>How the preview draws: size and the brightness of height and relief (content/look/terrain.json "preview").</summary>
/// <param name="MaxSidePx">Longest side of the picture in pixels; a larger island is sampled down.</param>
/// <param name="ReliefGain">Brightness change per height unit towards the north-west neighbour (light from the north-west).</param>
/// <param name="HeightGain">Extra brightness from the lowest to the highest land (0 = flat colours).</param>
/// <param name="WaterShade">Brightness of all water against its game colour - the palette's sea is about as bright as its
/// grass, so the preview darkens water to keep land and water apart without hue.</param>
public sealed record PreviewLook(int MaxSidePx, float ReliefGain, float HeightGain, float WaterShade = 0.5f)
{
    /// <summary>The preview values of the terrain data, or plain defaults where the data names none.</summary>
    public static PreviewLook From(LookTerrain terrain)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        var data = terrain.Preview;
        return new PreviewLook(data?.MaxSidePx ?? 192, (float)(data?.ReliefGain ?? 0.05), (float)(data?.HeightGain ?? 0.3), (float)(data?.WaterShade ?? 0.5));
    }
}

/// <summary>
/// A1769 (B2805): the new-world window's preview - the generated island as a small 2D picture of height, water and biome,
/// in the colours the game draws the ground with. Pure: no Godot, the same island gives the same picture. Water and land
/// differ in brightness as well as hue, and height shows as light and shade, so the picture reads without telling colours
/// apart (Marcel is colour-blind).
/// </summary>
public static class IslandPreview
{
    /// <summary>Draws <paramref name="map"/> with the ground and water colours of <paramref name="look"/>.</summary>
    public static PreviewImage Draw(WorldMap map, TerrainLook look, PreviewLook preview)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(look);
        ArgumentNullException.ThrowIfNull(preview);
        var step = Math.Max(1, (int)Math.Ceiling(Math.Max(map.WidthCells, map.DepthCells) / (double)Math.Max(1, preview.MaxSidePx)));
        var width = (map.WidthCells + step - 1) / step;
        var height = (map.DepthCells + step - 1) / step;
        var (low, high) = LandRange(map);
        var bytes = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var colour = Pixel(map, look, preview, Math.Min(map.WidthCells - 1, (x * step) + (step / 2)), Math.Min(map.DepthCells - 1, (y * step) + (step / 2)), step, low, high);
                var i = ((y * width) + x) * 3;
                bytes[i] = ToByte(colour.R);
                bytes[i + 1] = ToByte(colour.G);
                bytes[i + 2] = ToByte(colour.B);
            }
        }

        return new PreviewImage(width, height, bytes);
    }

    private static Rgb Pixel(WorldMap map, TerrainLook look, PreviewLook preview, int cx, int cz, int step, int low, int high)
    {
        var cell = map.Cell(cx, cz);
        switch (cell.Water)
        {
            case WaterKind.Salt:
                return (cell.WaterDepth >= look.DeepFromUnits ? look.SeaDeep : look.SeaShallow).Scaled(preview.WaterShade);
            case WaterKind.Fresh:
                return (cell.WaterDepth <= look.SwampMaxUnits ? look.Swamp : look.Fresh).Scaled(preview.WaterShade);
        }

        var ground = cell.Biome < look.BiomeColors.Count ? look.BiomeColors[cell.Biome] : look.Bed;

        // Light from the north-west: ground rising towards the light is brighter, falling away darker; higher land a bit brighter.
        var nx = Math.Max(0, cx - step);
        var nz = Math.Max(0, cz - step);
        var relief = 1f + ((cell.Height - map.Cell(nx, nz).Height) * preview.ReliefGain);
        var lift = high > low ? preview.HeightGain * ((cell.Height - low) / (float)(high - low)) : 0f;
        // Land is only ever lightened by height and darkened little by relief, so it stays clearly brighter than the
        // darkened water (no hue needed to tell them apart).
        return ground.Scaled(Math.Clamp(relief, 0.85f, 1.25f) * (1f + lift));
    }

    private static (int Low, int High) LandRange(WorldMap map)
    {
        var low = int.MaxValue;
        var high = int.MinValue;
        for (var z = 0; z < map.DepthCells; z++)
        {
            for (var x = 0; x < map.WidthCells; x++)
            {
                var cell = map.Cell(x, z);
                if (cell.Water == WaterKind.None)
                {
                    low = Math.Min(low, cell.Height);
                    high = Math.Max(high, cell.Height);
                }
            }
        }

        return low > high ? (0, 0) : (low, high);
    }

    private static byte ToByte(float channel) => (byte)Math.Clamp((int)Math.Round(channel * 255f), 0, 255);
}

/// <summary>
/// A1769 (B2805): runs work (the generator for the preview) off the main thread and tells whether a result is still the
/// latest asked for - a slider moved again in the meantime makes an older island stale, it is then dropped, not shown.
/// </summary>
/// <typeparam name="T">The result of the work.</typeparam>
public sealed class LatestOnly<T>
{
    private int _ticket;

    /// <summary>Runs <paramref name="work"/> on the thread pool; <c>Latest</c> is false when another run was asked for since.</summary>
    public async Task<(bool Latest, T Value)> RunAsync(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var mine = Interlocked.Increment(ref _ticket);
        var value = await Task.Run(work).ConfigureAwait(false);
        return (mine == Volatile.Read(ref _ticket), value);
    }
}
