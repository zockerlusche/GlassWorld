using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inventions;

/// <summary>Data for building inventions (D471 stages 4, 5, 7): kit, settings, which needs can be neglected, compute night.</summary>
/// <param name="Settings">content/build/settings.json.</param>
/// <param name="Kit">content/build/kit.json.</param>
/// <param name="NeglectableNeeds">Needs with a neglect threshold; the inventor builds only while they are high enough.</param>
/// <param name="NightPhases">Day phases that are the compute night of the AI service (content/jobs/phases.json).</param>
/// <param name="Sound">Lengths, steps and priority of sound jobs (content/sound/generation.json).</param>
public sealed record BuildRules(BuildSettings Settings, BuildKit Kit, FrozenSet<string> NeglectableNeeds, FrozenSet<string> NightPhases, SoundGeneration Sound);

/// <summary>The sound job of a built invention (D471 D7) and its result.</summary>
/// <param name="TypeId">Runtime type the sound belongs to.</param>
/// <param name="RequestId">The sound job.</param>
/// <param name="ResultJson">SoundResult once the sound exists, else null.</param>
/// <param name="Failed">True when the job failed: the invention keeps no sound of its own (base library by description).</param>
public sealed record SoundTask(string TypeId, string RequestId, string? ResultJson, bool Failed);

/// <summary>
/// Building one approved invention: the plan job, the checked plan, the building site and the progress.
/// </summary>
/// <param name="TypeId">Runtime type that is built.</param>
/// <param name="InhabitantId">The inventor, who builds it.</param>
/// <param name="Attempts">Plans asked for so far.</param>
/// <param name="RequestId">Open build_plan job, or null.</param>
/// <param name="RetryAtTick">When to ask for a plan again, or null.</param>
/// <param name="PlanJson">The accepted building plan, or null while there is none.</param>
/// <param name="SiteX">Building site x (m).</param>
/// <param name="SiteZ">Building site z (m).</param>
/// <param name="StepsDone">Finished building steps.</param>
/// <param name="Gathered">Material types worked in for the current step (ordinal order).</param>
/// <param name="PendingObjectId">Material object the inventor is on the way to, or null.</param>
/// <param name="PendingType">Type of that object.</param>
/// <param name="BuiltObjectId">The finished object, or null.</param>
/// <param name="GaveUp">True when no acceptable plan came after the allowed attempts.</param>
public sealed record BuildTask(
    string TypeId,
    string InhabitantId,
    int Attempts,
    string? RequestId,
    long? RetryAtTick,
    string? PlanJson,
    double SiteX,
    double SiteZ,
    int StepsDone,
    IReadOnlyList<string> Gathered,
    string? PendingObjectId,
    string? PendingType,
    string? BuiltObjectId,
    bool GaveUp);

/// <content>Building inventions from a plan and their first use (D471 D5, part 2).</content>
public sealed partial class InventionSystem
{
    /// <summary>A building plan was accepted by the core (target = runtime type).</summary>
    public const string BuildPlanned = "build_planned";

    /// <summary>A building plan was refused by the core (target = runtime type).</summary>
    public const string BuildPlanRejected = "build_plan_rejected";

    /// <summary>A building step is finished (action = step number, target = runtime type).</summary>
    public const string BuildStep = "build_step";

    /// <summary>The invention stands (target = the new object).</summary>
    public const string Built = "built";

    /// <summary>No acceptable plan after the allowed attempts (target = runtime type).</summary>
    public const string BuildFailed = "build_failed";

    /// <summary>An inhabitant used an invention for the first time (target = object).</summary>
    public const string FirstUse = "first_use";

    private readonly SortedDictionary<string, BuildTask> _builds = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _firstUsed = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, SoundTask> _sounds = new(StringComparer.Ordinal);
    private bool? _night;

    /// <summary>Building tasks by runtime type.</summary>
    public IEnumerable<BuildTask> Builds => _builds.Values;

    /// <summary>Runtime types that have been used at least once.</summary>
    public IEnumerable<string> FirstUsed => _firstUsed;

