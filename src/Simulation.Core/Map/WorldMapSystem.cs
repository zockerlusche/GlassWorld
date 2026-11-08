using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Map;

/// <summary>
/// One placed instance of a regrowing spawn rule (minimal regrowth before B5): the object now standing
/// there (or the one used up), its cell and since when it is gone.
/// </summary>
/// <param name="ObjectId">Object currently at this place, or the last one that was used up.</param>
/// <param name="TypeId">Catalogue type that grows back.</param>
/// <param name="X">Cell x.</param>
/// <param name="Z">Cell z.</param>
/// <param name="GoneSinceTick">Tick at which the object was first missed; null while it exists.</param>
public sealed record Occurrence(string ObjectId, string TypeId, int X, int Z, long? GoneSinceTick);

/// <summary>
/// The generated start island as a world system (B2 R1): the base grid never changes after generation,
/// so the system does nothing per tick and writes the island data into the state hash. It is rebuilt
/// from seed and preset when a save game is loaded (the generator is deterministic).
/// </summary>
public sealed class WorldMapSystem : IWorldSystem
{
    private readonly List<Occurrence> _occurrences = [];
    private readonly IReadOnlyDictionary<string, long> _regrowTicks;
    private readonly WorldObjects? _objects;

    /// <summary>Creates the system around a generated island (no regrowth).</summary>
    public WorldMapSystem(GeneratedIsland island, int cellSizeCm)
        : this(island, cellSizeCm, null, new Dictionary<string, long>(StringComparer.Ordinal))
    {
    }

    /// <summary>
    /// Creates the system around a generated island; objects of the types in
    /// <paramref name="regrowTicks"/> grow back at their place that many ticks after they were used up.
    /// </summary>
    public WorldMapSystem(GeneratedIsland island, int cellSizeCm, WorldObjects? objects, IReadOnlyDictionary<string, long> regrowTicks)
    {
        ArgumentNullException.ThrowIfNull(island);
        ArgumentNullException.ThrowIfNull(regrowTicks);
        ArgumentOutOfRangeException.ThrowIfLessThan(cellSizeCm, 1);
        Island = island;
        CellSizeCm = cellSizeCm;
        IslandHash = island.Hash();
        _objects = objects;
        _regrowTicks = regrowTicks;
    }

    /// <summary>Places that grow back, in the order they were placed.</summary>
    public IReadOnlyList<Occurrence> Occurrences => _occurrences;

    /// <summary>Remembers a placed object if its type grows back.</summary>
    public void Track(WorldObject obj, int x, int z)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (_regrowTicks.ContainsKey(obj.TypeId))
        {
            _occurrences.Add(new Occurrence(obj.Id, obj.TypeId, x, z, null));
        }
    }

    /// <summary>Restores the places that grow back (save games).</summary>
    public void Restore(IEnumerable<Occurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        _occurrences.Clear();
        _occurrences.AddRange(occurrences);
    }

    /// <summary>Hash of the generated data, computed once (the grid never changes).</summary>
    public string IslandHash { get; }

    /// <inheritdoc/>
    public string Name => "worldmap";

    /// <summary>The generated island (map, objects, starts, check).</summary>
    public GeneratedIsland Island { get; }

    /// <summary>The base grid.</summary>
    public WorldMap Map => Island.Map;

    /// <summary>Edge length of a cell in centimetres (from the preset).</summary>
    public int CellSizeCm { get; }

    /// <summary>World position (metres, origin at the map corner) of the centre of cell (x, z).</summary>
    public Position CentreOf(int x, int z) => new((x + 0.5) * CellSizeCm / 100.0, (z + 0.5) * CellSizeCm / 100.0);

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_objects is null)
        {
            return;
        }

        for (var i = 0; i < _occurrences.Count; i++)
        {
            var place = _occurrences[i];
            if (place.GoneSinceTick is null)
            {
                if (!_objects.TryGet(place.ObjectId, out _))
                {
                    _occurrences[i] = place with { GoneSinceTick = world.Tick };
                }

                continue;
            }

            if (world.Tick - place.GoneSinceTick.Value >= _regrowTicks[place.TypeId])
            {
                var grown = _objects.Spawn(place.TypeId, CentreOf(place.X, place.Z));
                _occurrences[i] = place with { ObjectId = grown.Id, GoneSinceTick = null };
            }
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(Island.PresetId).Write(IslandHash).Write(_occurrences.Count);
        foreach (var place in _occurrences)
        {
            hasher.Write(place.ObjectId).Write(place.TypeId).Write(place.X).Write(place.Z).Write(place.GoneSinceTick ?? -1);
        }
    }
}
