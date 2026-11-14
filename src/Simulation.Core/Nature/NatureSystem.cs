using GlassWorld.Contracts;
using GlassWorld.Simulation.State;
using GlassWorld.Simulation.Weather;

namespace GlassWorld.Simulation.Nature;

/// <summary>The nature takt's sums as saved (A1380 N1): per growth group and per influence, one value per takt boundary.</summary>
/// <param name="BaseTakt">Index of the takt the sums start at (the world's first).</param>
/// <param name="Growth">Growth group → its sum at each takt boundary from <paramref name="BaseTakt"/> on.</param>
/// <param name="Current">Growth group → its factor (permille) in the takt that runs now.</param>
/// <param name="Wet">Wet takts so far, at each boundary.</param>
/// <param name="Frost">Frost takts so far.</param>
/// <param name="Heat">Hot takts so far.</param>
/// <param name="WetFrost">Frost takts with wetness shortly before (frost on wet things).</param>
/// <param name="LastWetTakt">The last takt with precipitation, or -1.</param>
/// <param name="Flags">Influences of the takt that runs now: bit 0 wet, 1 frost, 2 heat, 3 frost on wet.</param>
public sealed record NatureState(
    long BaseTakt,
    IReadOnlyDictionary<string, List<long>> Growth,
    IReadOnlyDictionary<string, int> Current,
    List<long> Wet,
    List<long> Frost,
    List<long> Heat,
    List<long> WetFrost,
    long LastWetTakt,
    int Flags);

/// <summary>What a place went through since a tick (R51, for the durability of things): counts of takts.</summary>
/// <param name="Wet">Takts with precipitation.</param>
/// <param name="Frost">Takts below 0 °C.</param>
/// <param name="Heat">Hot takts (data threshold).</param>
/// <param name="WetFrost">Frost takts shortly after wetness.</param>
/// <param name="DryTakts">Takts since the last wet one (whole history), or null when it has never rained.</param>
public readonly record struct NatureInfluencesSince(long Wet, long Frost, long Heat, long WetFrost, long? DryTakts);

/// <summary>
/// B5 R13/R11/R51 (A1380 N1, plan D989, review GW1 #23580): the nature takt. At each takt boundary it reads
/// the season and the weather once and keeps integer prefix sums: growth per growth group (factor in permille
/// x ticks; the drought is not known in advance, so the sums are kept, not predicted) and, for durability,
/// wet / frost / hot / frost-on-wet takts. Whether a used-up place has grown back is then a difference of two
/// sums - the same at whatever tick it is asked. A group with 1000 everywhere keeps no sums: it grows exactly by
/// the clock, so worlds without nature data keep their hashes.
/// </summary>
public sealed class NatureSystem : IWorldSystem, IOptionalWorldState
{
    private const int WetBit = 1;
    private const int FrostBit = 2;
    private const int HeatBit = 4;
    private const int WetFrostBit = 8;
    private const string WetKey = "~wet";
    private const string FrostKey = "~frost";
    private const string HeatKey = "~heat";
    private const string WetFrostKey = "~wet_frost";
    private const ulong Offset = 14695981039346656037UL;

    private readonly NatureSettings _data;
    private readonly long _taktTicks;
    private readonly Dictionary<string, string> _groupOfType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NatureGroup> _groups = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, List<long>> _growth = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, int> _current = new(StringComparer.Ordinal);
    private readonly List<long> _wet = [];
    private readonly List<long> _frost = [];
    private readonly List<long> _heat = [];
    private readonly List<long> _wetFrost = [];
    private readonly Dictionary<string, ulong> _digests = new(StringComparer.Ordinal);
    private long _baseTakt = -1;
    private long _lastWetTakt = -1;
    private int _flags;
    private long _lastTick = -1;

    /// <summary>Creates the takt; <paramref name="weather"/> null = no weather (no drought, no influences).</summary>
    public NatureSystem(NatureSettings data, int ticksPerHour, WeatherSystem? weather)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _taktTicks = Math.Max(1L, (long)data.TaktMinutes * ticksPerHour / 60);
        Weather = weather;
        foreach (var group in data.Groups)
        {
            _groups[group.Id] = group;
        }