    private BuildRules Build => _rules.Build;

    private static BuildPlan ParsePlan(BuildTask task) =>
        JsonSerializer.Deserialize<BuildPlan>(task.PlanJson!, RecordJson) ?? throw new InvalidDataException($"Building plan of '{task.TypeId}' is empty.");

    /// <summary>Starts building a just approved invention: the first plan job goes out right away.</summary>
    private void StartBuild(World world, string typeId, string inhabitantId)
    {
        var task = new BuildTask(typeId, inhabitantId, 0, null, null, null, 0, 0, 0, [], null, null, null, false);
        _builds[typeId] = SubmitPlan(world, task);
    }

    private BuildTask SubmitPlan(World world, BuildTask task)
    {
        _counter++;
        var requestId = DecisionIds.RequestId(world.Seed, world.Tick, task.InhabitantId + "/build_plan", _counter);
        var kit = Build.Kit;
        var job = new GenerationJob
        {
            ContractVersion = ContractInfo.Version,
            RequestId = requestId,
            Tick = world.Tick,
            Seed = SeedOf(requestId),
            DisplayLocale = Enum.Parse<Locale>(_displayLocale, ignoreCase: true),
            Kind = JobKind.Build_plan,
            Priority = Build.Settings.JobPriority,
            Inhabitant = task.InhabitantId,
            Object = null,
            Payload = new JobPayload
            {
                BuildPlan = new BuildPlanPayload
                {
                    Invention = Parse(_inventions[task.TypeId]),
                    Shapes = [.. kit.Shapes],
                    Colors = [.. kit.Colors.Select(c => c.Id)],
                    MaxParts = Math.Min(kit.MaxParts, _rules.Settings.Limits.MaxParts),
                },
            },
        };
        var json = ContractSerializer.Serialize(job);
        Log.Add(new DecisionLogEntry(DecisionLogKind.JobRequest, requestId, world.Tick, json, null));
        _port!.Submit(requestId, json);
        return task with { Attempts = task.Attempts + 1, RequestId = requestId, RetryAtTick = null };
    }

