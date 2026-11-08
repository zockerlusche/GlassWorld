using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Weather;

/// <summary>
/// The world's weather as the core knows it (agreed with GW1 #19313; GW1's weather core implements it,
/// <c>Host.Weather</c>, null without weather). Weather ids are the weather vocabulary.
/// </summary>
public interface IWeather
{
    /// <summary>Weather id now.</summary>
    string Current { get; }

    /// <summary>Weather id at a place and tick (island-wide for now: the position is ignored).</summary>
    string WeatherAt(Position at, long tick);
}
