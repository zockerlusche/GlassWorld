using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inventions;

/// <summary>An open wish to invent something (D471 stage 1), waiting for its job.</summary>
/// <param name="InhabitantId">Who wants it.</param>
/// <param name="Want">Need (vocabulary id) without a solution.</param>
/// <param name="RequestId">Current job (new attempt = new id).</param>
/// <param name="SinceTick">Tick the wish arose.</param>
/// <param name="Materials">Types sent as perceived materials with the current job (the final check uses them).</param>
/// <param name="RetryAtTick">Set after a retryable error: the wish is sent again at this tick.</param>
public sealed record OpenWant(string InhabitantId, string Want, string RequestId, long SinceTick, IReadOnlyList<string> Materials, long? RetryAtTick);

/// <summary>An invention that passed the final check and is now a runtime type of the world.</summary>
/// <param name="TypeId">Runtime type id (deterministic from the request id).</param>
/// <param name="RequestId">Job that produced it.</param>
/// <param name="InhabitantId">Inventor.</param>
/// <param name="Tick">Tick it entered the world.</param>
/// <param name="InventionJson">The approved <c>Invention</c> record as received (contract JSON).</param>
/// <param name="SoundJson">The guard's sound decision, if any (contract JSON).</param>
public sealed record InventionRecord(string TypeId, string RequestId, string InhabitantId, long Tick, string InventionJson, string? SoundJson);

/// <summary>What the UI needs about an invention (name in both languages, category).</summary>
/// <param name="TypeId">Runtime type id.</param>
/// <param name="Name">Generated name, DE and EN.</param>
/// <param name="Category">Object or building.</param>
/// <param name="Purpose">Action it is made for.</param>
/// <param name="InhabitantId">Inventor.</param>
/// <param name="Tick">Tick it entered the world.</param>
/// <param name="SoundFile">Its own sound from the sound model (file name in the service's output folder), or null: then the game takes the base library by the description (D471 D7).</param>
/// <param name="SoundDescription">The rule guard's sound description, or null when it needs no sound.</param>
public sealed record InventionInfo(string TypeId, LocalizedName Name, InventionCategory Category, string Purpose, string InhabitantId, long Tick, string? SoundFile = null, string? SoundDescription = null);

/// <summary>Runtime types of the world (inventions) for UI and later systems.</summary>
public interface IRuntimeCatalog
{
    /// <summary>The invention behind a runtime type, or null for content types.</summary>
    InventionInfo? Invention(string typeId);

    /// <summary>All inventions in type id order.</summary>
    IReadOnlyList<InventionInfo> Inventions { get; }
}

/// <summary>Complete state of the invention system (save games).</summary>
/// <param name="Counter">Request counter.</param>
/// <param name="Below">Since which tick a need of an inhabitant has been below the threshold ("inhabitant|need").</param>
/// <param name="Cooldowns">Tick until which an inhabitant does not wish again.</param>
/// <param name="Wants">Open wishes.</param>
/// <param name="Inventions">Inventions in type id order.</param>
/// <param name="Log">Job log.</param>
/// <param name="Builds">Building tasks (null in save games from before building).</param>
/// <param name="FirstUsed">Runtime types already used once.</param>
/// <param name="Sounds">Sound jobs of inventions and their results.</param>
public sealed record InventionState(
    long Counter,
    IReadOnlyList<KeyValuePair<string, long>> Below,
    IReadOnlyList<KeyValuePair<string, long>> Cooldowns,
    IReadOnlyList<OpenWant> Wants,
    IReadOnlyList<InventionRecord> Inventions,
    IReadOnlyList<DecisionLogEntry> Log,
    IReadOnlyList<BuildTask>? Builds = null,
    IReadOnlyList<string>? FirstUsed = null,
    IReadOnlyList<SoundTask>? Sounds = null);

/// <summary>
/// Inventions in the core (D471 D5, a world system): a lasting lack becomes a wish, the wish becomes an
/// <c>invention</c> job, and the result passes the core's final check before it becomes a runtime type
/// with the true properties it introduces. Without a job port the system is dormant (no AI, no wishes).
/// Without an answer the wish stays open (B2487); there is never a substitute result. Every value comes
/// from data; nothing here names an invention.
/// </summary>
public sealed partial class InventionSystem : IWorldSystem, IOptionalWorldState, IRuntimeCatalog
{
    /// <summary>Event: an invention entered the world (target = runtime type id, action = purpose).</summary>
    public const string Invented = "invented";

