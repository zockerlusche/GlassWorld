using GlassWorld.Simulation.Weather;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>
/// How far the inhabitants see right now (B2594): the one place where a sight range of the data becomes
/// the range in use. Both ranges go through it: the exploration range in cells (the preset's
/// <c>exploration</c> section) and the perception radius in metres (content/behaviour/utility.json).
/// <para>
/// Today the weather alone lowers the sight: <c>sight_permille</c> of the climate's weather states, the
/// core's current weather (island-wide; no blending during a transition). The light (day and night, moon,
/// fire; B5, D634 R33) joins here as a further factor of <see cref="Permille"/>, and every range follows.
/// </para>
/// Not state: the factor follows from the weather, which is hashed and saved.
/// </summary>
public sealed class Sight
{
    /// <summary>Full sight in permille.</summary>
    public const int Full = 1000;

    private readonly WeatherSystem? _weather;

    /// <summary>Creates the sight of a world; <paramref name="weather"/> is null while the world has no weather (full sight).</summary>
    public Sight(WeatherSystem? weather) => _weather = weather;

    /// <summary>Full sight whatever happens: worlds without weather, and systems built on their own.</summary>
    public static Sight Clear { get; } = new(null);

    /// <summary>The share of a sight range that is left right now, 1..1000.</summary>
    public int Permille => Math.Clamp(_weather?.SightPermille ?? Full, 1, Full);

    /// <summary>A range in cells as it is right now: whole cells (integer maths, rounded down), never less than one.</summary>
    public int Cells(int baseCells) => Math.Max(1, (int)((long)baseCells * Permille / Full));

    /// <summary>A range in metres as it is right now; with full sight exactly the range given.</summary>
    public double Metres(double baseMetres)
    {
        var permille = Permille;
        return permille == Full ? baseMetres : baseMetres * permille / Full;
    }
}
