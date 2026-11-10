using GlassWorld.Contracts;

namespace GlassWorld.Simulation.Map;

/// <summary>The tide at one tick, for the snapshot and the presentation (B2-5 R15). Integers only.</summary>
/// <param name="LevelTenths">Water level against the mean sea level in tenths of a height unit (negative = low water).</param>
/// <param name="LevelPermille">Position on the curve: −1000 (lowest) to 1000 (highest).</param>
/// <param name="Amplitude">Half the tidal range in height units (data); 0 = no tides.</param>
/// <param name="PeriodTicks">Length of one tidal cycle in ticks (data).</param>
/// <param name="Rising">True while the water rises from this tick to the next.</param>
public sealed record TideReading(int LevelTenths, int LevelPermille, int Amplitude, int PeriodTicks, bool Rising);

/// <summary>
/// The tides of an island (B2-5 R15): the water level follows from the world tick alone, by the curve of
/// the preset (<c>tides</c>: amplitude, period_ticks, curve) with linear interpolation between its
/// points. Nothing is drawn and nothing is kept, so there is nothing to save or hash; the same tick gives
/// the same level everywhere. Whole-number arithmetic only (no sine, ADR-0002; divisions cut off towards
/// zero). Amplitude 0 turns the tides off.
/// </summary>
public sealed class Tides
{
    private const long Thousand = 1000;

    // Curve points as (phase permille, level permille), phases rising strictly from 0 to 1000 (checked at load).
    private readonly (long Phase, long Level)[] _curve;

    /// <summary>Creates the tides of a preset.</summary>
    public Tides(WorldPresetTides data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegative(data.Amplitude);
        ArgumentOutOfRangeException.ThrowIfLessThan(data.PeriodTicks, 1);
        _curve = [.. data.Curve.Select(p => ((long)p.PhasePermille, (long)p.LevelPermille))];
        if (_curve.Length < 2 || _curve[0].Phase != 0 || _curve[^1].Phase != Thousand || _curve.Zip(_curve.Skip(1)).Any(pair => pair.Second.Phase <= pair.First.Phase))
        {
            throw new InvalidDataException("Tide curve phases must rise strictly from 0 to 1000.");
        }

        Amplitude = data.Amplitude;
        PeriodTicks = data.PeriodTicks;
    }

    /// <summary>Half the tidal range in height units; 0 = off.</summary>
    public int Amplitude { get; }

    /// <summary>Length of one cycle in ticks.</summary>
    public int PeriodTicks { get; }

    /// <summary>Whether this island has tides at all (amplitude above 0).</summary>
    public bool Active => Amplitude > 0;

    /// <summary>Position on the curve at a tick: −1000 (lowest) to 1000 (highest); 0 without tides.</summary>
    public int LevelPermilleAt(long tick) => (int)(MicroAt(tick) / Thousand);

    /// <summary>Water level against the mean sea level in tenths of a height unit at a tick; 0 without tides.</summary>
    public int LevelTenthsAt(long tick) => (int)(Amplitude * 10L * MicroAt(tick) / (Thousand * Thousand));

    /// <summary>
    /// Water level in whole height units at a tick, rounded to the nearest unit (a half counts away from
    /// zero, so rise and fall are treated alike and less than half a unit is mean water): what the walk
    /// costs of the coast use.
    /// </summary>
    public int LevelUnitsAt(long tick)
    {
        var tenths = LevelTenthsAt(tick);
        return (tenths + (tenths < 0 ? -5 : 5)) / 10;
    }

    /// <summary>The tide at a tick.</summary>
    public TideReading Read(long tick) =>
        new(LevelTenthsAt(tick), LevelPermilleAt(tick), Amplitude, PeriodTicks, Active && MicroAt(tick + 1) > MicroAt(tick));

    /// <summary>Curve level in millionths of the amplitude (permille × 1000) at a tick.</summary>
    private long MicroAt(long tick)
    {
        if (!Active)
        {
            return 0;
        }

        // Position in the cycle in (permille × period) units, so no division is needed to find the segment.
        var position = Math.Max(0, tick) % PeriodTicks * Thousand;
        for (var i = 1; i < _curve.Length; i++)
        {
            var end = _curve[i].Phase * PeriodTicks;
            if (position > end)
            {
                continue;
            }

            var start = _curve[i - 1].Phase * PeriodTicks;
            var (from, to) = (_curve[i - 1].Level, _curve[i].Level);
            return (from * Thousand) + ((to - from) * Thousand * (position - start) / (end - start));
        }

        return _curve[^1].Level * Thousand;
    }
}

