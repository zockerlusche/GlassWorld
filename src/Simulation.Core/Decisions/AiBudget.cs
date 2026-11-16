using GlassWorld.Contracts;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Simulation.Decisions;

/// <summary>Complete state of the AI budget (save games): the ticks of the day requests of the last game hour, oldest first.</summary>
/// <param name="Sent">Ticks at which a counted request went out.</param>
public sealed record AiBudgetState(IReadOnlyList<long> Sent);

/// <summary>
/// A1587 step 2 (B7 R24, R26, R28; B2804, D1161): how many day requests may go to the AI in a game hour. The quota
/// follows from the data (<see cref="ThroughputSettings"/>), the game's speed and the measured time of one AI job
/// (<see cref="AiPace"/>, logged commands): floor(day_share × real seconds of a game hour at the speed / seconds per
/// job), at least min_per_hour. The decider and the talks between inhabitants count their requests here; the player's
/// own (talking to an inhabitant, being noticed) never wait for it. Without the data block nothing is limited.
/// Stays out of the hash while it holds nothing.
/// </summary>
public sealed class AiBudget : IWorldSystem, IOptionalWorldState
{
    private readonly ThroughputSettings? _settings;
    private readonly AiPace _pace;
    private readonly WorldCalendar _calendar;
    private readonly Queue<long> _sent = new();

    /// <summary>Creates the budget; <paramref name="settings"/> null = no limit.</summary>
    public AiBudget(ThroughputSettings? settings, AiPace pace, WorldCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(pace);
        ArgumentNullException.ThrowIfNull(calendar);
        _settings = settings;
        _pace = pace;
        _calendar = calendar;
    }

    /// <inheritdoc/>
    public string Name => "ai_budget";

    /// <inheritdoc/>
    public bool HasState => _sent.Count > 0;

    /// <summary>Whether the data limit the day requests at all.</summary>
    public bool Active => _settings is not null;

    /// <summary>Milliseconds one AI job takes: the measured value, else the data's fallback.</summary>
    public int JobMs => _pace.JobMs ?? _settings?.FallbackJobMs ?? 0;

    /// <summary>Real seconds one game hour takes at the current speed.</summary>
    public double RealSecondsPerHour => _calendar.RealSecondsPerTickAtNormalSpeed * _calendar.Settings.TicksPerHour / Math.Max(1, _pace.Speed);

    /// <summary>Requests per game hour at the current speed and job time (int.MaxValue without the data block).</summary>
    public int PerHour => _settings is not { } s
        ? int.MaxValue
        : Math.Max(s.MinPerHour, (int)Math.Floor(s.DayShare * RealSecondsPerHour * 1000.0 / Math.Max(1, JobMs)));

    /// <summary>Requests counted within the game hour up to <paramref name="tick"/>.</summary>
    public int Used(long tick) => _sent.Count(t => t > tick - _calendar.Settings.TicksPerHour);

    /// <summary>Whether one more counted request may go out at <paramref name="tick"/>.</summary>
    public bool HasRoom(long tick) => !Active || Used(tick) < PerHour;

    /// <summary>Rank 1 (R23): what the player raises - always goes, never counted.</summary>
    public const int RankPlayer = 1;

    /// <summary>Rank 2: the inhabitant the camera follows and those near it (B2804).</summary>
    public const int RankFollowed = 2;

    /// <summary>Rank 3: what is new or pressing (data: first_kinds).</summary>
    public const int RankFirst = 3;

    /// <summary>Rank 4: everything else.</summary>
    public const int RankOther = 4;

    /// <summary>
    /// A1587 S3 (R26): the highest rank that still goes to the AI at the current speed - the data entry with the highest
    /// speed not above the game's; every rank without the data.
    /// </summary>
    public int UpToRank =>
        _settings?.LevelsBySpeed is { Count: > 0 } levels
            ? levels.Where(l => l.Speed <= _pace.Speed).OrderByDescending(l => l.Speed).Select(l => l.UpToLevel).DefaultIfEmpty(RankOther).First()
            : RankOther;

    /// <summary>Whether a request of this rank may go to the AI at the current speed (rank 1 always).</summary>
    public bool Allows(int rank) => !Active || rank <= RankPlayer || rank <= UpToRank;

    /// <summary>
    /// A1587 S3 (R23): the rank of a day request of an inhabitant - 1 the player's, 2 the followed inhabitant or one
    /// near it, 3 a kind the data call new or pressing, 4 the rest. <paramref name="positionOf"/> gives where a living
    /// inhabitant is (null = not in the world): a followed one who died or left ranks nobody (review GW1 #487).
    /// </summary>
    public int RankOf(string inhabitantId, string kind, Func<string, Position?> positionOf)
    {
        ArgumentNullException.ThrowIfNull(positionOf);
        if (kind == DecisionsSystem.NoticedPlayer)
        {
            return RankPlayer;
        }

        if (IsNearFollowed(inhabitantId, positionOf))
        {
            return RankFollowed;
        }

        return _settings?.FirstKinds?.Contains(kind) == true ? RankFirst : RankOther;
    }

    /// <summary>Whether an inhabitant is the followed one or within the data's distance of it.</summary>
    public bool IsNearFollowed(string inhabitantId, Func<string, Position?> positionOf)
    {
        ArgumentNullException.ThrowIfNull(positionOf);
        if (_pace.FollowedId is not { } followed || positionOf(followed) is not { } at)
        {
            return false;
        }

        if (string.Equals(inhabitantId, followed, StringComparison.Ordinal))
        {
            return true;
        }

        var near = _settings?.NearFollowedM ?? 0;
        if (near <= 0 || positionOf(inhabitantId) is not { } here)
        {
            return false;
        }

        var dx = here.X - at.X;
        var dz = here.Z - at.Z;
        return (dx * dx) + (dz * dz) <= near * near;
    }

    /// <summary>Counts a request that went out at <paramref name="tick"/> (nothing without the data block).</summary>
    public void Note(long tick)
    {
        if (Active)
        {
            _sent.Enqueue(tick);
        }
    }

    /// <summary>
    /// R28: the service's time budget for a decision - the real time of the data's answer_within_minutes at the current
    /// speed, at most <paramref name="deadlineMs"/> (and at least the contract's 100 ms); without the data block
    /// <paramref name="deadlineMs"/> as before.
    /// </summary>
    public int DeadlineMs(int deadlineMs)
    {
        if (_settings is not { } s)
        {
            return deadlineMs;
        }

        var ms = s.AnswerWithinMinutes * RealSecondsPerHour / 60.0 * 1000.0;
        return (int)Math.Clamp(Math.Floor(ms), 100, deadlineMs);
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var hour = _calendar.Settings.TicksPerHour;
        while (_sent.Count > 0 && _sent.Peek() <= world.Tick - hour)
        {
            _sent.Dequeue();
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_sent.Count);
        foreach (var tick in _sent)
        {
            hasher.Write(tick);
        }
    }

    /// <summary>Takes the complete state.</summary>
    public AiBudgetState Capture() => new([.. _sent]);

    /// <summary>Restores a captured state (null = a save game from before: nothing counted).</summary>
    public void Restore(AiBudgetState? state)
    {
        _sent.Clear();
        foreach (var tick in state?.Sent ?? [])
        {
            _sent.Enqueue(tick);
        }
    }
}
