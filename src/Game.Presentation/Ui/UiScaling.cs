using GlassWorld.Contracts;

namespace GlassWorld.Presentation.Ui;

/// <summary>
/// Scale arithmetic of the UI standard (D366 section 2, B2513). Godot's stretch mode
/// <c>canvas_items</c>/<c>expand</c> already scales the logical base size to the window; the
/// player's UI size is applied on top as <c>Window.ContentScaleFactor</c>. Text size is a separate
/// factor on the font sizes only.
/// </summary>
public static class UiScaling
{
    /// <summary>Clamps a player setting into the allowed range and snaps it to the step grid.</summary>
    public static double Snap(double value, UiScaleRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        var clamped = Math.Clamp(value, range.Min, range.Max);
        var steps = Math.Round((clamped - range.Min) / range.Step, MidpointRounding.AwayFromZero);
        return Math.Clamp(Math.Round(range.Min + (steps * range.Step), 4), range.Min, range.Max);
    }

    /// <summary>Factor Godot applies for the window size alone (stretch aspect expand).</summary>
    public static double StretchFactor(int windowWidth, int windowHeight, UiTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowHeight);
        return Math.Min((double)windowWidth / tokens.BaseWidth, (double)windowHeight / tokens.BaseHeight);
    }

    /// <summary>
    /// Logical canvas size the layout has to fit into for a window and UI size; for example 200 % on
    /// 1920x1080 leaves 960x540.
    /// </summary>
    public static (double Width, double Height) LogicalSize(int windowWidth, int windowHeight, UiTokens tokens, double uiScale)
    {
        var factor = StretchFactor(windowWidth, windowHeight, tokens) * uiScale;
        return (windowWidth / factor, windowHeight / factor);
    }

    /// <summary>Font size in logical pixels for a step and the player's text size.</summary>
    public static int FontPx(UiTokens tokens, UiFontSizeId step, double textScale)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var size = tokens.FontSizes.Single(f => f.Id == step).Px;
        return (int)Math.Round(size * textScale, MidpointRounding.AwayFromZero);
    }

    /// <summary>Physical pixels of a logical length on screen (for checks against XAG 101).</summary>
    public static double PhysicalPx(double logicalPx, int windowWidth, int windowHeight, UiTokens tokens, double uiScale) =>
        logicalPx * StretchFactor(windowWidth, windowHeight, tokens) * uiScale;
}
