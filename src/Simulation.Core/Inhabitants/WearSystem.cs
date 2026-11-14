using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>What an inhabitant wears (B4b K4, B2752): the thing's own id and type, kept while it is off the ground.</summary>
/// <param name="Id">The object's id; it comes back with this id when laid down.</param>
/// <param name="TypeId">Its type (the body reads its true properties, e.g. insulating).</param>
public sealed record WornThing(string Id, string TypeId);

/// <summary>The worn things as saved: inhabitant id → what it wears.</summary>
/// <param name="Worn">Inhabitant id → worn thing.</param>
public sealed record WearState(IReadOnlyDictionary<string, WornThing> Worn);

/// <summary>
/// B4b K4 part 2 (B2752): one thing an inhabitant wears on its body. Putting on (an effects rule with
/// <c>wear: on</c> on a thing with the property <c>wearable</c>) takes the thing off the ground: it leaves the world's
/// objects - nobody else sees, takes or uses it - and its id and type are kept here, so nothing is lost. Taking off
/// (<c>wear: off</c>) lays it down at the wearer's feet with the same id. Warmth comes only through the worn thing's
/// true properties (the body system asks <see cref="WornTypeOf"/>), never through the function (TL #25587).
/// While worn a thing keeps its age by time; durability watches it again when it is laid down (no wear-out while worn).
/// </summary>
public sealed class WearSystem : IWorldSystem, IOptionalWorldState
{
    private readonly WorldObjects _objects;
    private readonly SortedDictionary<string, WornThing> _worn = new(StringComparer.Ordinal);

    /// <summary>Creates the system over the world's objects.</summary>
    public WearSystem(WorldObjects objects) => _objects = objects ?? throw new ArgumentNullException(nameof(objects));

    /// <inheritdoc/>
    public string Name => "wear";

    /// <inheritdoc/>
    public bool HasState => _worn.Count > 0;

    /// <summary>What an inhabitant wears, or null.</summary>
    public WornThing? WornBy(string inhabitantId) => _worn.GetValueOrDefault(inhabitantId);

    /// <summary>The type of what an inhabitant wears, or null (for the body's warmth).</summary>
    public string? WornTypeOf(string inhabitantId) => _worn.GetValueOrDefault(inhabitantId)?.TypeId;

    /// <summary>
    /// Puts <paramref name="target"/> on (it leaves the ground), or with null takes off what the inhabitant wears and lays
    /// it at <paramref name="at"/>. True when something changed: one thing at a time, a thing must still be there.
    /// </summary>
    public bool Wear(Inhabitant inhabitant, WorldObject? target, Position at)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        if (target is null)
        {
            if (!_worn.Remove(inhabitant.Id, out var worn))
            {
                return false;
            }

            return _objects.Lay(new WorldObject(worn.Id, worn.TypeId, at));
        }

        if (_worn.ContainsKey(inhabitant.Id) || !_objects.TryGet(target.Id, out var thing) || !_objects.Lift(thing.Id))
        {
            return false;
        }

        _worn[inhabitant.Id] = new WornThing(thing.Id, thing.TypeId);
        return true;
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        // Nothing moves on its own: a worn thing travels with its wearer because it is not on the ground.
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_worn.Count);
        foreach (var (inhabitant, worn) in _worn)
        {
            hasher.Write(inhabitant).Write(worn.Id).Write(worn.TypeId);
        }
    }

    /// <summary>The state to save.</summary>
    public WearState Capture() => new(new SortedDictionary<string, WornThing>(_worn, StringComparer.Ordinal));

    /// <summary>Takes a saved state (the worn things are not among the saved objects; they come back from here).</summary>
    public void Restore(WearState? state)
    {
        _worn.Clear();
        foreach (var (inhabitant, worn) in state?.Worn ?? new Dictionary<string, WornThing>())
        {
            _worn[inhabitant] = worn;
        }
    }
}
