using GlassWorld.Contracts;
using GlassWorld.Simulation.Behaviour;
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
    /// B5 R47: whether an inhabitant feels a sensation of the vocabulary now (set by the host from the world's
    /// systems, e.g. a big fire close by); null = no sensations, sensation mood drivers never act.
    /// </summary>
    public Func<Inhabitant, string, bool>? Feels { get; set; }

    /// <summary>B2773: whether a thing near keeps the weather off an inhabitant (the body's shelter lookup); null = never.</summary>
    public Func<Inhabitant, bool>? Sheltered { get; set; }

    /// <summary>A1744: whether a place lies under a shelter (the body's lookup); null = none.</summary>
    public Func<Position, bool>? ShelteredAt { get; set; }

    /// <summary>A1744 (B2790 b): the body data's weight for leaving a shelter in rain or cold; null = no difference.</summary>
    public GlassWorld.Contracts.StaySheltered? StaySheltered { get; set; }

    /// <summary>
    /// A1744 (B2790 b): weighs the options that lead out of a shelter while the inhabitant is under one and it rains
    /// there or it feels one of the data's levels - a thing or someone not under a shelter, or a walk away (destination
    /// not here). What stays inside keeps its weight; nothing is forbidden.
    /// </summary>
    internal void WeighLeavingShelter(World world, Inhabitant inhabitant, List<Candidate> options)
    {
        if (StaySheltered is not { } stay || ShelteredAt is null || Sheltered?.Invoke(inhabitant) != true || options.Count == 0)
        {
            return;
        }

        var rains = stay.InPrecipitation == true && AirAt?.Invoke(world, inhabitant.Position) is { Precipitation: true };
        var feels = !rains && stay.AtLevels is { Count: > 0 } levels && ComfortLevelOf(world, inhabitant) is { } level && levels.Contains(level);
        if (!rains && !feels)
        {
            return;
        }

        var factor = stay.LeavingFactorPermille / 1000.0;
        for (var i = 0; i < options.Count; i++)
        {
            if (LeavesShelter(options[i]))
            {
                options[i] = options[i] with { Score = options[i].Score * factor };
            }
        }
    }

    private bool LeavesShelter(in Candidate option)
    {
        if (option.TargetId is { } id)
        {
            if (_objects.TryGet(id, out var thing))
            {
                return _objects.HeldBy(id) is null && ShelteredAt!(thing.Position) is false;
            }

            return _inhabitants.TryGetValue(id, out var other) && ShelteredAt!(other.Position) is false;
        }

        // Without a target: an action that walks somewhere (wander, explore) leads out; one done here does not.
        return _actions.HasTiming(option.Action) && (_actions.Timing(option.Action).RandomNearby || _actions.Timing(option.Action).Frontier);
    }

    /// <summary>
    /// The warmth of heat sources around a place in tenths of a degree (a fire, B2687 K3 / spec R7), added to
    /// the air the inhabitant feels; null = none. Set by the host.
    /// </summary>
    public Func<Position, int>? HeatAt { get; set; }

    /// <summary>
    /// B5-11 (R29/R30): what the body adds to the felt air in tenths of a degree - the sun where no shade falls,
    /// minus the cooling of being wet; null = nothing. Set by the host.
    /// </summary>
    public Func<World, Inhabitant, int>? BodyTenthC { get; set; }

    /// <summary>The comfort levels from coldest to hottest (vocabulary comfort_level, A1379); null = none in the request.</summary>
    public IReadOnlyList<string>? ComfortLevels { get; set; }

    /// <summary>
    /// How warm it feels to the inhabitant right now (A1379), for the request: inside its zone the middle
    /// level, within the data's band below/above the next ones, beyond the outer ones - from the same felt
    /// temperature its warmth need uses (air plus the heat around it). Null without a zone band, air or levels.
    /// </summary>
    public string? ComfortLevelOf(World world, Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(inhabitant);
        if (_model.ComfortZone is not { BandTenthC: { } band } comfort || ComfortLevels is not { Count: 5 } levels
            || AirAt?.Invoke(world, inhabitant.Position) is not { } air)
        {
            return null;
        }

        var shift = ComfortShiftOf(world.Seed, inhabitant.Id);
        var felt = FeltOnce(world, inhabitant, air);
        var below = comfort.MinTenthC + shift - felt;
        var above = felt - (comfort.MaxTenthC + shift);
        return below > 0 ? levels[below <= band ? 1 : 0]
            : above > 0 ? levels[above <= band ? 3 : 4]
            : levels[2];
    }

    // B5 R5/R6: the comfort level of the inhabitant being updated in this tick (derived, set before its needs fall).
    private (string Id, string? Level) _levelNow;

    // Review GW3 #435: while one inhabitant's state is updated (its comfort level, then its warmth need, nothing in
    // between changes the world), its felt temperature is computed once; any other question computes it afresh.
    private (string Id, int Felt)? _feltOnce;
    private bool _keepFelt;

    /// <summary>The felt temperature (<see cref="Felt"/>), kept for the inhabitant being updated while <see cref="_keepFelt"/>.</summary>
    private int FeltOnce(World world, Inhabitant inhabitant, Air air)
    {
        if (_keepFelt && _feltOnce is { } known && string.Equals(known.Id, inhabitant.Id, StringComparison.Ordinal))
        {
            return known.Felt;
        }

        var felt = Felt(world, inhabitant, air);
        _feltOnce = _keepFelt ? (inhabitant.Id, felt) : null;
        return felt;
    }

    /// <summary>B5 R5/R6: the factor on a need's decay at the inhabitant's comfort level now (data need_modifiers).</summary>
    private double NeedFactor(Inhabitant inhabitant, string need)
    {
        if (_model.ComfortZone?.NeedModifiers is not { Count: > 0 } modifiers || !string.Equals(_levelNow.Id, inhabitant.Id, StringComparison.Ordinal)
            || _levelNow.Level is not { } level)
        {
            return 1.0;
        }

        var factor = 1.0;
        foreach (var modifier in modifiers)
        {
            if (string.Equals(modifier.Level, level, StringComparison.Ordinal) && string.Equals(modifier.Need, need, StringComparison.Ordinal))
            {
                factor *= modifier.Factor;
            }
        }

        return factor;
    }

    /// <summary>B5 R6: the factor on the walking pace at the inhabitant's comfort level in this tick (data pace_factors).</summary>
    private double PaceFactor(Inhabitant inhabitant)
    {
        if (_model.ComfortZone?.PaceFactors is not { Count: > 0 } factors || !string.Equals(_levelNow.Id, inhabitant.Id, StringComparison.Ordinal)
            || _levelNow.Level is not { } level)
        {
            return 1.0;
        }

        return factors.FirstOrDefault(f => string.Equals(f.Level, level, StringComparison.Ordinal))?.Factor ?? 1.0;
    }

    /// <summary>B5 R5: the loss per hour from a table over whole degrees outside the zone (linear between points).</summary>
    internal static double FromTable(ICollection<ComfortLossPoint> table, double degrees)
    {
        var points = table.OrderBy(p => p.Degrees).ToList();
        if (degrees <= points[0].Degrees)
        {
            return points[0].PerHour * (points[0].Degrees == 0 ? 1 : degrees / points[0].Degrees);
        }

        for (var i = 1; i < points.Count; i++)
        {
            if (degrees <= points[i].Degrees)
            {
                var a = points[i - 1];
                var b = points[i];
                return a.PerHour + ((b.PerHour - a.PerHour) * (degrees - a.Degrees) / (b.Degrees - a.Degrees));
            }
        }

        return points[^1].PerHour;
    }

    // A1729/A1730 (review GW4 #467): the snapshot's comfort level, worked out once per inhabitant and world tick -
    // the look asks every frame, the full felt air (sun and shade, wind shelter, fire, others) is not cheap. Not state.
    private readonly Dictionary<string, (long Tick, string? Level)> _comfortForView = new(StringComparer.Ordinal);

    /// <summary>
    /// The comfort level for the look (A1730): <see cref="ComfortLevelOf"/>, kept for the rest of the tick. Asked between
    /// ticks (snapshots), so it never sees a half-done tick (the felt cache of #435 is the core's, inside its loop).
    /// </summary>
    public string? ComfortLevelForView(World world, Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(inhabitant);
        if (_comfortForView.TryGetValue(inhabitant.Id, out var known) && known.Tick == world.Tick)
        {
            return known.Level;
        }

        var level = ComfortLevelOf(world, inhabitant);
        _comfortForView[inhabitant.Id] = (world.Tick, level);
        return level;
    }

    /// <summary>
    /// B5 R31: whether an inhabitant feels a comfort sensation of the data now (too cold at the cold level, ...):
    /// its comfort level is one of the sensation's levels. Unknown sensations: false.
    /// </summary>
    public bool FeelsComfort(World world, Inhabitant inhabitant, string sensation)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(inhabitant);
        if (_model.ComfortZone?.Felt?.FirstOrDefault(f => string.Equals(f.Sensation, sensation, StringComparison.Ordinal)) is not { } felt)
        {
            return false;
        }

        return ComfortLevelOf(world, inhabitant) is { } level && felt.Levels.Contains(level, StringComparer.Ordinal);
    }

    /// <summary>
    /// The felt temperature of an inhabitant: the air plus the heat of things around it, plus the sun and minus
    /// the cooling of being wet (B5-11), in tenths of a degree.
    /// </summary>
    private int Felt(World world, Inhabitant inhabitant, Air air) =>
        air.TemperatureTenthC + (HeatAt?.Invoke(inhabitant.Position) ?? 0) + (BodyTenthC?.Invoke(world, inhabitant) ?? 0);

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
        var felt = FeltOnce(world, inhabitant, air);
        var below = comfort.MinTenthC + shift - felt;
        var above = felt - (comfort.MaxTenthC + shift);
        if (below <= 0 && above <= 0)
        {
            inhabitant.Needs[comfort.Need] = Math.Min(1.0, value + PerTick(comfort.RecoverPerHour));
            return;
        }

        var running = inhabitant.Activity is { RemainingTicks: not null } activity
            ? comfort.ActionFactors?.FirstOrDefault(f => string.Equals(f.Action, activity.Action, StringComparison.Ordinal))
            : null;
        // B5 R5: from the data's table over the degrees outside, where given; else per degree as before.
        var coldBase = comfort.ColdLossTable is { Count: > 0 } coldTable ? FromTable(coldTable, below / 10.0) : comfort.ColdLossPerHourPerDegree * (below / 10.0);
        var heatBase = comfort.HeatLossTable is { Count: > 0 } heatTable ? FromTable(heatTable, above / 10.0) : comfort.HeatLossPerHourPerDegree * (above / 10.0);
        var perHour = below > 0
            ? coldBase * (running?.ColdFactor ?? 1.0) * (air.Precipitation ? comfort.PrecipitationColdFactor ?? 1.0 : 1.0)
            : heatBase * (running?.HeatFactor ?? 1.0);
        var floor = comfort.Floor ?? 0.0;
        if (value > floor)
        {
            inhabitant.Needs[comfort.Need] = Math.Max(floor, value - PerTick(perHour));
        }
    }
}
