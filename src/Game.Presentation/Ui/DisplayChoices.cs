using System.Globalization;

namespace GlassWorld.Presentation.Ui;

/// <summary>
/// A1748 (D957 v4 section 10): the choices of the settings tab 'Grafik' and the rule of 'Behalten?' - a change that rebuilds
/// the window (mode or size) must be confirmed within the data's seconds, else it falls back, as in most PC games.
/// </summary>
public static class DisplayChoices
{
    /// <summary>Window in a frame.</summary>
    public const string Windowed = "windowed";

    /// <summary>The whole screen, the game alone (exclusive).</summary>
    public const string Fullscreen = "fullscreen";

    /// <summary>The whole screen as a window without a frame.</summary>
    public const string Borderless = "borderless";

    /// <summary>The window modes in the order the settings show them.</summary>
    public static IReadOnlyList<string> WindowModes { get; } = [Windowed, Fullscreen, Borderless];

    /// <summary>Text key of a window mode.</summary>
    public static string ModeKey(string mode) => "ui.settings.window_mode." + mode;

    /// <summary>Text key of a quality level.</summary>
    public static string QualityKey(string level) => "ui.settings.quality." + level;

    /// <summary>Width and height of a 'WIDTHxHEIGHT' size, or null.</summary>
    public static (int Width, int Height)? Size(string? resolution)
    {
        var parts = (resolution ?? string.Empty).Split('x');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var w)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var h) && w > 0 && h > 0
            ? (w, h)
            : null;
    }

    /// <summary>The data's window sizes that fit a screen of this size, in the data's order.</summary>
    public static IReadOnlyList<string> Fitting(IEnumerable<string> resolutions, int screenWidth, int screenHeight) =>
        [.. resolutions.Where(r => Size(r) is { } s && s.Width <= screenWidth && s.Height <= screenHeight)];

    /// <summary>True when the change rebuilds the window (mode or size) and so needs 'Behalten?'.</summary>
    public static bool NeedsKeep(PlayerSettings before, PlayerSettings after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return before.WindowMode != after.WindowMode || before.Resolution != after.Resolution;
    }

    /// <summary>The settings with the window part of <paramref name="before"/> again - what falling back means.</summary>
    public static PlayerSettings FallBack(PlayerSettings now, PlayerSettings before)
    {
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(before);
        return now with { WindowMode = before.WindowMode, Resolution = before.Resolution };
    }
}

/// <summary>A1748: the countdown of 'Behalten?' - whole seconds left for the text, and whether time is up.</summary>
public sealed class KeepCountdown(double seconds)
{
    private double _left = Math.Max(0, seconds);

    /// <summary>Whole seconds left, rounded up (what the question shows).</summary>
    public int SecondsLeft => (int)Math.Ceiling(_left);

    /// <summary>True once time is up.</summary>
    public bool Over => _left <= 0;

    /// <summary>Lets <paramref name="delta"/> seconds pass; true exactly when time runs out with this step.</summary>
    public bool Tick(double delta)
    {
        if (Over)
        {
            return false;
        }

        _left -= Math.Max(0, delta);
        return Over;
    }
}
