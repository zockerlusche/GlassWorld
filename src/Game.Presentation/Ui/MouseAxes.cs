namespace GlassWorld.Presentation.Ui;

/// <summary>
/// A1749 (B2825, Marcel W2239): which mouse axes the camera turns round - turning (across) and tilting (up/down) with the
/// right button, and moving the view (left drag or Shift with the right button), each on its own. All false = as before.
/// </summary>
/// <param name="TurnX">Turning goes the other way.</param>
/// <param name="TurnY">Tilting goes the other way.</param>
/// <param name="PanX">Moving goes the other way across.</param>
/// <param name="PanY">Moving goes the other way up and down.</param>
public sealed record MouseAxes(bool TurnX = false, bool TurnY = false, bool PanX = false, bool PanY = false)
{
    /// <summary>A turn/tilt drag in pixels with the chosen directions.</summary>
    public (double X, double Y) Turn(double dx, double dy) => (TurnX ? -dx : dx, TurnY ? -dy : dy);

    /// <summary>
    /// The point a move drag goes to (screen units, e.g. NDC): from <paramref name="from"/> by the mouse's step, each axis
    /// mirrored when it is turned round - the ground then follows the mouse the other way.
    /// </summary>
    public (double X, double Y) PanTo((double X, double Y) from, (double X, double Y) to) =>
        (PanX ? from.X - (to.X - from.X) : to.X, PanY ? from.Y - (to.Y - from.Y) : to.Y);
}
