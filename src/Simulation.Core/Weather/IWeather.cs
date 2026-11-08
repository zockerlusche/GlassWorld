using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Weather;

/// <summary>
/// Read access to the world's weather (B2 R13) for other systems and the presentation, e.g. GW3's A1357.
/// Weather ids come from the vocabulary kind <c>weather</c>.
/// </summary>
/// <remarks>
/// For now the weather is the same on the whole island: <c>at</c> is ignored (regional weather comes later
/// and keeps this signature). <see cref="WeatherAt"/> answers for the current tick, for any tick of the
/// current phase and for ticks of the recent phases kept in a small history; a tick before that history
/// or after the current tick throws <see cref="ArgumentOutOfRangeException"/>, so a caller never gets a
/// guessed value.
/// </remarks>
public interface IWeather
{
    /// <summary>The current main weather id.</summary>
    string Current { get; }

    /// <summary>The main weather id at a place and tick (see remarks for the allowed ticks).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tick lies in the future or before the kept history.</exception>
    string WeatherAt(Position at, long tick);
}
