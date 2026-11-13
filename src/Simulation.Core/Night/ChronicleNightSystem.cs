using System.Collections.Frozen;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Safety;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Night;

/// <summary>The narrative of one game day (B8 R10): it stands above the base lines <paramref name="FirstSeq"/> … <paramref name="LastSeq"/>.</summary>
/// <param name="Day">The game day it tells (1 = the first).</param>
/// <param name="Tick">Tick the answer took effect.</param>
/// <param name="FirstSeq">First chronicle entry it was written from.</param>
/// <param name="LastSeq">Last chronicle entry it was written from.</param>
/// <param name="Locale">Language it is written in (de/en).</param>
/// <param name="Text">The narrative (content guard passed in the service and here).</param>
public sealed record DayNarrative(long Day, long Tick, long FirstSeq, long LastSeq, string Locale, string Text);

/// <summary>A chronicle job that is out: which day and which entries.</summary>
/// <param name="RequestId">Job id.</param>
/// <param name="Day">The day it tells.</param>
/// <param name="FirstSeq">First entry sent.</param>
/// <param name="LastSeq">Last entry sent.</param>
/// <param name="MaxChars">Length limit sent.</param>
/// <param name="Locale">Language asked for (de/en).</param>
public sealed record ChronicleJob(string RequestId, long Day, long FirstSeq, long LastSeq, int MaxChars, string Locale);

/// <summary>Complete state (save games): request counter, entries sent through, open jobs, narratives.</summary>
/// <param name="Counter">Request counter.</param>
/// <param name="SentThrough">Last chronicle entry that went out (or was passed over) at a nightfall.</param>
/// <param name="Jobs">Open jobs.</param>
/// <param name="Narratives">The day narratives, oldest first.</param>
public sealed record ChronicleNightState(long Counter, long SentThrough, IReadOnlyList<ChronicleJob> Jobs, IReadOnlyList<DayNarrative> Narratives);

/// <summary>
/// B8 R10 (A1355): the chronicle's day as a short narrative (family book), written in the computing
/// night. At nightfall the chronicle entries since the last nightfall go out as one job of kind
/// <c>chronicle</c> (names as the chronicle shows them, ids beside them); the answer stands above the
/// day's base lines. The AI only words what the entries say: the core checks language, length and the
/// content guard, and anything else - a null narrative, a failed or late job - leaves the base lines
/// alone for good (no stand-in, no second try). Stays out of the hash while it holds nothing.
/// </summary>
public sealed class ChronicleNightSystem : IWorldSystem, IOptionalWorldState
{
    /// <summary>The answer was longer than the limit sent.</summary>
    public const string TooLong = "narrative_too_long";

    /// <summary>The answer is not in the language asked for.</summary>
    public const string WrongLanguage = "narrative_wrong_language";

    /// <summary>The core's content guard refused the answer.</summary>
    public const string Refused = "narrative_refused";

    private readonly ChronicleNarrativeSettings? _settings;
    private readonly FrozenDictionary<string, ChronicleKind> _kinds;
    private readonly ChronicleSystem _chronicle;
    private readonly InhabitantsSystem _inhabitants;
    private readonly Func<World, bool> _nightBegins;
    private readonly TextGuard _guard;
    private readonly IJobPort? _port;
    private readonly SortedDictionary<string, ChronicleJob> _jobs = new(StringComparer.Ordinal);
    private readonly SortedDictionary<long, DayNarrative> _narratives = [];
    private long _counter;
    private long _sentThrough;

