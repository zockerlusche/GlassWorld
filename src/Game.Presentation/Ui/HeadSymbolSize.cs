namespace GlassWorld.Presentation.Ui;

/// <summary>
/// Size of the symbols above inhabitants' heads on screen (F5284): a symbol keeps its world size while that
/// is between the floor and the ceiling in pixels; further away it stays at the floor (still readable),
/// close up at the ceiling (not huge). The result is a scale factor for the symbol's node. Godot-free.
/// </summary>
public static class HeadSymbolSize
{
    /// <summary>Screen pixels per metre at <paramref name="distanceM"/> for a perspective camera.</summary>
    /// <param name="distanceM">Distance camera to symbol.</param>
    /// <param name="fovDeg">Vertical field of view.</param>
    /// <param name="viewportPx">Viewport height in pixels.</param>
    public static double PixelsPerMetre(double distanceM, double fovDeg, double viewportPx) =>
        distanceM <= 0 || fovDeg <= 0 ? double.PositiveInfinity : viewportPx / (2 * distanceM * Math.Tan(fovDeg * Math.PI / 360));

    /// <summary>
    /// The scale factor that brings a symbol of world height <paramref name="sizeM"/> to a screen height
    /// between <paramref name="minPx"/> and <paramref name="maxPx"/>; 1 when it already is.
    /// </summary>
    public static double Factor(double sizeM, double pixelsPerMetre, double minPx, double maxPx)
    {
        if (sizeM <= 0 || double.IsNaN(pixelsPerMetre) || pixelsPerMetre <= 0)
        {
            return 1;
        }

        if (double.IsPositiveInfinity(pixelsPerMetre))
        {
            return 0; // the camera sits in the symbol: hide it rather than fill the screen
        }

        var natural = sizeM * pixelsPerMetre;
        return Math.Clamp(natural, minPx, Math.Max(minPx, maxPx)) / natural;
    }
}
