namespace GlassWorld.Simulation.Behaviour;

/// <summary>
/// A response curve from data: points (x, y) with ascending x, linear interpolation in between and
/// constant outside. Replaces transcendental math in the core (ADR-0002).
/// </summary>
public sealed class Curve
{
    private readonly (double X, double Y)[] _points;

    /// <summary>Creates a curve; points must have ascending, distinct x.</summary>
    public Curve(IEnumerable<IEnumerable<double>> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        _points = [.. points.Select(p => p.ToArray()).Select(p => p.Length == 2 ? (p[0], p[1]) : throw new InvalidDataException("A curve point needs exactly x and y."))];
        if (_points.Length < 2)
        {
            throw new InvalidDataException("A curve needs at least two points.");
        }

        for (var i = 1; i < _points.Length; i++)
        {
            if (_points[i].X <= _points[i - 1].X)
            {
                throw new InvalidDataException("Curve points must have ascending x.");
            }
        }
    }

    /// <summary>Value of the curve at <paramref name="x"/>.</summary>
    public double At(double x)
    {
        if (x <= _points[0].X)
        {
            return _points[0].Y;
        }

        for (var i = 1; i < _points.Length; i++)
        {
            if (x <= _points[i].X)
            {
                var (x0, y0) = _points[i - 1];
                var (x1, y1) = _points[i];
                return y0 + ((y1 - y0) * (x - x0) / (x1 - x0));
            }
        }

        return _points[^1].Y;
    }
}