/// <summary>
/// The run-time layer of the tides over the immutable <see cref="WorldMap"/> (B2-5 R15): the walk cost of
/// the shallow sea cells at a tick. The path finder and the walking speed read it instead of the base cost.
/// <para>
/// The rule, chosen to be safe: <b>the tide never closes and never opens a cell</b>; it only changes what a
/// step through the shallow sea costs. So the walkable areas stay those of the map, nobody can be cut off
/// or trapped by rising water, and a route found earlier stays walkable. A sea cell the map lets one wade
/// through (salt water, not blocked) costs
/// </para>
/// <list type="bullet">
/// <item>its base cost while the water over it (base depth + tide level in whole units) is between
/// nothing and the wading depth <c>water.shallow_max_depth</c> – always so at mean water;</item>
/// <item>the base cost without the shallow-water part once the falling tide leaves it dry;</item>
/// <item>the base cost plus <c>pathfinding.cost_flooded_water</c> once the rising tide lifts the water
/// above the wading depth: one still gets through (and out), but slowly, and routes go around.</item>
/// </list>
/// Fresh water, land and deep sea are not touched. Without tides (amplitude 0) the layer is not used at all.
/// </summary>
public sealed class TideWalkLayer
{
    private readonly int[] _base;
    private readonly int[] _tidal;
    private readonly int[] _depth;
    private readonly int _shallowMaxDepth;
    private readonly int _shallowCost;
    private readonly int _floodedCost;
    private int[]? _costs;
    private int _costsLevel;

    /// <summary>Creates the layer for a map.</summary>
    /// <param name="map">The base grid.</param>
    /// <param name="tides">The tides of the preset.</param>
    /// <param name="shallowMaxDepth">Wading depth of the preset (water.shallow_max_depth).</param>
    /// <param name="shallowCost">The part of a water cell's base cost that stands for shallow water (see <see cref="WorldGenerator.WaterCost"/>).</param>
    /// <param name="floodedCost">Extra cost of a cell flooded above the wading depth, in the same unit.</param>
    public TideWalkLayer(WorldMap map, Tides tides, int shallowMaxDepth, int shallowCost, int floodedCost)
    {
        ArgumentNullException.ThrowIfNull(map);
        Tides = tides ?? throw new ArgumentNullException(nameof(tides));
        ArgumentOutOfRangeException.ThrowIfNegative(shallowCost);
        ArgumentOutOfRangeException.ThrowIfNegative(floodedCost);
        _shallowMaxDepth = shallowMaxDepth;
        _shallowCost = shallowCost;
        _floodedCost = floodedCost;
        int w = map.WidthCells, d = map.DepthCells;
        _base = new int[w * d];
        var tidal = new List<int>();
        var depth = new List<int>();
        for (var i = 0; i < _base.Length; i++)
        {
            var cell = map.Cell(i % w, i / w);
            _base[i] = cell.WalkCost;
            if (cell.Water == WaterKind.Salt && cell.WalkCost != MapCell.Blocked)
            {
                tidal.Add(i);
                depth.Add(cell.WaterDepth);
            }
        }

        _tidal = [.. tidal];
        _depth = [.. depth];
        var lowest = _base.Where(c => c != MapCell.Blocked).DefaultIfEmpty(1000).Min();
        MinCost = _tidal.Length == 0 ? lowest : Math.Min(lowest, _tidal.Min(i => Dry(_base[i])));
    }

    /// <summary>The tides the layer follows.</summary>
    public Tides Tides { get; }

    /// <summary>Number of sea cells whose cost follows the tide.</summary>
    public int TidalCells => _tidal.Length;

    /// <summary>The lowest walk cost any cell can have at any tide (keeps the path finder's estimate admissible).</summary>
    public int MinCost { get; }

    /// <summary>
    /// Walk costs of all cells at a tick (index z × width + x, <see cref="MapCell.Blocked"/> for obstacles).
    /// The array is kept until the tide level changes by a whole unit; callers must not change or keep it.
    /// </summary>
    public int[] CostsAt(long tick)
    {
        var level = Tides.LevelUnitsAt(tick);
        if (_costs is not null && _costsLevel == level)
        {
            return _costs;
        }

        _costs ??= (int[])_base.Clone();
        for (var t = 0; t < _tidal.Length; t++)
        {
            var i = _tidal[t];
            var water = _depth[t] + level;
            _costs[i] = level < 0 && water <= 0 ? Dry(_base[i])
                : level > 0 && water > _shallowMaxDepth ? _base[i] + _floodedCost
                : _base[i];
        }

        _costsLevel = level;
        return _costs;
    }

    private int Dry(int cost) => Math.Max(1, cost - _shallowCost);
}
