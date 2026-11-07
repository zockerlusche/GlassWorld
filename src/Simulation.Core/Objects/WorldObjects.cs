using System.Globalization;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Objects;

/// <summary>Position on the island in metres (x east, z south).</summary>
/// <param name="X">East coordinate.</param>
/// <param name="Z">South coordinate.</param>
public readonly record struct Position(double X, double Z);

/// <summary>An object instance in the world. Its truth is its type in the <see cref="ObjectCatalog"/>.</summary>
/// <param name="Id">Entity id, e.g. <c>obj-000042</c>; assigned once, never reused.</param>
/// <param name="TypeId">Object type id.</param>
/// <param name="Position">Where it lies.</param>
public sealed record WorldObject(string Id, string TypeId, Position Position);

/// <summary>
/// All object instances of the world (a world system). Ids come from a counter that is part of the state,
/// so the same history always yields the same ids.
/// </summary>
public sealed class WorldObjects : IWorldSystem
{
    private readonly SortedDictionary<string, WorldObject> _objects = new(StringComparer.Ordinal);

    /// <summary>Creates the registry for a catalog.</summary>
    public WorldObjects(ObjectCatalog catalog) => Catalog = catalog;

    /// <summary>The truth about types.</summary>
    public ObjectCatalog Catalog { get; }

    /// <summary>Next number for an object id.</summary>
    public long NextNumber { get; private set; } = 1;

    /// <inheritdoc/>
    public string Name => "objects";

    /// <summary>All objects in id order.</summary>
    public IEnumerable<WorldObject> All => _objects.Values;

    /// <summary>Number of objects.</summary>
    public int Count => _objects.Count;

    /// <summary>Creates an object of a known type.</summary>
    public WorldObject Spawn(string typeId, Position position)
    {
        if (!Catalog.Contains(typeId))
        {
            throw new ArgumentException($"Unknown object type '{typeId}'.", nameof(typeId));
        }

        var obj = new WorldObject(string.Create(CultureInfo.InvariantCulture, $"obj-{NextNumber:000000}"), typeId, position);
        NextNumber++;
        _objects.Add(obj.Id, obj);
        return obj;
    }

    /// <summary>Looks up an object.</summary>
    public bool TryGet(string id, out WorldObject obj) => _objects.TryGetValue(id, out obj!);

    /// <summary>Removes an object (used up, eaten …). Its id is never given out again.</summary>
    public bool Remove(string id) => _objects.Remove(id);

    /// <summary>Restores an object with its original id (save games).</summary>
    public void Restore(WorldObject obj, long nextNumber)
    {
        ArgumentNullException.ThrowIfNull(obj);
        _objects.Add(obj.Id, obj);
        NextNumber = Math.Max(NextNumber, nextNumber);
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        // Objects are passive in B1; growth and decay follow in B5.
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(NextNumber).Write(_objects.Count);
        foreach (var obj in _objects.Values)
        {
            hasher.Write(obj.Id).Write(obj.TypeId).Write(obj.Position.X).Write(obj.Position.Z);
        }
    }
}