        foreach (var type in data.Types)
        {
            _groupOfType[type.Type] = type.Group;
        }
    }

    /// <summary>The island's weather, or null.</summary>
    public WeatherSystem? Weather { get; }

    /// <inheritdoc/>
    public string Name => "nature";

    /// <inheritdoc/>
    public bool HasState => _growth.Count > 0 || (_data.Influences is not null && _wet.Count > 0);

    /// <summary>Ticks per takt.</summary>
    public long TaktTicks => _taktTicks;

    /// <summary>
    /// GW3 K4 (#25247): a further growth factor of a group in permille, read when a takt opens (group id, takt
    /// number), or null = none. The provider answers from its state at the takt's start (before its own step in
    /// that tick), so a run, a replay and a loaded game agree. Multiplies the season and drought factor.
    /// </summary>
    public Func<string, long, int?>? FactorOf { get; set; }

    /// <summary>
    /// Whether a used-up place of <paramref name="typeId"/>, gone since <paramref name="goneSince"/>, has grown
    /// back at <paramref name="now"/>: the growth of its group since then reaches <paramref name="regrowTicks"/> x 1000.
    /// A type without a group grows by the clock.
    /// </summary>
    public bool Grown(string typeId, long goneSince, long now, long regrowTicks)
    {
        if (!_groupOfType.TryGetValue(typeId, out var group) || !_growth.ContainsKey(group))
        {
            return now - goneSince >= regrowTicks;
        }

        return GrowthAt(group, now) - GrowthAt(group, goneSince) >= regrowTicks * 1000;
    }

    /// <summary>R51: what the island went through from <paramref name="since"/> to <paramref name="now"/>, in whole takts.</summary>
    public NatureInfluencesSince InfluencesSince(long since, long now)
    {
        if (_wet.Count == 0)
        {
            return new NatureInfluencesSince(0, 0, 0, 0, null);
        }

        long Count(List<long> series) => SumAt(series, now) - SumAt(series, since);
        var takt = now / _taktTicks;
        return new NatureInfluencesSince(Count(_wet), Count(_frost), Count(_heat), Count(_wetFrost), _lastWetTakt < 0 ? null : takt - _lastWetTakt);
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);

        // Once per tick: a boundary is never closed twice (not state - a world steps each tick once).
        if (world.Tick == _lastTick || (world.Tick % _taktTicks != 0 && _baseTakt >= 0))
        {
            return;
        }

        _lastTick = world.Tick;

        var takt = world.Tick / _taktTicks;
        if (_baseTakt < 0)
        {
            _baseTakt = takt;
        }
        else
        {
            // Close the takt that ends now: each sum grows by what held in it.
            foreach (var (group, series) in _growth)
            {
                Append(group, series, series[^1] + (_current[group] * _taktTicks));
            }

            // Only series that were opened (influences need weather; a world without a climate keeps none).
            if (_data.Influences is not null && _wet.Count > 0)
            {
                Close(WetKey, _wet, WetBit);
                Close(FrostKey, _frost, FrostBit);
                Close(HeatKey, _heat, HeatBit);
                Close(WetFrostKey, _wetFrost, WetFrostBit);
            }
        }

        Open(world, takt);
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_baseTakt).Write(_growth.Count);
        foreach (var (group, series) in _growth)
        {
            // The whole series counts (review GW1 #375): a running digest of every value, not only the last.
            hasher.Write(group).Write(_current[group]).Write(series.Count).Write((long)_digests[group]);
        }

        hasher.Write(_wet.Count);
        if (_wet.Count > 0)
        {
            hasher.Write((long)_digests[WetKey]).Write((long)_digests[FrostKey]).Write((long)_digests[HeatKey]).Write((long)_digests[WetFrostKey]).Write(_lastWetTakt).Write(_flags);
        }
    }

    /// <summary>The sums for a save game.</summary>
    public NatureState Capture() => new(
        _baseTakt,
        _growth.ToDictionary(g => g.Key, g => g.Value.ToList(), StringComparer.Ordinal),
        _current.ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal),
        [.. _wet],
        [.. _frost],
        [.. _heat],
        [.. _wetFrost],
        _lastWetTakt,
        _flags);

    /// <summary>Restores the sums of a save game.</summary>
    public void Restore(NatureState? state)
    {
        if (state is null)
        {
            return;
        }

        _baseTakt = state.BaseTakt;
        _growth.Clear();
        foreach (var (group, series) in state.Growth)
        {
            _growth[group] = [.. series];
        }

        _current.Clear();
        foreach (var (group, factor) in state.Current)
        {
            _current[group] = factor;
        }

        Replace(_wet, state.Wet);
        Replace(_frost, state.Frost);
        Replace(_heat, state.Heat);
        Replace(_wetFrost, state.WetFrost);
        _lastWetTakt = state.LastWetTakt;
        _flags = state.Flags;
        _digests.Clear();
        foreach (var (group, series) in _growth)
        {
            _digests[group] = DigestOf(series);
        }

        if (_wet.Count > 0)
        {
            _digests[WetKey] = DigestOf(_wet);
            _digests[FrostKey] = DigestOf(_frost);
            _digests[HeatKey] = DigestOf(_heat);
            _digests[WetFrostKey] = DigestOf(_wetFrost);
        }

        static void Replace(List<long> into, List<long> from)
        {
            into.Clear();
            into.AddRange(from);
        }
    }

    /// <summary>Starts a takt: the factors and influences that hold until the next boundary.</summary>
    private void Open(World world, long takt)
    {
        var season = world.Now.SeasonId;
        var drought = Weather?.InDrought == true;
        foreach (var group in _groups.Values)
        {
            var factor = group.Seasons.FirstOrDefault(s => s.Season == season)?.Permille ?? 1000;
            if (drought && group.DroughtPermille is { } inDrought)
            {
                factor = factor * inDrought / 1000;
            }

            // GW3 K4 (#25247): a factor from outside, e.g. how full an animal stock is - read at the takt's start.
            if (FactorOf?.Invoke(group.Id, takt) is { } outside)
            {
                factor = factor * Math.Max(0, outside) / 1000;
            }

            // A group that has only ever grown by the clock keeps no sums (golden hashes of worlds without nature).
            if (!_growth.ContainsKey(group.Id))
            {
                if (factor == 1000)
                {
                    continue;
                }

                _growth[group.Id] = [];
                Append(group.Id, _growth[group.Id], (takt - _baseTakt) * _taktTicks * 1000);
            }

            _current[group.Id] = factor;
        }

        if (_data.Influences is not { } influences || Weather is null)
        {
            return;
        }

        if (_wet.Count == 0)
        {
            Append(WetKey, _wet, 0);
            Append(FrostKey, _frost, 0);
            Append(HeatKey, _heat, 0);
            Append(WetFrostKey, _wetFrost, 0);
        }

        var reading = Weather.Read(world);
        _flags = 0;
        if (reading.PrecipitationPermille > 0)
        {
            _flags |= WetBit;
            _lastWetTakt = takt;
        }

        if (reading.TemperatureTenthC < 0)
        {
            _flags |= FrostBit;
            if (_lastWetTakt >= 0 && takt - _lastWetTakt <= influences.WetFrostTakts)
            {
                _flags |= WetFrostBit;
            }
        }

        if (reading.TemperatureTenthC >= influences.HeatMinTenthC)
        {
            _flags |= HeatBit;
        }
    }

    private void Close(string key, List<long> series, int bit) => Append(key, series, series[^1] + ((_flags & bit) != 0 ? 1 : 0));

    /// <summary>Adds a value to a series and to its running digest (FNV-1a over the values).</summary>
    private void Append(string key, List<long> series, long value)
    {
        series.Add(value);
        _digests[key] = Fold(_digests.GetValueOrDefault(key, Offset), value);
    }

    private static ulong Fold(ulong digest, long value) => (digest ^ unchecked((ulong)value)) * 1099511628211UL;

    /// <summary>The digest of a whole series, as the appends build it (for a restored save).</summary>
    private static ulong DigestOf(List<long> series) => series.Aggregate(Offset, (d, v) => Fold(d, v));

    /// <summary>
    /// The growth sum of a group at a tick: the sum at the boundary before plus the factor of that takt times the
    /// ticks into it. Before the group kept sums it grew by the clock (1000 per tick).
    /// </summary>
    private long GrowthAt(string group, long tick)
    {
        var series = _growth[group];

        // The first value stands for the boundary where the group began to keep sums; up to there it grew by the clock.
        var start = _baseTakt + (series[0] / (_taktTicks * 1000));
        var k = (tick / _taktTicks) - start;
        if (k < 0)
        {
            return (tick * 1000) - (start * _taktTicks * 1000) + series[0];
        }

        if (k >= series.Count - 1)
        {
            return series[^1] + (_current[group] * (tick - ((start + series.Count - 1) * _taktTicks)));
        }

        var factor = (series[(int)k + 1] - series[(int)k]) / _taktTicks;
        return series[(int)k] + (factor * (tick - ((start + k) * _taktTicks)));
    }

    /// <summary>A count series at a tick (whole takts before it).</summary>
    private long SumAt(List<long> series, long tick)
    {
        var k = (tick / _taktTicks) - _baseTakt;
        return series[(int)Math.Clamp(k, 0, series.Count - 1)];
    }
}
