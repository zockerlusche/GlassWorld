using System.Text.Json;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Night;

/// <summary>A job the night queue holds: built by day, sent at night.</summary>
/// <param name="RequestId">Job id.</param>
/// <param name="Kind">Job kind (generation-job kind).</param>
/// <param name="Priority">The job's own priority (data of its kind).</param>
/// <param name="InhabitantId">The inhabitant it is about, or null.</param>
/// <param name="Tick">Tick it was built.</param>
/// <param name="Nights">Compute nights it already waited (each adds the data's bonus).</param>
/// <param name="Json">The job as the system built it.</param>
public sealed record HeldJob(string RequestId, string Kind, int Priority, string? InhabitantId, long Tick, int Nights, string Json);

/// <summary>Complete state (save games): the held jobs in the order they came.</summary>
/// <param name="Held">The held jobs.</param>
public sealed record NightQueueState(IReadOnlyList<HeldJob> Held);

/// <summary>
/// A1766 (A1568 step 2b, B2789, D1113 v3 4b): the night queue of the day mode "night_only". By day the jobs of the
/// data's kinds are not sent: the system that built a job keeps it open as always, the queue holds it (at the job
/// router, so no system changes) and sends it when the compute night begins - most important first (the job's own
/// priority plus a bonus per night it waited), then each inhabitant in turn and the oldest first, at most the data's
/// number per night; the rest waits for the next night and moves up, never dropped. Switching back to "ai" sends
/// what waits at once. The day mode is the player's logged command (<see cref="DayModeCommand"/>), so a replay holds
/// and sends at the same ticks. Stays out of the hash while it holds nothing.
/// </summary>
public sealed class NightQueueSystem : IWorldSystem, IOptionalWorldState
{
    /// <summary>The player's command that sets the day mode (text <see cref="DayModeAi"/> or <see cref="DayModeNightOnly"/>).</summary>
    public const string DayModeCommand = "set_day_mode";

    /// <summary>The AI thinks along by day as well (the default).</summary>
    public const string DayModeAi = "ai";

    /// <summary>By day without AI: the jobs of the held kinds wait for the night.</summary>
    public const string DayModeNightOnly = "night_only";

    private readonly NightQueue? _settings;
    private readonly JobRouter? _router;
    private readonly World _world;
    private readonly Func<World, bool> _isNight;
    private readonly Func<World, bool> _nightBegins;
    private readonly HashSet<string> _heldKinds;
    private readonly List<HeldJob> _held = [];

    /// <summary>Creates the queue. No settings or no job router = it never holds anything.</summary>
    /// <param name="settings">The night queue data (content/jobs/phases.json, night_queue).</param>
    /// <param name="router">The world's job router, whose submissions it may hold.</param>
    /// <param name="world">The world (its tick and calendar when a job comes in).</param>
    /// <param name="isNight">Whether the world is in the compute night now.</param>
    /// <param name="nightBegins">Whether the compute night begins with this tick.</param>
    public NightQueueSystem(NightQueue? settings, JobRouter? router, World world, Func<World, bool> isNight, Func<World, bool> nightBegins)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(isNight);
        ArgumentNullException.ThrowIfNull(nightBegins);
        _settings = settings;
        _router = settings is null ? null : router;
        _world = world;
        _isNight = isNight;
        _nightBegins = nightBegins;
        _heldKinds = settings?.HeldKinds.ToHashSet(StringComparer.Ordinal) ?? [];
        if (_router is not null)
        {
            _router.Holds = Hold;
        }
    }

    /// <inheritdoc/>
    public string Name => "night_queue";

    /// <inheritdoc/>
    public bool HasState => _held.Count > 0;

    /// <summary>Whether the day mode is "night_only" (the last logged <see cref="DayModeCommand"/>; off without one).</summary>
    public bool NightOnly { get; internal set; }

    /// <summary>Whether jobs are held right now: night_only, the data present, and no compute night.</summary>
    public bool HoldsNow => NightOnly && _router is not null && !_isNight(_world);

    /// <summary>The held jobs in the order they came.</summary>
    public IReadOnlyList<HeldJob> Held => _held;

    /// <summary>How many jobs about an inhabitant wait for the night (the game's "open for the night: N").</summary>
    public int HeldFor(string inhabitantId) => _held.Count(h => string.Equals(h.InhabitantId, inhabitantId, StringComparison.Ordinal));

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_held.Count == 0 || _router is null || _settings is null)
        {
            return;
        }

        if (!NightOnly)
        {
            // Back to "ai": what waits goes out now, in the night's order.
            Release(_held.Count);
            return;
        }

        if (_nightBegins(world))
        {
            Release(_settings.CapPerNight);
            for (var i = 0; i < _held.Count; i++)
            {
                _held[i] = _held[i] with { Nights = _held[i].Nights + 1 };
            }
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_held.Count);
        foreach (var h in _held)
        {
            hasher.Write(h.RequestId).Write(h.Kind).Write(h.Priority).Write(h.InhabitantId ?? string.Empty).Write(h.Tick).Write(h.Nights).Write(h.Json);
        }
    }

    /// <summary>Takes the complete state.</summary>
    public NightQueueState Capture() => new([.. _held]);

    /// <summary>Restores a captured state (null = a save game from before: nothing waits).</summary>
    public void Restore(NightQueueState? state)
    {
        _held.Clear();
        _held.AddRange(state?.Held ?? []);
    }

    /// <summary>Whether a job a system submits now is held (and keeps it); false = it goes out now.</summary>
    private bool Hold(string requestId, string jobJson)
    {
        if (!HoldsNow)
        {
            return false;
        }

        using var doc = JsonDocument.Parse(jobJson);
        var root = doc.RootElement;
        var kind = root.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : string.Empty;
        if (!_heldKinds.Contains(kind))
        {
            return false;
        }

        var priority = root.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
        var tick = root.TryGetProperty("tick", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : _world.Tick;
        string? inhabitant = root.TryGetProperty("inhabitant", out var i) && i.ValueKind == JsonValueKind.Object && i.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
        _held.Add(new HeldJob(requestId, kind, priority, inhabitant, tick, 0, jobJson));
        return true;
    }

    /// <summary>
    /// Sends up to <paramref name="count"/> held jobs: each time the most important (own priority plus the bonus per
    /// night waited), then the inhabitant that got fewest so far, then the oldest, then the id - so everybody gets
    /// something and the order is the same in every run.
    /// </summary>
    private void Release(int count)
    {
        var bonus = _settings!.AgeBonusPerNight;
        var sent = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var n = 0; n < count && _held.Count > 0; n++)
        {
            var next = _held
                .OrderByDescending(h => h.Priority + ((long)h.Nights * bonus))
                .ThenBy(h => sent.GetValueOrDefault(h.InhabitantId ?? string.Empty))
                .ThenBy(h => h.Tick)
                .ThenBy(h => h.RequestId, StringComparer.Ordinal)
                .First();
            _held.Remove(next);
            sent[next.InhabitantId ?? string.Empty] = sent.GetValueOrDefault(next.InhabitantId ?? string.Empty) + 1;
            _router!.SendHeld(next.RequestId, next.Json);
        }
    }
}
