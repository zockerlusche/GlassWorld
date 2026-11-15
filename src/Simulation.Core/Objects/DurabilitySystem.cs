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

/// <summary>A thing worn beyond time and weather (A1576, plan D1008 H2): by use, or by damage (save game).</summary>
/// <param name="Id">Object id.</param>
/// <param name="WearPermille">The extra wear so far, permille of its life (before quality).</param>
public sealed record UsedThing(string Id, long WearPermille);

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
    private readonly SortedDictionary<string, long> _extra = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _usedNow = new(StringComparer.Ordinal);
    private readonly FrozenSet<string> _useActions;
    private bool _started;

    /// <summary>Creates the system.</summary>
    public DurabilitySystem(DurabilitySettings data, WorldObjects objects, int ticksPerHour, ulong seed, NatureSystem? nature)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(objects);
        _data = data;
        _objects = objects;
        _truth = objects.Catalog;
        _useActions = (data.UseActions ?? []).ToFrozenSet(StringComparer.Ordinal);
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

        // A processed thing keeps its age and is queued anew by its new class, as a loaded world does (GW2 #492).
        _objects.TypeChanged += (_, now) =>
        {
            if (_dueOf.Remove(now.Id, out var was))
            {
                _queue.Remove((was, now.Id));
            }

            if (_started)
            {
                Track(now, null);
            }
        };

        // A thing that leaves the world is forgotten at once, so a loaded world and the running one agree.
        _objects.Removed += thing =>
        {
            _placedAt.Remove(thing.Id);
            _extra.Remove(thing.Id);
            _usedNow.Remove(thing.Id);
            if (_dueOf.Remove(thing.Id, out var due))
            {
                _queue.Remove((due, thing.Id));
            }
        };
    }

    /// <summary>Whether a thing grows in its place (a berry on its bush, a regrowth place of the island): it does not wear there.</summary>
    public Func<string, bool>? GrowsInPlace { get; set; }

    /// <summary>A1576 (H2): the quality tier of a made thing's type (build quality), or null.</summary>
    public Func<string, string?>? QualityOf { get; set; }

    /// <summary>A1576 (review GW1 #497): who made a thing of this type (its builder, else its inventor), or null.</summary>
    public Func<string, string?>? MakerOf { get; set; }

    /// <summary>A1576 (H2): where the end of a made or carried thing is told (spoiled, broke); null = nowhere.</summary>
    public Action<Inhabitants.WorldEvent>? Report { get; set; }

    /// <summary>The nature takt whose influence sums age things faster (R51), or null (time only).</summary>
    public NatureSystem? Nature { get; }

    /// <inheritdoc/>
    public string Name => "durability";

    /// <inheritdoc/>
    public bool HasState => _placedAt.Count > 0 || _extra.Count > 0;

    private long _now;

    /// <summary>The wear of a thing in permille of its life now, or null when it does not wear away.</summary>
    public long? WearPermille(WorldObject thing, long now)
    {
        ArgumentNullException.ThrowIfNull(thing);
        if (ClassOf(thing.TypeId) is not { } material || (material.BaseHours is null && material.UsePermille is not > 0 && !_extra.ContainsKey(thing.Id)) || GrowsInPlace?.Invoke(thing.Id) == true)
        {
            return null;
        }

        // A1576 (H2): use wears it too, and a made thing's quality stretches the whole life.
        var used = _extra.GetValueOrDefault(thing.Id);
        var quality = LifePermille(thing.TypeId);
        if (material.BaseHours is not { } hours)
        {
            return used * 1000 / quality;
        }

        var life = (long)hours * _ticksPerHour;
        var since = _placedAt.TryGetValue(thing.Id, out var placed) ? placed : -StartAge(thing.Id, life);
        var aged = now - since;
        if (Nature is { } nature)
        {
            // Start things went through the weather since the world began (review GW1 #382).
            var influences = nature.InfluencesSince(Math.Max(since, 0), now);
            aged += ((influences.Wet * (material.WetPermille ?? 0)) + (influences.Frost * (material.FrostPermille ?? 0))
                + (influences.Heat * (material.HeatPermille ?? 0)) + (influences.WetFrost * (material.WetFrostPermille ?? 0))) * nature.TaktTicks / 1000;
        }

        return ((aged * 1000 / life) + used) * 1000 / quality;
    }

    /// <summary>
    /// A1576 (H2): an event of an inhabitant - the end of a use action (data use_actions) wears its target thing by one use;
    /// the thing is looked at in the next step.
    /// </summary>
    public void Observe(Inhabitants.WorldEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Kind != Inhabitants.InhabitantsSystem.ActionFinished || e.Action is null || !_useActions.Contains(e.Action) || e.TargetId is not { } id
            || !_objects.TryGet(id, out var thing) || ClassOf(thing.TypeId) is not { UsePermille: > 0 } material)
        {
            return;
        }

        AddWear(id, material.UsePermille!.Value);
    }

    /// <summary>
    /// A1576 (review GW1 #26993 d): wears a thing by <paramref name="permille"/> of its life beyond time and weather - one
    /// use, or damage (a storm on a building, A1382 S2); it is looked at in the next step. Things of no material class stay.
    /// </summary>
    public void AddWear(string id, long permille)
    {
        if (permille <= 0 || !_objects.TryGet(id, out var thing) || ClassOf(thing.TypeId) is null)
        {
            return;
        }

        _extra[id] = _extra.GetValueOrDefault(id) + permille;
        _usedNow.Add(id);
    }

    /// <summary>The extra wear of a thing so far, permille of its life before quality (A1576).</summary>
    public long ExtraWearOf(string id) => _extra.GetValueOrDefault(id);

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

        // A1576 (H2): things used since the last step - worn out now, or queued anew by their new wear.
        foreach (var id in _usedNow.ToList())
        {
            _usedNow.Remove(id);
            if (_objects.TryGet(id, out var used) && WearPermille(used, world.Tick) is { } usedWear)
            {
                if (usedWear >= 1000)
                {
                    End(world, used);
                }
                else if (_dueOf.Remove(id, out var was))
                {
                    _queue.Remove((was, id));
                    Enqueue(used, world.Tick, usedWear);
                }
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
                End(world, thing);
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

        // A1576: only once something was used, so worlds without use keep their hashes.
        if (_extra.Count > 0)
        {
            hasher.Write("extra_wear").Write(_extra.Count);
            foreach (var (id, wear) in _extra)
            {
                hasher.Write(id).Write(wear);
            }
        }
    }

    /// <summary>
    /// A thing that grew in its place was taken from it at <paramref name="tick"/> (picked, AK1a): fresh from the plant, it
    /// ages from now on, and it is looked at again by that - a load and the running world then agree on when it spoils.
    /// </summary>
    public void LeftPlace(string id, long tick)
    {
        if (!_objects.TryGet(id, out var thing) || ClassOf(thing.TypeId) is not { BaseHours: not null })
        {
            return;
        }

        _placedAt[id] = tick;
        if (_dueOf.Remove(id, out var due))
        {
            _queue.Remove((due, id));
        }

        Enqueue(thing, tick, 0);
    }

    /// <summary>The things placed after the start, for a save game.</summary>
    public IReadOnlyList<PlacedThing> Capture() => [.. _placedAt.Select(p => new PlacedThing(p.Key, p.Value))];

    /// <summary>The things worn by use, for a save game (A1576).</summary>
    public IReadOnlyList<UsedThing> CaptureUses() => [.. _extra.Select(u => new UsedThing(u.Key, u.Value))];

    /// <summary>Restores the placed and used things; the queue is rebuilt from the world at the next advance.</summary>
    public void Restore(IReadOnlyList<PlacedThing>? placed, long tick, IReadOnlyList<UsedThing>? used = null)
    {
        foreach (var thing in placed ?? [])
        {
            _placedAt[thing.Id] = thing.PlacedAt;
        }

        foreach (var thing in used ?? [])
        {
            _extra[thing.Id] = thing.WearPermille;
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
            // A class without a life wears by use only: nothing to queue (A1576).
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
        // The rest of the life, stretched by quality (A1576); use only ever brings the end nearer and is looked at when it happens.
        var rest = Math.Max(1L, (1000 - wear) * life / 1000 * LifePermille(thing.TypeId) / 1000);
        var fastest = 1000L + (material.WetPermille ?? 0) + (material.FrostPermille ?? 0) + (material.HeatPermille ?? 0) + (material.WetFrostPermille ?? 0);
        // In its last hour it is looked at every tick, so its end is the first tick its wear reaches 1000 - the same in a
        // running and a loaded world, whenever it was queued (GW2 #492: before, the end came a tick or two apart).
        var delay = Math.Max(1L, rest * 1000 / fastest);
        var due = now + (delay <= _ticksPerHour ? 1 : delay);
        _queue.Add((due, thing.Id));
        _dueOf[thing.Id] = due;
    }

    /// <summary>
    /// The end of a thing: gone; a made thing (data end_event_if) or one an inhabitant holds tells its class's end event
    /// (spoiled, broke) - nature's own things end in silence (plan D1008, review GW1 #24077).
    /// </summary>
    private void End(World world, WorldObject thing)
    {
        var holder = _objects.HeldBy(thing.Id);
        var made = _data.EndEventIf is { } madeProperty && _truth.Contains(thing.TypeId) && _truth.TrueProperties(thing.TypeId).Contains(madeProperty);
        var endEvent = ClassOf(thing.TypeId)?.EndEvent;
        _objects.Remove(thing.Id);
        // Told to whoever holds or wears it, else to its maker; with neither it ends in silence (review GW1 #497).
        var told = holder ?? (made ? MakerOf?.Invoke(thing.TypeId) : null);
        if (endEvent is not null && told is not null)
        {
            Report?.Invoke(new Inhabitants.WorldEvent(endEvent, world.Tick, told, null, thing.Id) { TargetType = thing.TypeId });
        }
    }

    /// <summary>A1576: permille of the plain life a type's quality tier gives (data quality), 1000 without one.</summary>
    private long LifePermille(string typeId) =>
        QualityOf?.Invoke(typeId) is { } tier && _data.Quality?.FirstOrDefault(q => string.Equals(q.Tier, tier, StringComparison.Ordinal)) is { } life
            ? life.LifePermille
            : 1000;

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
