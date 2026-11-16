namespace GlassWorld.Presentation.World;

/// <summary>
/// A1385 (B5-8, A1380 N2): what the island shows of overuse and wetness - the mask of bare field cells for the ground
/// shader, and how dark wet clothes look. Presentation only; the core keeps the truth (WorldMapSystem, BodySystem).
/// </summary>
public static class BareGround
{
    /// <summary>
    /// The mask of bare field cells, one byte per cell (255 = bare, 0 = not), rows by <c>z</c>: <paramref name="width"/>
    /// × <paramref name="depth"/> cells; cells outside are left out.
    /// </summary>
    public static byte[] Mask(int width, int depth, IEnumerable<(int X, int Z)> bareCells)
    {
        ArgumentNullException.ThrowIfNull(bareCells);
        var mask = new byte[Math.Max(0, width) * Math.Max(0, depth)];
        foreach (var (x, z) in bareCells)
        {
            if (x >= 0 && z >= 0 && x < width && z < depth)
            {
                mask[(z * width) + x] = 255;
            }
        }

        return mask;
    }

    /// <summary>
    /// The shade of wet clothes and hair (1 = dry): from 1 to <paramref name="wetShade"/> by the wetness (0..1000), in
    /// <paramref name="steps"/> steps so the shader is set only when it shows a change. 1 without the data or a wetness.
    /// </summary>
    public static double WetShade(double? wetShade, int? wetPermille, int steps = 20)
    {
        if (wetShade is not { } darkest || wetPermille is not { } wet || wet <= 0)
        {
            return 1.0;
        }

        var share = Math.Round(Math.Clamp(wet, 0, 1000) / 1000.0 * steps) / steps;
        return 1.0 - ((1.0 - darkest) * share);
    }
}
