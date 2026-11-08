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

    /// <summary>
    /// Wind strength 0..1 at a tick, for sound and nature motion (GW3): the base wind of the weather, blended
    /// over the transition after a change, plus slow gusts from the world seed (same seed, same wind).
    /// Derived from tick, seed and weather history, so it is not part of the state.
    /// </summary>
    double WindAt(long tick);
}
