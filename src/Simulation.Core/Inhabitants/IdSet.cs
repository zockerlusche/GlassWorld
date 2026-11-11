using System.Collections;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>
/// A set of entity ids an inhabitant keeps (whom it has met, whose name it knows): read in ordinal order
/// (hash, save game, lists for the AI), asked "is this one in it?" for every pair of inhabitants in every
/// tick. So it answers that from a hash table, and <see cref="Version"/> tells whoever keeps something
/// worked out from the set whether it still holds.
/// </summary>
public sealed class IdSet : IReadOnlyCollection<string>
{
    private readonly SortedSet<string> _ordered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    /// <summary>Number of ids.</summary>
    public int Count => _ordered.Count;

    /// <summary>Changes with every id added or removed (never with a call that changed nothing).</summary>
    public int Version { get; private set; }

    /// <summary>Whether the id is in the set.</summary>
    public bool Contains(string id) => _ids.Contains(id);

    /// <summary>Adds an id; false if it was there already.</summary>
    public bool Add(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!_ids.Add(id))
        {
            return false;
        }

        _ordered.Add(id);
        Version++;
        return true;
    }

    /// <summary>Removes an id; false if it was not there.</summary>
    public bool Remove(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!_ids.Remove(id))
        {
            return false;
        }

        _ordered.Remove(id);
        Version++;
        return true;
    }

    /// <summary>Adds every id of <paramref name="ids"/>.</summary>
    public void UnionWith(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var id in ids)
        {
            Add(id);
        }
    }

    /// <summary>The ids in ordinal order.</summary>
    public IEnumerator<string> GetEnumerator() => _ordered.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
