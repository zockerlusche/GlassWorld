using GlassWorld.Contracts;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Nature;

/// <summary>The animal stocks as saved (B4b K4): "species|biome" → stock in thousandths of an animal.</summary>
/// <param name="Stock">Stock per species and biome, in thousandths (integers only, deterministic).</param>
public sealed record AnimalStockState(IReadOnlyDictionary<string, long> Stock);

/// <summary>
/// B4b K4 (A1577, D711 §2, B2645): animals as a stock per species and biome - one number that grows towards a limit,
/// no animal of its own and no killing (B2481). Once per game day each stock grows logistically by the data's rate and
/// season factor. How full the stocks of a species are scales the growth groups of what it leaves behind (egg, wool,
/// feather, dung) through <see cref="FactorOf"/> (GW4's hook in the nature takt). The system runs after the nature
/// takt in a tick, so the takt always reads the stock as it was at its start (a run, a replay and a load agree).
/// </summary>
public sealed class AnimalStockSystem : IWorldSystem, IOptionalWorldState
{
    private const long Milli = 1000;
    private readonly NatureAnimals _data;
    private readonly long _ticksPerDay;
    private readonly SortedDictionary<string, long> _stock = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _cap = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _keysOfGroup = new(StringComparer.Ordinal);
    private long _lastDay = -1;

    /// <summary>Sets up the stocks of the biomes the island has (<paramref name="biomesOnIsland"/>), each at its start share.</summary>
    public AnimalStockSystem(NatureAnimals data, long ticksPerDay, IReadOnlySet<string> biomesOnIsland)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        ArgumentNullException.ThrowIfNull(biomesOnIsland);
        _ticksPerDay = Math.Max(1, ticksPerDay);
        foreach (var species in data.Species)
        {
            foreach (var biome in species.Biomes.Where(biomesOnIsland.Contains))
            {
                var key = Key(species.Id, biome);
                var cap = species.CapPerBiome * Milli;
                _cap[key] = cap;
                _stock[key] = cap * species.StartPermille / 1000;
                foreach (var group in species.Groups)
                {
                    if (!_keysOfGroup.TryGetValue(group, out var keys))
                    {
                        _keysOfGroup[group] = keys = [];
                    }

                    keys.Add(key);
                }
            }
        }
    }

    /// <inheritdoc/>
    public string Name => "animals";

    /// <inheritdoc/>
    public bool HasState => _stock.Count > 0;

    /// <summary>The stock of a species in a biome in whole animals (rounded down), 0 when it does not live there.</summary>
    public long StockOf(string species, string biome) => _stock.TryGetValue(Key(species, biome), out var stock) ? stock / Milli : 0;

    /// <summary>
    /// The factor (permille) by which the stocks scale a growth group: their sum over their limits; null when no stock
    /// feeds the group (it grows by season alone). Hook for <see cref="NatureSystem.FactorOf"/>; the takt is not needed,
    /// the stock changes only after the nature takt has opened in a tick.
    /// </summary>
    public int? FactorOf(string group, long takt)
    {
        if (!_keysOfGroup.TryGetValue(group, out var keys) || keys.Count == 0)
        {
            return null;
        }

        long stock = 0;
        long cap = 0;
        foreach (var key in keys)
        {
            stock += _stock[key];
            cap += _cap[key];
        }

        return cap <= 0 ? 0 : (int)(stock * 1000 / cap);
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var day = world.Tick / _ticksPerDay;
        if (world.Tick % _ticksPerDay != 0 || day == _lastDay)
        {
            return;
        }

        _lastDay = day;
        if (world.Tick == 0)
        {
            return;
        }

        var season = world.Now.SeasonId;
        foreach (var species in _data.Species)
        {
            var factor = species.Seasons?.FirstOrDefault(s => string.Equals(s.Season, season, StringComparison.Ordinal))?.Permille ?? 1000;
            foreach (var biome in species.Biomes)
            {
                var key = Key(species.Id, biome);
                if (!_stock.TryGetValue(key, out var stock))
                {
                    continue;
                }

                // Logistic: the stock grows by its rate and the season, slower the nearer it is to the limit; never past it.
                var cap = _cap[key];
                // One division at the end (review GW4 #422): rounding in each step let a small stock stop growing. Int128
                // keeps the product exact (stock and limit in thousandths x rate x season x room).
                var growth = (long)((Int128)stock * species.GrowthPerDayPermille * factor * (cap - stock) / ((Int128)1_000_000 * cap));
                _stock[key] = Math.Clamp(stock + growth, 0, cap);
            }
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_stock.Count);
        foreach (var (key, stock) in _stock)
        {
            hasher.Write(key).Write(stock);
        }
    }

    /// <summary>The state to save.</summary>
    public AnimalStockState Capture() => new(new SortedDictionary<string, long>(_stock, StringComparer.Ordinal));

    /// <summary>Takes a saved state; stocks of biomes the island does not have (or no longer in the data) are left out.</summary>
    public void Restore(AnimalStockState? state)
    {
        if (state is null)
        {
            return;
        }

        foreach (var (key, stock) in state.Stock)
        {
            if (_cap.TryGetValue(key, out var cap))
            {
                _stock[key] = Math.Clamp(stock, 0, cap);
            }
        }
    }

    private static string Key(string species, string biome) => species + "|" + biome;
}