    /// <summary>Creates the system. <paramref name="jobs"/> null or no narrative settings = dormant.</summary>
    /// <param name="kinds">The chronicle kinds and the narrative settings (content/chronicle/kinds.json).</param>
    /// <param name="chronicle">The chronicle whose entries are told.</param>
    /// <param name="inhabitants">The inhabitants (their names).</param>
    /// <param name="nightBegins">Whether the computing night begins with this tick (the night jobs' signal).</param>
    /// <param name="guard">The content guard.</param>
    /// <param name="jobs">The world's job port, shared with the other job systems.</param>
    /// <param name="log">The world's job log.</param>
    /// <param name="displayLocale">Language the narrative is written in.</param>
    public ChronicleNightSystem(ChronicleKinds kinds, ChronicleSystem chronicle, InhabitantsSystem inhabitants, Func<World, bool> nightBegins, TextGuard guard, JobRouter? jobs, DecisionLog log, string displayLocale)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(chronicle);
        ArgumentNullException.ThrowIfNull(inhabitants);
        ArgumentNullException.ThrowIfNull(nightBegins);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(log);
        _settings = kinds.Narrative;
        _kinds = kinds.Kinds.ToFrozenDictionary(k => k.Id, StringComparer.Ordinal);
        _chronicle = chronicle;
        _inhabitants = inhabitants;
        _nightBegins = nightBegins;
        _guard = guard;
        _port = _settings is null ? null : jobs?.Claim(_jobs.ContainsKey);
        Log = log;
        DisplayLocale = displayLocale;
    }

    /// <inheritdoc/>
    public string Name => "chronicle_night";

    /// <inheritdoc/>
    public bool HasState => _counter > 0 || _sentThrough > 0 || _jobs.Count > 0 || _narratives.Count > 0;

    /// <summary>The world's job log.</summary>
    public DecisionLog Log { get; }

    /// <summary>Whether jobs are validated against the contract before they go out (tests may turn it off).</summary>
    public bool ValidateRequests { get; init; } = true;

    /// <summary>Language the next narrative is written in (de/en).</summary>
    public string DisplayLocale { get; set; }

    /// <summary>
    /// Name of a chronicle target in the display language (an invention or a thing of one), or null when
    /// the chronicle has no name for it (the line says "something"); set by the host.
    /// </summary>
    public Func<string, string, string?> TargetName { get; init; } = (_, _) => null;

    /// <summary>The narratives, oldest day first.</summary>
    public IEnumerable<DayNarrative> Narratives => _narratives.Values;

    /// <summary>Open jobs, in id order.</summary>
    public IEnumerable<ChronicleJob> Jobs => _jobs.Values;

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_port is null || _settings is null)
        {
            return;
        }

        foreach (var message in _port.Receive(world.Tick))
        {
            Apply(world, message);
        }

        if (_nightBegins(world))
        {
            Submit(world, _settings);
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_counter).Write(_sentThrough).Write(_jobs.Count);
        foreach (var job in _jobs.Values)
        {
            hasher.Write(job.RequestId).Write(job.Day).Write(job.FirstSeq).Write(job.LastSeq).Write(job.MaxChars).Write(job.Locale);
        }

        hasher.Write(_narratives.Count);
        foreach (var n in _narratives.Values)
        {
            hasher.Write(n.Day).Write(n.Tick).Write(n.FirstSeq).Write(n.LastSeq).Write(n.Locale).Write(n.Text);
        }
    }

    /// <summary>Takes the complete state.</summary>
    public ChronicleNightState Capture() => new(_counter, _sentThrough, [.. _jobs.Values], [.. _narratives.Values]);

    /// <summary>Restores a captured state (null = a save game from before: nothing was told yet).</summary>
    public void Restore(ChronicleNightState? state)
    {
        if (state is null)
        {
            return;
        }

        _counter = state.Counter;
        _sentThrough = state.SentThrough;
        _jobs.Clear();
        foreach (var job in state.Jobs)
        {
            _jobs[job.RequestId] = job;
        }

        _narratives.Clear();
        foreach (var n in state.Narratives)
        {
            _narratives[n.Day] = n;
        }
    }

    private void Submit(World world, ChronicleNarrativeSettings settings)
    {
        var fresh = _chronicle.Since(_sentThrough);
        if (fresh.Count == 0)
        {
            return;
        }

        // Every entry goes out once: what is not told tonight (beyond the limit, unnamed) is not told later.
        _sentThrough = fresh[^1].Seq;
        var told = fresh.Skip(Math.Max(0, fresh.Count - settings.MaxEntries)).ToList();
        var lines = told.Select(Line).OfType<ChronicleDayLine>().ToList();
        if (lines.Count == 0)
        {
            return;
        }

        var day = ((world.Tick - 1) / world.Calendar.TicksPerDay) + 1;
        _counter++;
        var requestId = DecisionIds.RequestId(world.Seed, world.Tick, "chronicle/day", _counter);
        var job = new GenerationJob
        {
            ContractVersion = ContractInfo.Version,
            RequestId = requestId,
            Tick = world.Tick,
            Seed = DecisionIds.SeedOf(requestId),
            DisplayLocale = Enum.Parse<Locale>(DisplayLocale, ignoreCase: true),
            Kind = JobKind.Chronicle,
            Priority = settings.JobPriority,
            Inhabitant = null,
            Object = null,
            Payload = new JobPayload
            {
                Chronicle = new ChroniclePayload { Day = (int)Math.Min(day, 1_000_000), Entries = lines, MaxChars = settings.MaxChars },
            },
        };
        var json = ContractSerializer.Serialize(job, ValidateRequests);
        Log.Add(new DecisionLogEntry(DecisionLogKind.JobRequest, requestId, world.Tick, json, null));
        _port!.Submit(requestId, json);
        _jobs[requestId] = new ChronicleJob(requestId, day, told[0].Seq, told[^1].Seq, settings.MaxChars, DisplayLocale.ToLowerInvariant());
    }

    /// <summary>One entry as the chronicle shows it; null when its inhabitant has no name to tell it by.</summary>
    private ChronicleDayLine? Line(ChronicleRecord record)
    {
        if (!_kinds.TryGetValue(record.Kind, out var kind) || NameOf(record.InhabitantId) is not { } who)
        {
            return null;
        }

        var param = kind.Params.Select(p => p.ToString().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        ChronicleDayName? target = null;
        if (record.TargetId is { } id)
        {
            var label = param.Contains("other") ? NameOf(id)?.Label : param.Contains("target") ? TargetName(id, DisplayLocale) : null;
            target = string.IsNullOrWhiteSpace(label) ? null : new ChronicleDayName { Id = id, Label = Trim(label) };
        }

        return new ChronicleDayLine
        {
            Kind = kind.Id,
            TextKey = kind.TextKey,
            Inhabitant = who,
            Target = target,
            Action = param.Contains("action") ? record.Action : null,
        };
    }

    private ChronicleDayName? NameOf(string inhabitantId) =>
        _inhabitants.TryGet(inhabitantId, out var inhabitant) && !string.IsNullOrWhiteSpace(inhabitant.Name)
            ? new ChronicleDayName { Id = inhabitantId, Label = Trim(inhabitant.Name) }
            : null;

    private static string Trim(string label) => label.Length <= 64 ? label : label[..64];

    private void Apply(World world, PortMessage message)
    {
        if (!_jobs.Remove(message.RequestId, out var job))
        {
            return;
        }

        if (message.IsError)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobError, message.RequestId, world.Tick, message.Json, ReplayNotes.OfError(message.Json)));
            return;
        }

        JobStatus status;
        try
        {
            status = ContractSerializer.Deserialize<JobStatus>(message.Json);
        }
        catch (ContractViolationException)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobDiscarded, message.RequestId, world.Tick, message.Json, ReplayNotes.ContractViolation));
            return;
        }

        if (status.State == JobState.Failed && status.JobId == message.RequestId && status.Kind == JobKind.Chronicle)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobError, message.RequestId, world.Tick, message.Json, status.Error?.Code.ToString()));
            return;
        }

        if (!(status.State == JobState.Done && status.JobId == message.RequestId && status.Kind == JobKind.Chronicle && status.Result?.Chronicle is not null))
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobDiscarded, message.RequestId, world.Tick, message.Json, NightJobsSystem.NotFinal));
            return;
        }

        var narrative = status.Result.Chronicle.Narrative;
        string? reason = null;
        if (narrative is not null)
        {
            if (narrative.Text.EnumerateRunes().Count() > job.MaxChars)
            {
                reason = TooLong;
            }
            else if (!string.Equals(narrative.Locale.ToString(), job.Locale, StringComparison.OrdinalIgnoreCase))
            {
                reason = WrongLanguage;
            }
            else if (!_guard.Check(narrative.Text).Allowed)
            {
                reason = Refused;
            }
        }

        Log.Add(new DecisionLogEntry(DecisionLogKind.JobResult, message.RequestId, world.Tick, message.Json, reason is null ? null : NightJobsSystem.Rejected + reason));
        if (narrative is not null && reason is null && !string.IsNullOrWhiteSpace(narrative.Text))
        {
            _narratives[job.Day] = new DayNarrative(job.Day, world.Tick, job.FirstSeq, job.LastSeq, narrative.Locale.ToString().ToLowerInvariant(), narrative.Text.Trim());
        }
    }
}