    /// <summary>Routes a job outcome to a building task; false if it belongs to none.</summary>
    private bool ApplyBuild(World world, PortMessage message)
    {
        var task = _builds.Values.FirstOrDefault(b => b.RequestId == message.RequestId);
        if (task is null)
        {
            return false;
        }

        if (message.IsError)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobError, message.RequestId, world.Tick, message.Json, ReplayNotes.ServiceError));
            RetryPlan(world, task, IsRetryable(message.Json));
            return true;
        }

        JobStatus status;
        try
        {
            status = ContractSerializer.Deserialize<JobStatus>(message.Json);
        }
        catch (ContractViolationException)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobDiscarded, message.RequestId, world.Tick, message.Json, ReplayNotes.ContractViolation));
            RetryPlan(world, task, retryable: true);
            return true;
        }

        if (status.JobId != task.RequestId || status.Kind != JobKind.Build_plan)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobDiscarded, message.RequestId, world.Tick, message.Json, ReplayNotes.QuestionClosed));
            return true;
        }

        switch (status.State)
        {
            case JobState.Failed:
                Log.Add(new DecisionLogEntry(DecisionLogKind.JobError, message.RequestId, world.Tick, message.Json, status.Error?.Code.ToString()));
                RetryPlan(world, task, status.Error?.Retryable == true);
                return true;
            case JobState.Done when status.Result?.BuildPlan is { } plan:
                AcceptOrRefuse(world, task, plan, message.Json);
                return true;
            default:
                Log.Add(new DecisionLogEntry(DecisionLogKind.JobDiscarded, message.RequestId, world.Tick, message.Json, "not_final"));
                return true;
        }
    }

    private void AcceptOrRefuse(World world, BuildTask task, BuildPlan plan, string json)
    {
        var invention = Parse(_inventions[task.TypeId]);
        var reasons = BuildPlanCheck.Check(plan, invention, Build.Kit, Build.Settings, _rules.Settings.Limits.MaxParts);
        Log.Add(new DecisionLogEntry(DecisionLogKind.JobResult, task.RequestId!, world.Tick, json, reasons.Count == 0 ? null : "rejected:" + string.Join(',', reasons)));
        if (reasons.Count > 0)
        {
            _inhabitants.Report(new WorldEvent(BuildPlanRejected, world.Tick, task.InhabitantId, null, task.TypeId));
            RetryPlan(world, task, retryable: true);
            return;
        }

        // The site is where the inventor stands now (rounded to 10 cm, the same in every run).
        var at = _inhabitants.Get(task.InhabitantId).Position;
        _builds[task.TypeId] = task with
        {
            RequestId = null,
            PlanJson = JsonSerializer.Serialize(plan, RecordJson),
            SiteX = Math.Round(at.X, 1, MidpointRounding.AwayFromZero),
            SiteZ = Math.Round(at.Z, 1, MidpointRounding.AwayFromZero),
        };
        _inhabitants.Report(new WorldEvent(BuildPlanned, world.Tick, task.InhabitantId, null, task.TypeId));
    }

    private void RetryPlan(World world, BuildTask task, bool retryable)
    {
        if (!retryable || task.Attempts >= Build.Settings.MaxPlanAttempts)
        {
            _builds[task.TypeId] = task with { RequestId = null, RetryAtTick = null, GaveUp = true };
            _inhabitants.Report(new WorldEvent(BuildFailed, world.Tick, task.InhabitantId, null, task.TypeId));
            return;
        }

        var after = (long)Build.Settings.RetryAfterMinutes * world.Calendar.Settings.TicksPerHour / 60;
        _builds[task.TypeId] = task with { RequestId = null, RetryAtTick = world.Tick + Math.Max(1, after) };
    }

    /// <summary>Plan retries, then one material errand per inventor whose building waits for material.</summary>
    private void AdvanceBuilds(World world)
    {
        foreach (var due in _builds.Values.Where(b => b.RetryAtTick is { } at && at <= world.Tick).ToList())
        {
            _builds[due.TypeId] = SubmitPlan(world, due);
        }

        // A finished invention that needs a sound gets its sound job (in the tick it was built).
        foreach (var done in _builds.Values.Where(b => b.BuiltObjectId is not null && !_sounds.ContainsKey(b.TypeId)).ToList())
        {
            if (SoundOf(_inventions[done.TypeId]) is { Needed: true } decision)
            {
                SubmitSound(world, done, decision);
            }
        }

        foreach (var task in _builds.Values.Where(b => b.PlanJson is not null && b.BuiltObjectId is null).ToList())
        {
            var inhabitant = _inhabitants.Get(task.InhabitantId);
            if (task.PendingObjectId is { } pending)
            {
                if (inhabitant.Activity?.TargetId == pending || inhabitant.Planned?.TargetId == pending)
                {
                    continue;
                }

                // Something else came first (a decision, the object is gone): look again.
                _builds[task.TypeId] = task with { PendingObjectId = null, PendingType = null };
            }

            if (inhabitant.Planned is not null
                || Build.NeglectableNeeds.Any(n => inhabitant.Needs.GetValueOrDefault(n, 1.0) < Build.Settings.MinNeedToBuild))
            {
                continue;
            }

            var missing = StepMaterials(ParsePlan(task), task.StepsDone + 1).Except(task.Gathered, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var radiusSquared = _perceptionRadiusM * _perceptionRadiusM;
            var material = _objects.All
                .Where(o => missing.Contains(o.TypeId))
                .Select(o => (o, d: Square(o.Position.X - inhabitant.Position.X) + Square(o.Position.Z - inhabitant.Position.Z)))
                .Where(x => x.d <= radiusSquared)
                .OrderBy(x => x.d)
                .ThenBy(x => x.o.Id, StringComparer.Ordinal)
                .Select(x => x.o)
                .FirstOrDefault();
            if (material is null)
            {
                // No material in sight: the building waits (no substitute, B2487).
                continue;
            }

            inhabitant.Planned = new Activity(Build.Settings.StepAction, material.Id, material.Position, null);
            _builds[task.TypeId] = _builds[task.TypeId] with { PendingObjectId = material.Id, PendingType = material.TypeId };
        }
    }

    private static List<string> StepMaterials(BuildPlan plan, int step) =>
        [.. plan.Parts.Where(p => p.Step == step).Select(p => p.Material).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>Follows finished actions: material worked in, steps done, the finished object, first use.</summary>
    private void Observe(WorldEvent e)
    {
        if (e.Kind == InhabitantsSystem.ActionCancelled)
        {
            foreach (var task in _builds.Values.Where(b => b.PendingObjectId is not null && b.PendingObjectId == e.TargetId).ToList())
            {
                _builds[task.TypeId] = task with { PendingObjectId = null, PendingType = null };
            }

            return;
        }

        if (e.Kind != InhabitantsSystem.ActionFinished || e.TargetId is null)
        {
            return;
        }

        if (e.Action == Build.Settings.StepAction)
        {
            var task = _builds.Values.FirstOrDefault(b => b.PendingObjectId == e.TargetId && b.InhabitantId == e.InhabitantId);
            if (task is not null)
            {
                WorkIn(e, task);
            }

            return;
        }

        if (e.Action == Build.Settings.UseAction
            && _objects.TryGet(e.TargetId, out var used)
            && _inventions.ContainsKey(used.TypeId)
            && _firstUsed.Add(used.TypeId))
        {
            _inhabitants.Report(new WorldEvent(FirstUse, e.Tick, e.InhabitantId, e.Action, e.TargetId));
        }
    }

    private void WorkIn(WorldEvent e, BuildTask task)
    {
        var plan = ParsePlan(task);
        var step = task.StepsDone + 1;
        var gathered = task.Gathered.Append(task.PendingType!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        task = task with { Gathered = gathered, PendingObjectId = null, PendingType = null };
        if (StepMaterials(plan, step).Except(gathered, StringComparer.Ordinal).Any())
        {
            _builds[task.TypeId] = task;
            return;
        }

        task = task with { StepsDone = step, Gathered = [] };
        _builds[task.TypeId] = task;
        _inhabitants.Report(new WorldEvent(BuildStep, e.Tick, task.InhabitantId, step.ToString(CultureInfo.InvariantCulture), task.TypeId));
        if (step < plan.Parts.Max(p => p.Step))
        {
            return;
        }

        var built = _objects.Spawn(task.TypeId, new Position(task.SiteX, task.SiteZ));
        _builds[task.TypeId] = task with { BuiltObjectId = built.Id };
        _inhabitants.Report(new WorldEvent(Built, e.Tick, task.InhabitantId, null, built.Id));
    }

    private static SoundDecision? SoundOf(InventionRecord record) =>
        record.SoundJson is null ? null : JsonSerializer.Deserialize<SoundDecision>(record.SoundJson, RecordJson);

    /// <summary>Asks the sound model for the invention's own sound (it runs in the compute night, B7 S4).</summary>
    private void SubmitSound(World world, BuildTask task, SoundDecision decision)
    {
        _counter++;
        var tick = world.Tick;
        var requestId = DecisionIds.RequestId(world.Seed, tick, task.InhabitantId + "/sound", _counter);
        var generation = Build.Sound;
        var job = new GenerationJob
        {
            ContractVersion = ContractInfo.Version,
            RequestId = requestId,
            Tick = tick,
            Seed = SeedOf(requestId),
            DisplayLocale = Enum.Parse<Locale>(_displayLocale, ignoreCase: true),
            Kind = JobKind.Sound,
            Priority = generation.Priority,
            Inhabitant = task.InhabitantId,
            Object = task.BuiltObjectId,
            Payload = new JobPayload
            {
                Sound = new SoundPayload
                {
                    Description = decision.Description,
                    Kind = decision.Kind,
                    Seconds = decision.Kind == SoundKind.Loop ? generation.Seconds.Loop : generation.Seconds.OneShot,
                    Steps = generation.DefaultSteps,
                    ObjectType = task.TypeId,
                },
            },
        };
        var json = ContractSerializer.Serialize(job);
        Log.Add(new DecisionLogEntry(DecisionLogKind.JobRequest, requestId, tick, json, null));
        _port!.Submit(requestId, json);
        _sounds[task.TypeId] = new SoundTask(task.TypeId, requestId, null, false);
    }

    /// <summary>Routes a job outcome to a sound job; false if it belongs to none.</summary>
    private bool ApplySound(World world, PortMessage message)
    {
        var sound = _sounds.Values.FirstOrDefault(s => s.RequestId == message.RequestId && s.ResultJson is null && !s.Failed);
        if (sound is null)
        {
            return false;
        }

        JobStatus? status = null;
        if (!message.IsError)
        {
            try
            {
                status = ContractSerializer.Deserialize<JobStatus>(message.Json);
            }
            catch (ContractViolationException)
            {
                status = null;
            }
        }

        if (status is { State: JobState.Done, Kind: JobKind.Sound, Result.Sound: { } result } && status.JobId == sound.RequestId)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobResult, message.RequestId, world.Tick, message.Json, null));
            _sounds[sound.TypeId] = sound with { ResultJson = JsonSerializer.Serialize(result, RecordJson) };
            return true;
        }

        // Any failure: the invention keeps no sound of its own; the game takes the base library (D7).
        Log.Add(new DecisionLogEntry(message.IsError ? DecisionLogKind.JobError : DecisionLogKind.JobDiscarded, message.RequestId, world.Tick, message.Json, message.IsError ? ReplayNotes.ServiceError : "sound_failed"));
        _sounds[sound.TypeId] = sound with { Failed = true };
        return true;
    }

    /// <summary>Tells the AI service when the compute night begins and ends (only a signal; nothing in the world depends on it).</summary>
    private void SignalPhase(World world)
    {
        var phase = world.Calendar.At(world.Tick).Phase.ToString().ToLowerInvariant();
        var night = Build.NightPhases.Contains(phase);
        if (_night == night)
        {
            return;
        }

        _night = night;
        _port!.SetPhase(night ? JobPhase.Night : JobPhase.Day, world.Tick);
    }

    private void WriteBuildState(StateHasher hasher)
    {
        hasher.Write(_builds.Count);
        foreach (var b in _builds.Values)
        {
            hasher.Write(b.TypeId).Write(b.InhabitantId).Write(b.Attempts).Write(b.RequestId ?? string.Empty).Write(b.RetryAtTick ?? -1)
                .Write(b.PlanJson ?? string.Empty).Write(b.SiteX).Write(b.SiteZ).Write(b.StepsDone).Write(b.Gathered.Count);
            foreach (var g in b.Gathered)
            {
                hasher.Write(g);
            }

            hasher.Write(b.PendingObjectId ?? string.Empty).Write(b.PendingType ?? string.Empty).Write(b.BuiltObjectId ?? string.Empty).Write(b.GaveUp);
        }

        hasher.Write(_firstUsed.Count);
        foreach (var t in _firstUsed)
        {
            hasher.Write(t);
        }

        hasher.Write(_sounds.Count);
        foreach (var s in _sounds.Values)
        {
            hasher.Write(s.TypeId).Write(s.RequestId).Write(s.ResultJson ?? string.Empty).Write(s.Failed);
        }
    }

    private void RestoreBuilds(InventionState state)
    {
        foreach (var task in state.Builds ?? [])
        {
            _builds[task.TypeId] = task;
        }

        foreach (var type in state.FirstUsed ?? [])
        {
            _firstUsed.Add(type);
        }

        foreach (var sound in state.Sounds ?? [])
        {
            _sounds[sound.TypeId] = sound;
        }
    }
}
