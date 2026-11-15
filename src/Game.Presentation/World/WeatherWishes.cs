using GlassWorld.Simulation;
using GlassWorld.Simulation.Player;

namespace GlassWorld.Presentation.World;

/// <summary>One weather the player may wish (A1340): whether it is the weather now, and whether the favour suffices.</summary>
/// <param name="Weather">Weather id of the climate.</param>
/// <param name="Current">It is the weather now (nothing to wish).</param>
/// <param name="Reason">Why it cannot be wished now (a text key), or null.</param>
public sealed record WeatherWish(string Weather, bool Current, string? Reason)
{
    /// <summary>Whether it can be wished now.</summary>
    public bool Possible => Reason is null;
}

/// <summary>
/// The player's weather wish (A1340, B6-8, Bogen 31 "Wetter beeinflussen"; core: WeatherIntervention, B2-5 R13): the
/// weathers of the climate in the data's order, each with the reason it cannot be wished now - the core checks again when
/// the wish is sent. Read only, without Godot.
/// </summary>
public static class WeatherWishes
{
    /// <summary>The favour intervention of a weather wish (content/player/favor.json).</summary>
    public const string Intervention = "weather";

    /// <summary>The weathers to wish, or none in a world without weather.</summary>
    public static IReadOnlyList<WeatherWish> For(SimulationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.WeatherSystem is not { } weather)
        {
            return [];
        }

        var affordable = host.Player.CanAfford(Intervention);
        return [.. weather.Weathers.Select(id =>
        {
            var current = string.Equals(id, weather.Current, StringComparison.Ordinal);
            var reason = current ? WeatherIntervention.ReasonUnchanged : affordable ? null : PlayerSystem.ReasonNoFavor;
            return new WeatherWish(id, current, reason);
        })];
    }

    /// <summary>The command that wishes this weather.</summary>
    public static Simulation.Decisions.PlayerCommand Command(string weather) => new(WeatherIntervention.SetWeather, null) { TypeId = weather };
}
