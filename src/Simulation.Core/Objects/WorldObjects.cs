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

/// <summary>The objects of one type that are in the world right now, in id order.</summary>
/// <param name="TypeId">Object type id.</param>
/// <param name="Objects">Its objects in id order; empty once all of them are gone.</param>
public sealed record ObjectsOfType(string TypeId, IReadOnlyList<WorldObject> Objects);

/// <summary>
/// All object instances of the world (a world system). Ids come from a counter that is part of the state,
/// so the same history always yields the same ids.
/// </summary>
public sealed class WorldObjects : IWorldSystem
{
    // The objects by id for the look-up and in id order for every pass, hash and save.
    private readonly Dictionary<string, WorldObject> _objects = new(StringComparer.Ordinal);
    private readonly List<WorldObject> _ordered = [];

    // Index, not state (it follows from the objects): the objects of each type in id order, the types in
    // ordinal order. Whoever asks about a kind of thing (the nearest thing of a type nobody knows) walks
    // the objects of that type instead of every object of the world.
    private readonly List<ObjectsOfType> _byType = [];
    private readonly List<List<WorldObject>> _lists = [];

    /// <summary>Creates the registry for a type truth (the world's catalog, or a content catalog in tests).</summary>
    public WorldObjects(ITypeTruth catalog) => Catalog = catalog;

    /// <summary>The truth about types.</summary>
    public ITypeTruth Catalog { get; }

    /// <summary>Next number for an object id.</summary>
    public long NextNumber { get; private set; } = 1;

    /// <inheritdoc/>
    public string Name => "objects";

    /// <summary>All objects in id order.</summary>
    public IEnumerable<WorldObject> All => _ordered;

    /// <summary>Number of objects.</summary>
    public int Count => _ordered.Count;

    /// <summary>
    /// Adds the objects that lie within <paramref name="radius"/> metres of a position (the border counts)
    /// to <paramref name="into"/>, in id order.
    /// </summary>
    public void Within(Position centre, double radius, List<WorldObject> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        var limit = radius * radius;
        foreach (var obj in _ordered)
        {
            var dx = centre.X - obj.Position.X;
            var dz = centre.Z - obj.Position.Z;
            if ((dx * dx) + (dz * dz) <= limit)
            {
                into.Add(obj);
            }
        }
    }

    /// <summary>
    /// The objects by type: every type that has had an object in this world, in ordinal order of the type
    /// ids, each with its objects of now in id order.
    /// </summary>
    public IReadOnlyList<ObjectsOfType> ByType => _byType;

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
        Index(obj);
        return obj;
    }

    /// <summary>Looks up an object.</summary>
    public bool TryGet(string id, out WorldObject obj) => _objects.TryGetValue(id, out obj!);

    /// <summary>Removes an object (used up, eaten …). Its id is never given out again.</summary>
    public bool Remove(string id)
    {
        if (!_objects.TryGetValue(id, out var obj))
        {
            return false;
        }

        _objects.Remove(id);

        var ofType = OfType(obj.TypeId);
        ofType.RemoveAt(Place(ofType, id));
        _ordered.RemoveAt(Place(_ordered, id));
        return true;
    }

    /// <summary>Restores an object with its original id (save games).</summary>
    public void Restore(WorldObject obj, long nextNumber)
    {
        ArgumentNullException.ThrowIfNull(obj);
        _objects.Add(obj.Id, obj);
        Index(obj);
        NextNumber = Math.Max(NextNumber, nextNumber);
    }

    private void Index(WorldObject obj)
    {
        var ofType = OfType(obj.TypeId);
        ofType.Insert(~Place(ofType, obj.Id), obj);
        _ordered.Insert(~Place(_ordered, obj.Id), obj);
    }

    /// <summary>The list of a type's objects, added in its place among the types when the type is new.</summary>
    private List<WorldObject> OfType(string typeId)
    {
        var (low, high) = (0, _byType.Count - 1);
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var order = string.CompareOrdinal(_byType[mid].TypeId, typeId);
            if (order == 0)
            {
                return _lists[mid];
            }

            (low, high) = order < 0 ? (mid + 1, high) : (low, mid - 1);
        }

        var objects = new List<WorldObject>();
        _byType.Insert(low, new ObjectsOfType(typeId, objects));
        _lists.Insert(low, objects);
        return objects;
    }

    /// <summary>Index of an id in a list in id order, or the complement of where it belongs.</summary>
    private static int Place(List<WorldObject> objects, string id)
    {
        var (low, high) = (0, objects.Count - 1);
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var order = string.CompareOrdinal(objects[mid].Id, id);
            if (order == 0)
            {
                return mid;
            }

            (low, high) = order < 0 ? (mid + 1, high) : (low, mid - 1);
        }

        return ~low;
    }

    /// <summary>Restores the id counter (save games; also when no object exists any more).</summary>
    public void Restore(long nextNumber) => NextNumber = Math.Max(NextNumber, nextNumber);

    /// <inheritdoc/>
    public void Advance(World world)
    {
        // Objects are passive in B1; growth and decay follow in B5.
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(NextNumber).Write(_ordered.Count);
        foreach (var obj in _ordered)
        {
            hasher.Write(obj.Id).Write(obj.TypeId).Write(obj.Position.X).Write(obj.Position.Z);
        }
    }
}
