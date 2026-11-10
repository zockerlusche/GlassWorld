using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Weather;

namespace GlassWorld.Simulation.Player;

/// <summary>
/// The player's intervention in the weather (B2-5 R13, B6): the command <see cref="SetWeather"/> sets the
/// main weather to a weather of the climate and costs favour (intervention <c>weather</c> of
/// content/player/favor.json). Like every command it is checked at submit, logged at the tick it takes
/// effect and applied again by a replay at the same tick. The weather goes on by its table afterwards, so
/// the next change step may already replace the chosen weather. The code names no weather.
/// </summary>
public sealed class WeatherIntervention
{
    /// <summary>Command kind: set the main weather (TypeId = weather id of the climate).</summary>
    public const string SetWeather = "set_weather";

    /// <summary>Event: the player set the weather (Detail = the weather, no inhabitant).</summary>
    public const string WeatherSet = "weather_set";

    /// <summary>Reason: this world has no weather (placeholder world).</summary>
    public const string ReasonNoWeather = "msg.command.no_weather";

    /// <summary>Reason: the climate of this world has no such weather.</summary>
    public const string ReasonUnknownWeather = "msg.command.unknown_weather";

    /// <summary>Reason: the weather is already the chosen one (nothing to pay for).</summary>
    public const string ReasonUnchanged = "msg.command.weather_unchanged";

    private const string Intervention = "weather";

    private readonly WeatherSystem? _weather;
    private readonly PlayerSystem _player;
    private readonly Action<WorldEvent> _report;

    /// <summary>Creates the handler; <paramref name="weather"/> is null in worlds without weather (every command is refused).</summary>
    public WeatherIntervention(WeatherSystem? weather, PlayerSystem player, Action<WorldEvent> report)
    {
        _weather = weather;
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>Checks a weather command without changing anything (the UI shows the reason).</summary>
    public CommandResult Check(PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_weather is null)
        {
            return new CommandResult(false, ReasonNoWeather);
        }

        if (command.TypeId is not { } weather || !_weather.Has(weather))
        {
            return new CommandResult(false, ReasonUnknownWeather);
        }

        if (string.Equals(weather, _weather.Current, StringComparison.Ordinal))
        {
            return new CommandResult(false, ReasonUnchanged);
        }

        return _player.CanAfford(Intervention) ? new CommandResult(true, null) : new CommandResult(false, PlayerSystem.ReasonNoFavor);
    }

    /// <summary>Applies a checked weather command (called by the decisions system at the tick it takes effect).</summary>
    public void Apply(World world, PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(command);
        if (!Check(command).Accepted)
        {
            // Conditions can change between submitting and the tick (favour spent, the weather changed by itself).
            return;
        }

        _player.Spend(Intervention);
        _report(new WorldEvent(WeatherSet, world.Tick, string.Empty, null, null) { Detail = command.TypeId });
        _weather!.Force(command.TypeId!, world.Tick);
    }
}
