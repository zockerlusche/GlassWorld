using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Nature;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Objects;

/// <summary>A thing that came into the world after its start, with the tick it came (save game).</summary>
/// <param name="Id">Object id.</param>
/// <param name="PlacedAt">Tick it came into the world.</param>
public sealed record PlacedThing(string Id, long PlacedAt);

/// <summary>
/// D711 section 5 (plan D1008 H1, review GW1 #24077, values TH147): things wear away by their material class -
/// the first class whose TRUE properties fit. Wear is a pure function: ticks since the thing came plus the
/// extra ageing of the wet, frost, hot and frost-on-wet nature takts since then (R51 sums, O(1)), against the
/// class's hours; at the end the thing is gone (fresh food spoils, regrowth fills the place). Things of the world
/// start have a seeded age (no state), so they do not all end on one day. A queue by a lower bound of the end
/// (the largest factors) keeps the work at what is due. Silent: spoiling in nature is just regrowth.
/// </summary>
public sealed class DurabilitySystem : IWorldSystem, IOptionalWorldState
{
    private readonly DurabilitySettings _data;
    private readonly WorldObjects _objects;
    private readonly ITypeTruth _truth;
    private readonly int _ticksPerHour;
    private readonly ulong _seed;
    private readonly Dictionary<string, MaterialClass?> _classOf = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _placedAt = new(StringComparer.Ordinal);
    private readonly SortedSet<(long Due, string Id)> _queue = [];
    private readonly Dictionary<string, long> _dueOf = new(StringComparer.Ordinal);
    private bool _started;

    /// <summary>Creates the system.</summary>
    public DurabilitySystem(DurabilitySettings data, WorldObjects objects, int ticksPerHour, ulong seed, NatureSystem? nature)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(objects);
        _data = data;
        _objects = objects;
        _truth = objects.Catalog;
        _ticksPerHour = ticksPerHour;
        _seed = seed;
        Nature = nature;
        // Things that come before the first tick (the island, a test's setup) count as start things (seeded age).
        _objects.Spawned += thing =>
        {
            if (_started)
            {
                Track(thing, _now);
            }
        };

        // A thing that leaves the world is forgotten at once, so a loaded world and the running one agree.
        _objects.Removed += thing =>
        {
            _placedAt.Remove(thing.Id);
            if (_dueOf.Remove(thing.Id, out var due))
            {
                _queue.Remove((due, thing.Id));
            }
        };
    }

    /// <summary>Whether a thing grows in its place (a berry on its bush, a regrowth place of the island): it does not wear there.</summary>
    public Func<string, bool>? GrowsInPlace { get; set; }

    /// <summary>The nature takt whose influence sums age things faster (R51), or null (time only).</summary>
    public NatureSystem? Nature { get; }

    /// <inheritdoc/>
    public string Name => "durability";

    /// <inheritdoc/>
    public bool HasState => _placedAt.Count > 0;

    private long _now;

    /// <summary>The wear of a thing in permille of its life now, or null when it does not wear away.</summary>
    public long? WearPermille(WorldObject thing, long now)
    {
        ArgumentNullException.ThrowIfNull(thing);
        if (ClassOf(thing.TypeId) is not { BaseHours: { } hours } material || GrowsInPlace?.Invoke(thing.Id) == true)
        {
            return null;
        }

        var life = (long)hours * _ticksPerHour;
        var since = _placedAt.TryGetValue(thing.Id, out var placed) ? placed : -StartAge(thing.Id, life);
        var aged = now - since;
        if (Nature is { } nature && since >= 0)
        {
            var influences = nature.InfluencesSince(since, now);
            aged += ((influences.Wet * (material.WetPermille ?? 0)) + (influences.Frost * (material.FrostPermille ?? 0))
                + (influences.Heat * (material.HeatPermille ?? 0)) + (influences.WetFrost * (material.WetFrostPermille ?? 0))) * nature.TaktTicks / 1000;
        }

        return aged * 1000 / life;
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _now = world.Tick;
        if (!_started)
        {
            _started = true;
            foreach (var thing in _objects.All.ToList())
            {
                Track(thing, null);
            }
        }

        while (_queue.Count > 0 && _queue.Min.Due <= world.Tick)
        {
            var (_, id) = _queue.Min;
            _queue.Remove(_queue.Min);
            _dueOf.Remove(id);
            if (!_objects.TryGet(id, out var thing))
            {
                _placedAt.Remove(id);
                continue;
            }

            if (WearPermille(thing, world.Tick) is not { } wear)
            {
                // Growing in its place now: looked at again later (it may be picked and put down).
                Enqueue(thing, world.Tick, 0);
                continue;
            }

            if (wear >= 1000)
            {
                _objects.Remove(id);
            }
            else
            {
                Enqueue(thing, world.Tick, wear);
            }
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_placedAt.Count);
        foreach (var (id, placed) in _placedAt)
        {
            hasher.Write(id).Write(placed);
        }
    }

    /// <summary>The things placed after the start, for a save game.</summary>
    public IReadOnlyList<PlacedThing> Capture() => [.. _placedAt.Select(p => new PlacedThing(p.Key, p.Value))];

    /// <summary>Restores the placed things; the queue is rebuilt from the world at the next advance.</summary>
    public void Restore(IReadOnlyList<PlacedThing>? placed, long tick)
    {
        foreach (var thing in placed ?? [])
        {
            _placedAt[thing.Id] = thing.PlacedAt;
        }

        _now = tick;
        _started = false;
        _queue.Clear();
        _dueOf.Clear();
    }

    /// <summary>Starts watching a thing; <paramref name="placedAt"/> null = it was there at the start (seeded age).</summary>
    private void Track(WorldObject thing, long? placedAt)
    {
        if (ClassOf(thing.TypeId) is not { BaseHours: not null } || _dueOf.ContainsKey(thing.Id))
        {
            return;
        }

        if (placedAt is { } tick && !_placedAt.ContainsKey(thing.Id))
        {
            _placedAt[thing.Id] = tick;
        }

        Enqueue(thing, _now, WearPermille(thing, _now) ?? 0);
    }

    /// <summary>Queues a thing at the earliest tick it could end: the rest of its life at the fastest ageing the data allow.</summary>
    private void Enqueue(WorldObject thing, long now, long wear)
    {
        var material = ClassOf(thing.TypeId)!;
        var life = (long)material.BaseHours!.Value * _ticksPerHour;
        var rest = Math.Max(1L, (1000 - wear) * life / 1000);
        var fastest = 1000L + (material.WetPermille ?? 0) + (material.FrostPermille ?? 0) + (material.HeatPermille ?? 0) + (material.WetFrostPermille ?? 0);
        var due = now + Math.Max(1L, rest * 1000 / fastest);
        _queue.Add((due, thing.Id));
        _dueOf[thing.Id] = due;
    }

    /// <summary>The seeded age of a thing of the world start: a pure function of world seed and object id (review GW1 #24077 A).</summary>
    private long StartAge(string id, long life)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(_seed.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + id));
        return (long)(BitConverter.ToUInt64(hash, 0) % (ulong)life);
    }

    private MaterialClass? ClassOf(string typeId)
    {
        if (_classOf.TryGetValue(typeId, out var known))
        {
            return known;
        }

        var truth = _truth.Contains(typeId) ? _truth.TrueProperties(typeId) : FrozenSet<string>.Empty;
        var found = _data.Classes.FirstOrDefault(c => c.PropertiesAll.All(truth.Contains) && !c.PropertiesNone.Any(truth.Contains));
        _classOf[typeId] = found;
        return found;
    }
}
