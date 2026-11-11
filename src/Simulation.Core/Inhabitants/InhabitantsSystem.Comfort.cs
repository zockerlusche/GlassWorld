using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Random;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>The air at a place right now, as far as the comfort zone asks (B2582).</summary>
/// <param name="TemperatureTenthC">Air temperature in tenths of a degree Celsius.</param>
/// <param name="Precipitation">Whether precipitation falls (rain, snow, storm).</param>
public readonly record struct Air(int TemperatureTenthC, bool Precipitation);

/// <summary>
/// The comfort zone (B2582, W1745; data: <c>comfort_zone</c> in the inhabitant model): the air at the inhabitant's
/// place moves a need. Only a need effect follows, no behaviour; the code names no need and no action.
/// </summary>
public sealed partial class InhabitantsSystem
{
    // Derived from world seed and id, so neither saved nor hashed.
    private readonly Dictionary<string, int> _comfortShift = new(StringComparer.Ordinal);

    /// <summary>
    /// The air at a place right now (set by the host on worlds with a climate: season, weather, time of day
    /// and the height of the cell). Null, or a null answer, means the world has no air temperature there:
    /// the comfort zone then moves nothing.
    /// </summary>
    public Func<World, Position, Air?>? AirAt { get; init; }

    /// <summary>
    /// How far the inhabitant's comfort zone is shifted against the zone of the data, in tenths of a degree
    /// (B2582: every inhabitant's zone lies a little differently): a fixed value between minus and plus the
    /// data's <c>individual_shift_tenth_c</c>, drawn from the world seed and the inhabitant's id with an own
    /// stream (not kept among the world's streams). 0 without comfort data.
    /// </summary>
    public int ComfortShiftOf(ulong seed, string inhabitantId)
    {
        ArgumentNullException.ThrowIfNull(inhabitantId);
        if (_model.ComfortZone?.IndividualShiftTenthC is not { } spread || spread <= 0)
        {
            return 0;
        }

        if (!_comfortShift.TryGetValue(inhabitantId, out var shift))
        {
            shift = RandomStreams.Create(seed, $"inhabitant/{inhabitantId}/comfort").NextInt(-spread, spread + 1);
            _comfortShift[inhabitantId] = shift;
        }

        return shift;
    }

    /// <summary>
    /// One tick of air on the comfort need: inside the zone it recovers, below or above it sinks by the
    /// data's loss per hour and degree outside (times the factors of a running action and of precipitation),
    /// never below the data's floor by the air alone. Temperatures are whole tenths of a degree.
    /// </summary>
    private void FeelTheAir(World world, Inhabitant inhabitant)
    {
        if (_model.ComfortZone is not { } comfort
            || !inhabitant.Needs.TryGetValue(comfort.Need, out var value)
            || AirAt?.Invoke(world, inhabitant.Position) is not { } air)
        {
            return;
        }

        var shift = ComfortShiftOf(world.Seed, inhabitant.Id);
        var below = comfort.MinTenthC + shift - air.TemperatureTenthC;
        var above = air.TemperatureTenthC - (comfort.MaxTenthC + shift);
        if (below <= 0 && above <= 0)
        {
            inhabitant.Needs[comfort.Need] = Math.Min(1.0, value + PerTick(comfort.RecoverPerHour));
            return;
        }

        var running = inhabitant.Activity is { RemainingTicks: not null } activity
            ? comfort.ActionFactors?.FirstOrDefault(f => string.Equals(f.Action, activity.Action, StringComparison.Ordinal))
            : null;
        var perHour = below > 0
            ? comfort.ColdLossPerHourPerDegree * (below / 10.0) * (running?.ColdFactor ?? 1.0) * (air.Precipitation ? comfort.PrecipitationColdFactor ?? 1.0 : 1.0)
            : comfort.HeatLossPerHourPerDegree * (above / 10.0) * (running?.HeatFactor ?? 1.0);
        var floor = comfort.Floor ?? 0.0;
        if (value > floor)
        {
            inhabitant.Needs[comfort.Need] = Math.Max(floor, value - PerTick(perHour));
        }
    }
}
