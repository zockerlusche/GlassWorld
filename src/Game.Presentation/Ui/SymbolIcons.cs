using GlassWorld.Contracts;

namespace GlassWorld.Presentation.Ui;

/// <summary>
/// The comic icons of the feeling symbols (B2541 part 4, look D355): flat fill with a dark outline,
/// drawn from signed distance shapes into RGBA pixels on the CPU. No image files, no licence question,
/// the same pixels on every machine and headless.
/// </summary>
public static class SymbolIcons
{
    /// <summary>Outline colour (dark ink, D355).</summary>
    private static readonly (byte R, byte G, byte B) Ink = (0x2b, 0x2b, 0x3a);

    /// <summary>Renders an icon as RGBA8 pixels, row by row from the top.</summary>
    /// <param name="shape">Which shape.</param>
    /// <param name="fill">Fill colour #rrggbb.</param>
    /// <param name="size">Width and height in pixels.</param>
    public static byte[] Render(EmotionSymbolShape shape, string fill, int size)
    {
        ArgumentException.ThrowIfNullOrEmpty(fill);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 16);
        var color = (R: Hex(fill, 1), G: Hex(fill, 3), B: Hex(fill, 5));
        var (body, detail) = Shape(shape);
        var pixel = 1.0 / size;
        var outline = 0.06;
        var rgba = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // Centre of the pixel in -0.5..0.5, y up.
                var p = (X: ((x + 0.5) / size) - 0.5, Y: 0.5 - ((y + 0.5) / size));
                var d = body(p);
                var alpha = Math.Clamp(0.5 - (d / pixel), 0, 1);
                if (alpha <= 0)
                {
                    continue;
                }

                var inner = Math.Clamp(0.5 - ((d + outline) / pixel), 0, 1);
                if (detail is not null)
                {
                    inner = Math.Min(inner, Math.Clamp(0.5 + (detail(p) / pixel), 0, 1));
                }

