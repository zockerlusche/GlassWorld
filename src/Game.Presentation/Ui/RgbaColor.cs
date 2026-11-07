using System.Globalization;

namespace GlassWorld.Presentation.Ui;

/// <summary>A colour token value (#RRGGBB or #RRGGBBAA) with the contrast arithmetic of WCAG 2.2.</summary>
/// <param name="R">Red 0..1.</param>
/// <param name="G">Green 0..1.</param>
/// <param name="B">Blue 0..1.</param>
/// <param name="A">Alpha 0..1.</param>
public readonly record struct RgbaColor(double R, double G, double B, double A)
{
    /// <summary>Parses #RRGGBB or #RRGGBBAA.</summary>
    public static RgbaColor Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length is not (7 or 9) || hex[0] != '#')
        {
            throw new FormatException($"colour '{hex}' is not #RRGGBB or #RRGGBBAA");
        }

        double Channel(int index) => int.Parse(hex.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        return new RgbaColor(Channel(1), Channel(3), Channel(5), hex.Length == 9 ? Channel(7) : 1.0);
    }

    /// <summary>This colour painted over an opaque background.</summary>
    public RgbaColor Over(RgbaColor background) =>
        new(Mix(R, background.R), Mix(G, background.G), Mix(B, background.B), 1.0);

    /// <summary>Relative luminance (WCAG 2.2 definition).</summary>
    public double Luminance => (0.2126 * Linear(R)) + (0.7152 * Linear(G)) + (0.0722 * Linear(B));

    /// <summary>Contrast ratio between two opaque colours, 1..21.</summary>
    public static double Contrast(RgbaColor a, RgbaColor b)
    {
        var (light, dark) = a.Luminance >= b.Luminance ? (a.Luminance, b.Luminance) : (b.Luminance, a.Luminance);
        return (light + 0.05) / (dark + 0.05);
    }

    private double Mix(double front, double back) => (front * A) + (back * (1 - A));

    private static double Linear(double channel) =>
        channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
}
