using System.Globalization;

namespace GlassWorld.Presentation.Ui;

/// <summary>
/// A1789 (B2826, Marcel W2240): every window remembers where the player put it - per kind of window (its title key), kept in
/// the settings as 'x,y' in logical pixels. A remembered place that no longer fits (smaller window, bigger UI) is pulled into
/// the safe area instead of being lost.
/// </summary>
public static class WindowPlacement
{
    /// <summary>At most this many windows are remembered (a hand-edited file cannot grow without end).</summary>
    public const int MaxRemembered = 64;

    /// <summary>The place to show a window of <paramref name="size"/> in <paramref name="area"/>: the remembered one, pulled inside the safe margin.</summary>
    public static (float X, float Y) Restore((float X, float Y) remembered, (float W, float H) size, (float W, float H) area, float margin)
    {
        var maxX = Math.Max(margin, area.W - margin - size.W);
        var maxY = Math.Max(margin, area.H - margin - size.H);
        return (Math.Clamp(remembered.X, margin, maxX), Math.Clamp(remembered.Y, margin, maxY));
    }

    /// <summary>'x,y' of a place (whole logical pixels, invariant culture).</summary>
    public static string Format((float X, float Y) place) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(place.X)},{Math.Round(place.Y)}");

    /// <summary>A place from 'x,y', or null when the text is no place.</summary>
    public static (float X, float Y)? Parse(string? text)
    {
        var parts = (text ?? string.Empty).Split(',');
        return parts.Length == 2
            && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && float.IsFinite(x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && float.IsFinite(y)
            ? (x, y)
            : null;
    }

    /// <summary>Only readable places, at most <see cref="MaxRemembered"/> (a hand-edited file).</summary>
    public static Dictionary<string, string> Clean(IReadOnlyDictionary<string, string>? places) =>
        (places ?? new Dictionary<string, string>())
            .Where(p => !string.IsNullOrWhiteSpace(p.Key) && Parse(p.Value) is not null)
            .Take(MaxRemembered)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
}