                var i = ((y * size) + x) * 4;
                rgba[i] = Mix(Ink.R, color.R, inner);
                rgba[i + 1] = Mix(Ink.G, color.G, inner);
                rgba[i + 2] = Mix(Ink.B, color.B, inner);
                rgba[i + 3] = (byte)Math.Round(alpha * 255);
            }
        }

        return rgba;
    }

    /// <summary>Body: inside negative (filled, outlined). Detail: inside negative is drawn in ink on the fill.</summary>
    private static (Func<(double X, double Y), double> Body, Func<(double X, double Y), double>? Detail) Shape(EmotionSymbolShape shape) => shape switch
    {
        EmotionSymbolShape.Heart => (p => Union(Circle(p, -0.17, 0.12, 0.2), Circle(p, 0.17, 0.12, 0.2), Polygon(p, [(-0.36, 0.06), (0.36, 0.06), (0, -0.42)])), null),
        EmotionSymbolShape.Note => (p => Union(Ellipse(p, -0.12, -0.28, 0.17, 0.12), Box(p, 0.03, 0.06, 0.04, 0.36), Polygon(p, [(0.03, 0.42), (0.3, 0.26), (0.3, 0.14), (0.03, 0.3)])), null),
        EmotionSymbolShape.Bulb => (p => Union(Circle(p, 0, 0.1, 0.3), Box(p, 0, -0.28, 0.14, 0.14)), p => Union(Box(p, 0, -0.24, 0.12, 0.015), Box(p, 0, -0.32, 0.12, 0.015))),
        EmotionSymbolShape.Tear => (p => Union(Circle(p, 0, -0.14, 0.26), Polygon(p, [(-0.24, -0.06), (0.24, -0.06), (0, 0.44)])), null),
        EmotionSymbolShape.Star => (p => Polygon(p, StarPoints(0.44, 0.19)), null),
        EmotionSymbolShape.Storm_cloud => (p => Union(Circle(p, -0.18, 0.02, 0.17), Circle(p, 0.04, 0.12, 0.22), Circle(p, 0.22, 0.0, 0.16), Box(p, 0.02, -0.06, 0.34, 0.1)), p => Polygon(p, [(0.02, -0.04), (-0.08, -0.14), (0.0, -0.14), (-0.06, -0.26), (0.08, -0.1), (0.0, -0.1), (0.08, -0.04)])),
        EmotionSymbolShape.Plaster => (p => Rotated(p, 0.6, q => Box(q, 0, 0, 0.4, 0.13) - 0.04), p => Rotated(p, 0.6, q => Box(q, 0, 0, 0.1, 0.09)) + 0.06),
        EmotionSymbolShape.Sick_face => (p => Circle(p, 0, 0, 0.4), p => Union(Circle(p, -0.13, 0.08, 0.05), Circle(p, 0.13, 0.08, 0.05), Box(p, 0, -0.16, 0.14, 0.03))),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown symbol shape"),
    };

    private static byte Mix(byte ink, byte fill, double t) => (byte)Math.Round(ink + ((fill - ink) * t));

    private static byte Hex(string color, int at) => Convert.ToByte(color.Substring(at, 2), 16);

    private static double Union(params double[] d) => d.Min();

    private static double Circle((double X, double Y) p, double cx, double cy, double r) =>
        Math.Sqrt(((p.X - cx) * (p.X - cx)) + ((p.Y - cy) * (p.Y - cy))) - r;

    private static double Ellipse((double X, double Y) p, double cx, double cy, double rx, double ry)
    {
        // Approximation: scaled distance (good enough for small icons).
        var k = Math.Sqrt((((p.X - cx) / rx) * ((p.X - cx) / rx)) + (((p.Y - cy) / ry) * ((p.Y - cy) / ry)));
        return (k - 1) * Math.Min(rx, ry);
    }

    private static double Box((double X, double Y) p, double cx, double cy, double hx, double hy)
    {
        var dx = Math.Abs(p.X - cx) - hx;
        var dy = Math.Abs(p.Y - cy) - hy;
        return Math.Sqrt((Math.Max(dx, 0) * Math.Max(dx, 0)) + (Math.Max(dy, 0) * Math.Max(dy, 0))) + Math.Min(Math.Max(dx, dy), 0);
    }

    private static double Rotated((double X, double Y) p, double angle, Func<(double X, double Y), double> sdf)
    {
        var (c, s) = (Math.Cos(angle), Math.Sin(angle));
        return sdf(((p.X * c) + (p.Y * s), (-p.X * s) + (p.Y * c)));
    }

    /// <summary>Signed distance to a polygon (any simple polygon, inside negative).</summary>
    private static double Polygon((double X, double Y) p, (double X, double Y)[] v)
    {
        var d = double.MaxValue;
        var inside = false;
        for (int i = 0, j = v.Length - 1; i < v.Length; j = i++)
        {
            var (ex, ey) = (v[j].X - v[i].X, v[j].Y - v[i].Y);
            var (wx, wy) = (p.X - v[i].X, p.Y - v[i].Y);
            var t = Math.Clamp(((wx * ex) + (wy * ey)) / ((ex * ex) + (ey * ey)), 0, 1);
            var (bx, by) = (wx - (ex * t), wy - (ey * t));
            d = Math.Min(d, (bx * bx) + (by * by));
            if ((v[i].Y > p.Y) != (v[j].Y > p.Y) && p.X < (ex * (p.Y - v[i].Y) / ey) + v[i].X)
            {
                inside = !inside;
            }
        }

        return (inside ? -1 : 1) * Math.Sqrt(d);
    }

    private static (double X, double Y)[] StarPoints(double outer, double inner) =>
        [.. Enumerable.Range(0, 10).Select(i =>
        {
            var r = i % 2 == 0 ? outer : inner;
            var a = (Math.PI / 2) + (i * Math.PI / 5);
            return (r * Math.Cos(a), (r * Math.Sin(a)) - 0.03);
        })];
}
