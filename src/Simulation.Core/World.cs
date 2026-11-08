using GlassWorld.Simulation.Random;
using GlassWorld.Simulation.State;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Simulation;

/// <summary>Kind of world-time boundary reached by a tick.</summary>
public enum TimeBoundary
{
    /// <summary>A new game hour started.</summary>
    Hour,

    /// <summary>A new game day started (autosave and night work hook on).</summary>
    Day,

    /// <summary>A new season started.</summary>
    Season,

    /// <summary>A new world year started.</summary>
    Year,
}

/// <summary>A system that advances part of the world by one tick.</summary>
public interface IWorldSystem
{
    /// <summary>Stable name; also the prefix of the system's random streams.</summary>
    string Name { get; }

    /// <summary>Advances the system by exactly one tick.</summary>
    void Advance(World world);

    /// <summary>Writes the system's state into the hash in a canonical order.</summary>
    void WriteState(StateHasher hasher);
}

/// <summary>
/// A system whose state may be empty. While <see cref="HasState"/> is false the world hash leaves the
/// system out, so adding a system does not change the hash of worlds that never use it (golden hashes).
/// </summary>
public interface IOptionalWorldState
{
    /// <summary>True once the system holds any state.</summary>
    bool HasState { get; }
}

/// <summary>
/// The simulation world: fixed tick, world time, seeded random streams and a state hash. Systems run in
/// the order they were registered (an explicit list, no reflection). No Godot, no wall clock (ADR-0002).
/// </summary>
public sealed class World
{
    private readonly List<IWorldSystem> _systems = [];

    /// <summary>Creates a new world at tick 0.</summary>
    public World(ulong seed, WorldCalendar calendar)
        : this(seed, calendar, 0, new RandomStreams(seed))
    {
    }

    private World(ulong seed, WorldCalendar calendar, long tick, RandomStreams streams)
    {
        Seed = seed;
        Calendar = calendar;
        Tick = tick;
        Random = streams;
    }

    /// <summary>World seed.</summary>
    public ulong Seed { get; }

    /// <summary>Ticks since the founding of the world.</summary>
    public long Tick { get; private set; }

    /// <summary>World calendar.</summary>
    public WorldCalendar Calendar { get; }

    /// <summary>Current world time.</summary>
    public WorldTime Now => Calendar.At(Tick);

    /// <summary>Named random streams.</summary>
    public RandomStreams Random { get; }

    /// <summary>Raised after a tick when it crossed world-time boundaries (in order hour, day, season, year).</summary>
    public event Action<World, TimeBoundary>? BoundaryReached;

    /// <summary>Registers a system; systems run in registration order.</summary>
    public World Add(IWorldSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (_systems.Any(s => s.Name == system.Name))
        {
            throw new InvalidOperationException($"System '{system.Name}' is already registered.");
        }

        _systems.Add(system);
        return this;
    }

    /// <summary>Restores a world at a tick with given stream states (save games).</summary>
    public static World Restore(ulong seed, WorldCalendar calendar, long tick, IEnumerable<KeyValuePair<string, PcgState>> streams)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        return new World(seed, calendar, tick, RandomStreams.Restore(seed, streams));
    }

    /// <summary>Advances the world by exactly one tick.</summary>
    public void Step()
    {
        foreach (var system in _systems)
        {
            system.Advance(this);
        }

        var before = Now;
        Tick++;
        var after = Now;
        if (after.Hour != before.Hour || after.Day != before.Day)
        {
            BoundaryReached?.Invoke(this, TimeBoundary.Hour);
        }

        if (after.Day != before.Day)
        {
            BoundaryReached?.Invoke(this, TimeBoundary.Day);
        }

        if (after.SeasonIndex != before.SeasonIndex || after.Year != before.Year)
        {
            BoundaryReached?.Invoke(this, TimeBoundary.Season);
        }

        if (after.Year != before.Year)
        {
            BoundaryReached?.Invoke(this, TimeBoundary.Year);
        }
    }

    /// <summary>Advances the world by <paramref name="ticks"/> ticks.</summary>
    public void Run(long ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        for (var i = 0L; i < ticks; i++)
        {
            Step();
        }
    }

    /// <summary>SHA-256 over the complete world state (tick, seed, streams, systems).</summary>
    public string StateHash()
    {
        using var hasher = new StateHasher();
        hasher.Section("world").Write(Seed).Write(Tick);
        hasher.Section("random");
        var streams = Random.Snapshot();
        hasher.Write(streams.Count);
        foreach (var (name, state) in streams)
        {
            hasher.Write(name).Write(state.State).Write(state.Increment);
        }

        foreach (var system in _systems)
        {
            if (system is IOptionalWorldState { HasState: false })
            {
                continue;
            }

            hasher.Section("system:" + system.Name);
            system.WriteState(hasher);
        }

        return hasher.Finish();
    }
}