    /// <summary>Event: the core's final check refused an invention.</summary>
    public const string Rejected = "invention_rejected";

    /// <summary>Event: an inhabitant wants to invent something (action = the need).</summary>
    public const string Wanted = "invention_wanted";

    /// <summary>Event: an invention job failed without a retry.</summary>
    public const string Failed = "invention_failed";

    /// <summary>Prefix of runtime type ids from inventions.</summary>
    public const string TypePrefix = "inv_";

    private static readonly JsonSerializerOptions RecordJson = new();

    private readonly SortedDictionary<string, long> _below = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _cooldowns = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, OpenWant> _wants = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, InventionRecord> _inventions = new(StringComparer.Ordinal);
    private readonly InventionRules _rules;
    private readonly InhabitantsSystem _inhabitants;
    private readonly WorldObjects _objects;
    private readonly WorldTypeCatalog _types;
    private readonly double _perceptionRadiusM;
    private readonly IJobPort? _port;
    private string _displayLocale;
    private long _counter;

    /// <summary>Creates the system. <paramref name="port"/> null = dormant (no AI connected).</summary>
    public InventionSystem(InventionRules rules, InhabitantsSystem inhabitants, WorldObjects objects, WorldTypeCatalog types, double perceptionRadiusM, IJobPort? port, string displayLocale)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(inhabitants);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(types);
        _rules = rules;
        _inhabitants = inhabitants;
        _objects = objects;
        _types = types;
        _perceptionRadiusM = perceptionRadiusM;
        _port = port;
        _displayLocale = displayLocale;
        inhabitants.Reported += Observe;
    }

    /// <inheritdoc/>
    public string Name => "invention";

    /// <summary>
    /// Whether a request is validated against its schema before it leaves (ADR-0009; set by the host from
    /// <see cref="SimulationHostOptions.ValidateRequests"/>): on in tests and check runs, off in the running game.
    /// </summary>
    public bool ValidateRequests { get; init; } = true;

    /// <summary>Language of the texts the jobs write (display_locale; set with the player's language).</summary>
    public string DisplayLocale
    {
        get => _displayLocale;
        internal set => _displayLocale = value;
    }

    /// <inheritdoc/>
    public bool HasState => _counter > 0 || _below.Count > 0 || _cooldowns.Count > 0 || _wants.Count > 0 || _inventions.Count > 0 || _builds.Count > 0 || _firstUsed.Count > 0 || _sounds.Count > 0;

    /// <summary>Job log (requests and the outcomes that took effect).</summary>
    public DecisionLog Log { get; } = new();

    /// <summary>Open wishes by inhabitant.</summary>
    public IEnumerable<OpenWant> Wants => _wants.Values;

    /// <summary>Inventions in type id order.</summary>
    public IEnumerable<InventionRecord> Records => _inventions.Values;

    /// <inheritdoc/>
    public IReadOnlyList<InventionInfo> Inventions => [.. _inventions.Values.Select(InfoOf)];

    /// <inheritdoc/>
    public InventionInfo? Invention(string typeId) => _inventions.TryGetValue(typeId, out var record) ? InfoOf(record) : null;

    /// <summary>
    /// The name of an invention as far as one inhabitant knows it: its inventor does, and so does whoever
    /// believes anything about the thing - built it, tried it, looked at it, was told about it, guessed
    /// (any belief about the type, whatever its source; review GW2 #245: a builder holds an experienced
    /// belief, and a remembered event must not read "inv_..."). Null for whoever knows nothing of it and
    /// for content types. Used for the labels of remembered events.
    /// </summary>
    public LocalizedName? NameKnownTo(Inhabitant inhabitant, string typeId)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        ArgumentNullException.ThrowIfNull(typeId);
        return _inventions.TryGetValue(typeId, out var record)
            && (record.InhabitantId == inhabitant.Id || inhabitant.Beliefs.Knows(typeId))
            ? Parse(record).Name
            : null;
    }

    /// <summary>New skills an approved invention brings (B4b R11); empty for other types.</summary>
    public IReadOnlyList<NewNeed> NeedsOf(string typeId) =>
        _inventions.TryGetValue(typeId, out var record) ? [.. Parse(record).IntroducesNeeds ?? []] : [];

    /// <summary>The new abilities an approved invention brings (none for an unknown type).</summary>
    public IReadOnlyList<NewAbility> AbilitiesOf(string typeId) =>
        _inventions.TryGetValue(typeId, out var record) ? [.. Parse(record).IntroducesAbilities ?? []] : [];

    /// <summary>Runtime type id for an approved invention of a job (deterministic).</summary>
    public static string TypeIdFor(string requestId)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(requestId));
        return TypePrefix + Convert.ToHexStringLower(hash.AsSpan(0, 6));
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_port is null)
        {
            return;
        }

        foreach (var message in _port.Receive(world.Tick))
        {
            Apply(world, message);
        }

        foreach (var due in _wants.Values.Where(w => w.RetryAtTick is { } at && at <= world.Tick).ToList())
        {
            var materials = Materials(_inhabitants.Get(due.InhabitantId));
            if (materials.Count == 0)
            {
                Close(world, due, Failed);
                continue;
            }

            _wants[due.InhabitantId] = Submit(world, _inhabitants.Get(due.InhabitantId), due.Want, materials) with { SinceTick = due.SinceTick };
        }

        Trigger(world);
        AdvanceBuilds(world);
        SignalPhase(world);
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_counter).Write(_below.Count);
        foreach (var (key, tick) in _below)
        {
            hasher.Write(key).Write(tick);
        }

        hasher.Write(_cooldowns.Count);
        foreach (var (key, tick) in _cooldowns)
        {
            hasher.Write(key).Write(tick);
        }

        hasher.Write(_wants.Count);
        foreach (var w in _wants.Values)
        {
            hasher.Write(w.InhabitantId).Write(w.Want).Write(w.RequestId).Write(w.SinceTick).Write(w.RetryAtTick ?? -1).Write(w.Materials.Count);
            foreach (var m in w.Materials)
            {
                hasher.Write(m);
            }
        }

        hasher.Write(_inventions.Count);
        foreach (var r in _inventions.Values)
        {
            hasher.Write(r.TypeId).Write(r.RequestId).Write(r.InhabitantId).Write(r.Tick).Write(r.InventionJson).Write(r.SoundJson ?? string.Empty);
            foreach (var p in _types.TrueProperties(r.TypeId).Order(StringComparer.Ordinal))
            {
                hasher.Write(p);
            }
        }

        WriteBuildState(hasher);
    }

    /// <summary>Takes the complete state.</summary>
    public InventionState Capture() =>
        new(_counter, [.. _below], [.. _cooldowns], [.. _wants.Values], [.. _inventions.Values], [.. Log.Entries], [.. _builds.Values], [.. _firstUsed], [.. _sounds.Values]);

    /// <summary>Restores a captured state; registers the runtime types of the inventions.</summary>
    public void Restore(InventionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _counter = state.Counter;
        foreach (var (key, tick) in state.Below)
        {
            _below[key] = tick;
        }

        foreach (var (key, tick) in state.Cooldowns)
        {
            _cooldowns[key] = tick;
        }

        foreach (var want in state.Wants)
        {
            _wants[want.InhabitantId] = want;
        }

        foreach (var record in state.Inventions)
        {
            _types.Register(record.TypeId, Parse(record).Introduces.Concat(_rules.Settings.ImpliedProperties).Distinct(StringComparer.Ordinal));
            _inventions[record.TypeId] = record;
        }

        foreach (var entry in state.Log)
        {
            Log.Add(entry);
        }

        RestoreBuilds(state);
    }

    private static Invention Parse(InventionRecord record) =>
        JsonSerializer.Deserialize<Invention>(record.InventionJson, RecordJson)
        ?? throw new InvalidDataException($"Invention '{record.TypeId}' has no record.");

    private InventionInfo InfoOf(InventionRecord record)
    {
        var invention = Parse(record);
        var decision = SoundOf(record);
        string? file = _sounds.TryGetValue(record.TypeId, out var sound) && sound.ResultJson is not null
            ? JsonSerializer.Deserialize<SoundResult>(sound.ResultJson, RecordJson)?.File
            : null;
        return new InventionInfo(record.TypeId, invention.Name, invention.Category, invention.Purpose, record.InhabitantId, record.Tick, file, decision?.Needed == true ? decision.Description : null);
    }

    private static int SeedOf(string requestId) => DecisionIds.SeedOf(requestId);

    private static bool IsRetryable(string json)
    {
        try
        {
            return ContractSerializer.Deserialize<ServiceError>(json).Retryable;
        }
        catch (ContractViolationException)
        {
            return false;
        }
    }

    /// <summary>A lasting lack without an open wish becomes a wish (data: threshold, duration, cooldown, limit).</summary>
    private void Trigger(World world)
    {
        var trigger = _rules.Settings.Trigger;
        var ticksPerHour = world.Calendar.Settings.TicksPerHour;
        foreach (var inhabitant in _inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            (string Need, double Value)? lack = null;
            foreach (var (need, value) in inhabitant.Needs)
            {
                var key = inhabitant.Id + "|" + need;
                if (value >= trigger.NeedBelow)
                {
                    _below.Remove(key);
                    continue;
                }

                var since = _below.TryAdd(key, world.Tick) ? world.Tick : _below[key];
                if (world.Tick - since >= (long)trigger.HoursBelow * ticksPerHour && (lack is null || value < lack.Value.Value))
                {
                    lack = (need, value);
                }
            }

            if (_cooldowns.TryGetValue(inhabitant.Id, out var until))
            {
                if (world.Tick < until)
                {
                    continue;
                }

                _cooldowns.Remove(inhabitant.Id);
            }

            if (lack is null || _wants.ContainsKey(inhabitant.Id) || _wants.Count >= trigger.MaxOpen)
            {
                continue;
            }

            var materials = Materials(inhabitant);
            if (materials.Count == 0)
            {
                continue;
            }

            _wants[inhabitant.Id] = Submit(world, inhabitant, lack.Value.Need, materials);
            _inhabitants.Report(new WorldEvent(Wanted, world.Tick, inhabitant.Id, lack.Value.Need, null));
        }
    }

    /// <summary>Perceived things as the inhabitant sees them: one per type, nearest first, with believed-true properties only.</summary>
    private List<ObservedThing> Materials(Inhabitant inhabitant)
    {
        var radius = _inhabitants.Sight.Metres(_perceptionRadiusM);
        var radiusSquared = radius * radius;
        return [.. _objects.All
            .Select(o => (o, d: Square(o.Position.X - inhabitant.Position.X) + Square(o.Position.Z - inhabitant.Position.Z)))
            .Where(x => x.d <= radiusSquared)
            .OrderBy(x => x.d)
            .ThenBy(x => x.o.Id, StringComparer.Ordinal)
            .Select(x => x.o.TypeId)
            .Distinct(StringComparer.Ordinal)
            .Take(_rules.Settings.MaxMaterials)
            .Select(type => new ObservedThing
            {
                Type = type,
                Observed = [.. inhabitant.Beliefs.About(type).Where(b => b.Belief.Value).Select(b => b.PropertyId).Order(StringComparer.Ordinal).Take(12)],

                // An earlier invention among the materials goes by the name the inhabitant knows it by.
                Label = NameKnownTo(inhabitant, type),
            })];
    }

    private static double Square(double value) => value * value;

    private OpenWant Submit(World world, Inhabitant inhabitant, string want, List<ObservedThing> materials)
    {
        _counter++;
        var requestId = DecisionIds.RequestId(world.Seed, world.Tick, inhabitant.Id + "/invention", _counter);
        var settings = _rules.Settings;
        var job = new GenerationJob
        {
            ContractVersion = ContractInfo.Version,
            RequestId = requestId,
            Tick = world.Tick,
            Seed = SeedOf(requestId),
            DisplayLocale = Enum.Parse<Locale>(_displayLocale, ignoreCase: true),
            Kind = JobKind.Invention,
            Priority = settings.JobPriority,
            Inhabitant = inhabitant.Id,
            Object = null,
            Payload = new JobPayload
            {
                Invention = new InventionPayload
                {
                    Trigger = InventionPayloadTrigger.Need,
                    Want = want,
                    Subject = null,
                    Inhabitant = InhabitantStates.Of(inhabitant, null, [], [], _inhabitants.LabelOf),
                    Materials = materials,
                    TechLevel = settings.TechLevel,
                    Limits = new InventionLimits { MaxSizeM = settings.Limits.MaxSizeM, MaxParts = settings.Limits.MaxParts, MaxRounds = settings.Limits.MaxRounds },
                },
            },
        };
        var json = ContractSerializer.Serialize(job, ValidateRequests);
        Log.Add(new DecisionLogEntry(DecisionLogKind.JobRequest, requestId, world.Tick, json, null));
        _port!.Submit(requestId, json);
        return new OpenWant(inhabitant.Id, want, requestId, world.Tick, [.. materials.Select(m => m.Type)], null);
    }

    private void Apply(World world, PortMessage message)
    {
        if (ApplySound(world, message) || ApplyBuild(world, message))
        {
            return;
        }

        var want = _wants.Values.FirstOrDefault(w => w.RequestId == message.RequestId && w.RetryAtTick is null);
        if (message.IsError)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobError, message.RequestId, world.Tick, message.Json, ReplayNotes.OfError(message.Json)));
            Retry(world, want, IsRetryable(message.Json));
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
            Close(world, want, Failed);
            return;
        }

        if (want is null || status.JobId != want.RequestId || status.Kind != JobKind.Invention)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobDiscarded, message.RequestId, world.Tick, message.Json, ReplayNotes.QuestionClosed));
            return;
        }

        switch (status.State)
        {
            case JobState.Failed:
                Log.Add(new DecisionLogEntry(DecisionLogKind.JobError, message.RequestId, world.Tick, message.Json, status.Error?.Code.ToString()));
                Retry(world, want, status.Error?.Retryable == true);
                return;
            case JobState.Done when status.Result?.Invention is { } result:
                Decide(world, want, result, message.Json);
                return;
            default:
                // Not a final state, or a result of another kind: nothing takes effect.
                Log.Add(new DecisionLogEntry(DecisionLogKind.JobDiscarded, message.RequestId, world.Tick, message.Json, "not_final"));
                return;
        }
    }

    private void Decide(World world, OpenWant want, InventionResult result, string json)
    {
        var reasons = InventionCheck.Check(result, want.Materials.ToHashSet(StringComparer.Ordinal), _rules).ToList();
        var typeId = TypeIdFor(want.RequestId);
        if (reasons.Count == 0 && _types.Contains(typeId))
        {
            reasons.Add("type_id_taken");
        }

        Log.Add(new DecisionLogEntry(DecisionLogKind.JobResult, want.RequestId, world.Tick, json, reasons.Count == 0 ? null : "rejected:" + string.Join(',', reasons)));
        // The inventor's thought is text of the AI that the player sees: it passes the same content guard
        // as every other visible AI text (youth protection, B2466; the invention's names pass it in
        // InventionCheck). A thought the guard does not allow is not shown - nothing replaces it.
        if (result.Thought is { } thought && _rules.Guard.Check(thought.Text).Allowed)
        {
            _inhabitants.Report(new WorldEvent("thought", world.Tick, want.InhabitantId, null, null) { Text = thought.Text });
        }

        if (reasons.Count > 0)
        {
            Close(world, want, Rejected);
            return;
        }

        var invention = result.Invention!;
        _types.Register(typeId, invention.Introduces.Concat(_rules.Settings.ImpliedProperties).Distinct(StringComparer.Ordinal));
        _inventions[typeId] = new InventionRecord(
            typeId,
            want.RequestId,
            want.InhabitantId,
            world.Tick,
            JsonSerializer.Serialize(invention, RecordJson),
            result.Sound is null ? null : JsonSerializer.Serialize(result.Sound, RecordJson));
        Close(world, want, null);
        _inhabitants.Report(new WorldEvent(Invented, world.Tick, want.InhabitantId, invention.Purpose, typeId));
        StartBuild(world, typeId, want.InhabitantId);
    }

    /// <summary>B2487: a retryable failure keeps the wish and sends it again later with a new id; otherwise it closes.</summary>
    private void Retry(World world, OpenWant? want, bool retryable)
    {
        if (want is null)
        {
            return;
        }

        if (!retryable)
        {
            Close(world, want, Failed);
            return;
        }

        var after = (long)_rules.Settings.RetryAfterMinutes * world.Calendar.Settings.TicksPerHour / 60;
        _wants[want.InhabitantId] = want with { RetryAtTick = world.Tick + Math.Max(1, after) };
    }

    private void Close(World world, OpenWant? want, string? eventKind)
    {
        if (want is null)
        {
            return;
        }

        _wants.Remove(want.InhabitantId);
        _cooldowns[want.InhabitantId] = world.Tick + ((long)_rules.Settings.Trigger.CooldownHours * world.Calendar.Settings.TicksPerHour);
        if (eventKind is not null)
        {
            _inhabitants.Report(new WorldEvent(eventKind, world.Tick, want.InhabitantId, want.Want, null));
        }
    }
}
