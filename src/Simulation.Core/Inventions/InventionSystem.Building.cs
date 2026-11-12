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
/// <param name="Image">Sizes, style and priority of image jobs (content/image/generation.json; A1564); null = no picture jobs.</param>
public sealed record BuildRules(BuildSettings Settings, BuildKit Kit, FrozenSet<string> NeglectableNeeds, FrozenSet<string> NightPhases, SoundGeneration Sound, ImageGeneration? Image = null);

/// <summary>The picture job of a new invention (A1564, B7 R7) and its result.</summary>
/// <param name="TypeId">Runtime type the picture belongs to.</param>
/// <param name="RequestId">The image job.</param>
/// <param name="ResultJson">ImageResult once the picture exists, else null.</param>
/// <param name="Failed">True when the invention finally keeps no picture of its own (the kit's colours).</param>
/// <param name="Attempts">Image jobs sent so far.</param>
/// <param name="RetryAtNight">True while a retryable failure waits for the next compute night.</param>
public sealed record ImageTask(string TypeId, string RequestId, string? ResultJson, bool Failed, int Attempts = 1, bool RetryAtNight = false);

/// <summary>The sound job of a built invention (D471 D7) and its result.</summary>
/// <param name="TypeId">Runtime type the sound belongs to.</param>
/// <param name="RequestId">The sound job.</param>
/// <param name="ResultJson">SoundResult once the sound exists, else null.</param>
/// <param name="Failed">True when the invention finally keeps no sound of its own (base library by description).</param>
/// <param name="Attempts">Sound jobs sent so far.</param>
/// <param name="RetryAtNight">True while a retryable failure waits for the next compute night.</param>
public sealed record SoundTask(string TypeId, string RequestId, string? ResultJson, bool Failed, int Attempts = 1, bool RetryAtNight = false);

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
/// <param name="GaveUp">True when no acceptable plan came after the allowed attempts, or no material after waiting too long.</param>
/// <param name="WaitingSinceTick">Since when no material of the current step was in sight, or null (A1373).</param>
/// <param name="LookedTicks">Ticks of this wait in which the builder was free and looked about (asleep, eating or busy do not count).</param>
/// <param name="NeedsHelp">True once the builder looked long enough by itself and needs help (A1373, R15).</param>
/// <param name="Escalations">Questions to the decider for this building so far (A1373 step 2, R17c).</param>
/// <param name="Substitutes">Material types taken instead of a plan material (plan type to substitute type), or null.</param>
/// <param name="Smaller">True when the builder finished it smaller: the thing stands as far as it was built.</param>
/// <param name="Chosen">True while an answer about this stuck building is under way (the chosen action not finished yet).</param>
/// <param name="Quality">Quality tier of the built thing (data id, D827 R19), or null while unbuilt or without the data.</param>
/// <param name="Helper">The inhabitant who builds along after being asked for help (A1376, B2663), or null.</param>
/// <param name="HelperPendingObjectId">Material object the helper is on the way to, or null.</param>
/// <param name="HelperPendingType">The plan type that material stands for.</param>
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
    bool GaveUp,
    long? WaitingSinceTick = null,
    bool NeedsHelp = false,
    long LookedTicks = 0,
    int Escalations = 0,
    IReadOnlyDictionary<string, string>? Substitutes = null,
    bool Smaller = false,
    bool Chosen = false,
    string? Quality = null,
    string? Helper = null,
    string? HelperPendingObjectId = null,
    string? HelperPendingType = null);

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

    /// <summary>No acceptable plan after the allowed attempts, or no material after waiting too long - detail <see cref="NoMaterial"/> (target = runtime type).</summary>
    public const string BuildFailed = "build_failed";

    /// <summary>Detail of <see cref="BuildFailed"/> when the builder waited too long without material in sight.</summary>
    public const string NoMaterial = "no_material";

    /// <summary>
    /// A builder looked for the material of the current step by itself long enough without finding any (A1373, R15):
    /// target = runtime type, detail = the missing material's true properties (ordinal, comma-separated; never its
    /// type name), text = <see cref="InWorld"/> when some of it lies elsewhere in the world, else empty.
    /// </summary>
    public const string NeedsHelpEvent = "needs_help";

    /// <summary>Text of <see cref="NeedsHelpEvent"/>: the material exists in the world, out of the builder's sight.</summary>
    public const string InWorld = "in_world";

    /// <summary>Detail of <see cref="BuildFailed"/> when the builder chose to give up (A1373 step 2; data event_effects: frustration, R17f).</summary>
    public const string GaveUpByChoice = "gave_up";

    /// <summary>Detail of <see cref="Built"/> for a thing finished smaller (it stands as far as it was built).</summary>
    public const string FinishedSmaller = "smaller";

    /// <summary>Event: a substitute truly has what was missing (target = invention type, detail = substitute type).</summary>
    public const string SubstituteTaken = "substitute_taken";

    /// <summary>Event: a stuck builder asked another for help (target = the helper, detail = the missing properties, A1376).</summary>
    public const string AskedForHelp = "asked_for_help";

    /// <summary>Event: the helper showed the builder where it knows a missing material (target = the thing, A1376).</summary>
    public const string MaterialShown = "material_shown";

    /// <summary>Event: a substitute lacks what was missing (target = invention type, detail = substitute type); the builder learned it.</summary>
    public const string SubstituteFailed = "substitute_failed";

    /// <summary>
    /// A1373 step 2 (set by the host): asks the decider what to do about a stuck building, with the given options
    /// (action, target); true if the question went out. Null = never asks.
    /// </summary>
    public Func<World, string, string, IReadOnlyList<(string Action, string? TargetId)>, bool>? Escalate { get; set; }

    /// <summary>Whether a question about this building is open (inhabitant, invention type); set by the host.</summary>
    public Func<string, string, bool>? EscalationOpen { get; set; }

    /// <summary>An inhabitant used an invention for the first time (target = object).</summary>
    public const string FirstUse = "first_use";

    private readonly SortedDictionary<string, BuildTask> _builds = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _firstUsed = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, SoundTask> _sounds = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, ImageTask> _images = new(StringComparer.Ordinal);
    private bool? _night;

    /// <summary>
    /// True while the compute night of the AI service runs (B7): the day phase is one of the data's
    /// NightPhases. Only a signal for the service and the screen; the world and its time go on as usual.
    /// Null until the first tick of a new or loaded world has decided it, so a world started at night does
    /// not look like a night that just began.
    /// </summary>
    public bool? IsComputeNight => _night;

    /// <summary>Building tasks by runtime type.</summary>
    public IEnumerable<BuildTask> Builds => _builds.Values;

    /// <summary>Sound jobs of built inventions (D471 D7).</summary>
    public IEnumerable<SoundTask> Sounds => _sounds.Values;

    /// <summary>Picture jobs of inventions and their results (A1564).</summary>
    public IEnumerable<ImageTask> Images => _images.Values;

    /// <summary>The picture of an invention once it exists (A1564), else null.</summary>
    public ImageResult? ImageOf(string typeId) =>
        _images.TryGetValue(typeId, out var image) && image.ResultJson is { } json ? JsonSerializer.Deserialize<ImageResult>(json, RecordJson) : null;

    /// <summary>Runtime types that have been used at least once.</summary>
    public IEnumerable<string> FirstUsed => _firstUsed;

    private BuildRules Build => _rules.Build;

    private static BuildPlan ParsePlan(BuildTask task) =>
        JsonSerializer.Deserialize<BuildPlan>(task.PlanJson!, RecordJson) ?? throw new InvalidDataException($"Building plan of '{task.TypeId}' is empty.");

    /// <summary>A building plan as the core keeps it (<see cref="BuildTask.PlanJson"/>), for whoever draws it (A1375).</summary>
    public static BuildPlan PlanOf(string planJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(planJson);
        return JsonSerializer.Deserialize<BuildPlan>(planJson, RecordJson) ?? throw new InvalidDataException("A building plan is empty.");
    }

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
        var json = ContractSerializer.Serialize(job, ValidateRequests);
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
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobError, message.RequestId, world.Tick, message.Json, ReplayNotes.OfError(message.Json)));
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

        // A building given up stays given up (review GW3 #283): no new wait, no further step.
        foreach (var task in _builds.Values.Where(b => b.PlanJson is not null && b.BuiltObjectId is null && !b.GaveUp).ToList())
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
            var standsFor = StandsFor(task, missing);
            var radius = _inhabitants.Sight.Metres(_perceptionRadiusM);
            var radiusSquared = radius * radius;
            var material = _objects.All
                .Where(o => standsFor.ContainsKey(o.TypeId) && o.Id != task.HelperPendingObjectId)
                .Select(o => (o, d: Square(o.Position.X - inhabitant.Position.X) + Square(o.Position.Z - inhabitant.Position.Z)))
                .Where(x => x.d <= radiusSquared)
                .OrderBy(x => x.d)
                .ThenBy(x => x.o.Id, StringComparer.Ordinal)
                .Select(x => x.o)
                .FirstOrDefault() ?? Remembered(inhabitant, standsFor, task.HelperPendingObjectId);
            if (material is null)
            {
                // No material in sight: the building waits (no substitute, B2487) while the inhabitant looks about
                // by its routine; after the data's hours it needs help, after more it gives up (A1373, R15, R17).
                Wait(world, _builds[task.TypeId], missing);
                continue;
            }

            inhabitant.Planned = new Activity(Build.Settings.StepAction, material.Id, material.Position, null);
            _builds[task.TypeId] = _builds[task.TypeId] with { PendingObjectId = material.Id, PendingType = standsFor[material.TypeId], WaitingSinceTick = null, NeedsHelp = false, LookedTicks = 0 };
        }

        AdvanceHelpers();
    }

    /// <summary>
    /// A material the inhabitant remembers where it lies (place memory - its own, or shown by a helper, A1376), nearest
    /// first, still there; null if it knows none. The building then walks there.
    /// </summary>
    private WorldObject? Remembered(Inhabitant inhabitant, Dictionary<string, string> standsFor, string? taken)
    {
        WorldObject? best = null;
        var bestD = double.MaxValue;
        foreach (var (objectId, place) in inhabitant.Places)
        {
            if (objectId == taken || !standsFor.ContainsKey(place.TypeId) || !_objects.TryGet(objectId, out var thing) || thing.TypeId != place.TypeId)
            {
                continue;
            }

            var d = Square(thing.Position.X - inhabitant.Position.X) + Square(thing.Position.Z - inhabitant.Position.Z);
            if (d < bestD || (d == bestD && string.CompareOrdinal(thing.Id, best!.Id) < 0))
            {
                (best, bestD) = (thing, d);
            }
        }

        return best;
    }

    /// <summary>
    /// A1376 (B2663 (3)): the helper builds along - while it is free and its needs allow it, it fetches a missing material
    /// it sees or remembers (not the one the builder is on the way to) and works it in at the building.
    /// </summary>
    private void AdvanceHelpers()
    {
        foreach (var task in _builds.Values.Where(b => b.Helper is not null && b.PlanJson is not null && b.BuiltObjectId is null && !b.GaveUp).ToList())
        {
            if (!_inhabitants.TryGet(task.Helper!, out var helper) || _inhabitants.IsAsleep(helper))
            {
                continue;
            }

            if (task.HelperPendingObjectId is { } pending)
            {
                if (helper.Activity?.TargetId == pending || helper.Planned?.TargetId == pending)
                {
                    continue;
                }

                _builds[task.TypeId] = task with { HelperPendingObjectId = null, HelperPendingType = null };
            }

            if (helper.Planned is not null || Build.NeglectableNeeds.Any(n => helper.Needs.GetValueOrDefault(n, 1.0) < Build.Settings.MinNeedToBuild))
            {
                continue;
            }

            var current = _builds[task.TypeId];
            var missing = StepMaterials(ParsePlan(current), current.StepsDone + 1).Except(current.Gathered, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            if (current.PendingType is { } builderFetches)
            {
                missing.Remove(builderFetches);
            }

            var standsFor = StandsFor(current, missing);
            var radius = _inhabitants.Sight.Metres(_perceptionRadiusM);
            var radiusSquared = radius * radius;
            var material = _objects.All
                .Where(o => standsFor.ContainsKey(o.TypeId) && o.Id != current.PendingObjectId)
                .Select(o => (o, d: Square(o.Position.X - helper.Position.X) + Square(o.Position.Z - helper.Position.Z)))
                .Where(x => x.d <= radiusSquared)
                .OrderBy(x => x.d)
                .ThenBy(x => x.o.Id, StringComparer.Ordinal)
                .Select(x => x.o)
                .FirstOrDefault() ?? Remembered(helper, standsFor, current.PendingObjectId);
            if (material is null)
            {
                continue;
            }

            helper.Planned = new Activity(Build.Settings.StepAction, material.Id, material.Position, null);
            _builds[task.TypeId] = current with { HelperPendingObjectId = material.Id, HelperPendingType = standsFor[material.TypeId] };
        }
    }

    /// <summary>Builder and helper grow fonder of each other with every material the helper works in (data, A1376).</summary>
    private void GrowFond(string builderId, string helperId)
    {
        if (Build.Settings.Escalation?.HelpAffectionPerStep is not { } step || step <= 0
            || !_inhabitants.TryGet(builderId, out var builder) || !_inhabitants.TryGet(helperId, out var helper))
        {
            return;
        }

        foreach (var (who, other) in new[] { (builder, helperId), (helper, builderId) })
        {
            var r = who.Relationships.Get(other, new Relation(0, 0, 0));
            who.Relationships.Set(other, r with { Affection = Math.Min(1, r.Affection + step) });
        }
    }

    /// <summary>
    /// One tick of waiting for material (A1373): the wait starts now if it has not, and this tick counts as one of
    /// looking by itself (only ticks in which the builder is free get here - not asleep, eating or busy, review GW3
    /// #283). After help_after_hours of looking the builder needs help (once per wait), after give_up_after_hours it
    /// gives the building up. Whether the material lies elsewhere in the world is said, not decided.
    /// </summary>
    private void Wait(World world, BuildTask task, HashSet<string> missing)
    {
        task = task with { WaitingSinceTick = task.WaitingSinceTick ?? world.Tick, LookedTicks = task.LookedTicks + 1 };
        var waited = task.LookedTicks;
        var ticksPerHour = world.Calendar.Settings.TicksPerHour;
        if (Build.Settings.GiveUpAfterHours is { } giveUp && waited >= (long)(giveUp * ticksPerHour))
        {
            _builds[task.TypeId] = task with { GaveUp = true, NeedsHelp = false, WaitingSinceTick = null, LookedTicks = 0 };
            _inhabitants.Report(new WorldEvent(BuildFailed, world.Tick, task.InhabitantId, null, task.TypeId) { Detail = NoMaterial });
            return;
        }

        if (task.NeedsHelp && Build.Settings.HelpAfterHours is { } helpAfter && Build.Settings.Escalation is { } escalation && Escalate is { } escalate
            && task.Escalations < escalation.Max
            && waited >= (long)((helpAfter + ((task.Escalations + 1) * escalation.AfterHours)) * ticksPerHour)
            && EscalationOpen?.Invoke(task.InhabitantId, task.TypeId) != true)
        {
            // A1373 step 2 (D850): the help did not come - the builder asks what to do (the core's options only).
            // Counted before the request is built, so the request says which question this is.
            var asking = task with { Escalations = task.Escalations + 1 };
            _builds[task.TypeId] = asking;
            if (escalate(world, task.InhabitantId, task.TypeId, EscalationOptions(task, missing)))
            {
                task = asking;
            }
        }

        if (!task.NeedsHelp && Build.Settings.HelpAfterHours is { } help && waited >= (long)(help * ticksPerHour))
        {
            task = task with { NeedsHelp = true };
            var properties = missing.SelectMany(m => _types.Contains(m) ? _types.TrueProperties(m) : []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var elsewhere = _objects.All.Any(o => missing.Contains(o.TypeId));
            _inhabitants.Report(new WorldEvent(NeedsHelpEvent, world.Tick, task.InhabitantId, null, task.TypeId)
            {
                Detail = string.Join(',', properties),
                Text = elsewhere ? InWorld : string.Empty,
            });
        }

        _builds[task.TypeId] = task;
    }

    /// <summary>
    /// Which material type stands for which missing plan type: each missing type for itself, and a substitute the
    /// builder took instead (A1373 step 2) for the plan type it replaces.
    /// </summary>
    private static Dictionary<string, string> StandsFor(BuildTask task, HashSet<string> missing)
    {
        var standsFor = missing.ToDictionary(m => m, m => m, StringComparer.Ordinal);
        foreach (var (planType, substitute) in task.Substitutes ?? new Dictionary<string, string>())
        {
            if (missing.Contains(planType))
            {
                standsFor.TryAdd(substitute, planType);
            }
        }

        return standsFor;
    }

    /// <summary>The properties the missing plan materials truly have (what the step lacks; sorted).</summary>
    private List<string> MissingProperties(IEnumerable<string> missing) =>
        [.. missing.SelectMany(m => _types.Contains(m) ? _types.TrueProperties(m) : []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The ways the core leaves open for a stuck building (D850): a thing in sight of another kind that the builder
    /// BELIEVES has every missing property (nearest kinds first, at most the data's number), finishing smaller once a
    /// step is done, and giving up. Asking another for help comes with the next step.
    /// </summary>
    private List<(string Action, string? TargetId)> EscalationOptions(BuildTask task, HashSet<string> missing)
    {
        var escalation = Build.Settings.Escalation!;
        var options = new List<(string Action, string? TargetId)>();
        var inhabitant = _inhabitants.Get(task.InhabitantId);
        var needed = MissingProperties(missing);
        if (needed.Count > 0)
        {
            var radius = _inhabitants.Sight.Metres(_perceptionRadiusM);
            var radiusSquared = radius * radius;
            var candidates = _objects.All
                .Where(o => !missing.Contains(o.TypeId) && o.TypeId != task.TypeId)
                .Select(o => (o, d: Square(o.Position.X - inhabitant.Position.X) + Square(o.Position.Z - inhabitant.Position.Z)))
                .Where(x => x.d <= radiusSquared)
                .Where(x => needed.All(p => inhabitant.Beliefs.TryGet(x.o.TypeId, p, out var belief) && belief.Value))
                .OrderBy(x => x.d)
                .ThenBy(x => x.o.Id, StringComparer.Ordinal)
                .GroupBy(x => x.o.TypeId, StringComparer.Ordinal)
                .Select(g => g.First().o)
                .Take(escalation.MaxUseInstead ?? 2);
            options.AddRange(candidates.Select(o => (escalation.UseInsteadAction, (string?)o.Id)));
        }

        // Ask for help (A1376, B2663 (3)): the nearest awake inhabitant in sight whose name the builder knows.
        if (escalation.HelpAction is { } help && task.Helper is null)
        {
            var radius = _inhabitants.Sight.Metres(_perceptionRadiusM);
            var helper = _inhabitants.All
                .Where(o => o.Id != inhabitant.Id && inhabitant.KnownNames.Contains(o.Id) && !_inhabitants.IsAsleep(o))
                .Select(o => (o, d: Square(o.Position.X - inhabitant.Position.X) + Square(o.Position.Z - inhabitant.Position.Z)))
                .Where(x => x.d <= radius * radius)
                .OrderBy(x => x.d)
                .ThenBy(x => x.o.Id, StringComparer.Ordinal)
                .Select(x => x.o)
                .FirstOrDefault();
            if (helper is not null)
            {
                options.Add((help, helper.Id));
            }
        }

        if (task.StepsDone >= 1)
        {
            options.Add((escalation.SmallerAction, null));
        }

        options.Add((escalation.GiveUpAction, null));
        return options;
    }

    /// <summary>What the builder chose for its stuck building is done (A1373 step 2, D850; the decider chose among the core's options).</summary>
    private void Escalated(WorldEvent e, BuildEscalation escalation)
    {
        // The building whose question was answered (review GW3 #295), not just any of the builder's.
        var task = _builds.Values.FirstOrDefault(b => b.InhabitantId == e.InhabitantId && b.Chosen && b.PlanJson is not null && b.BuiltObjectId is null && !b.GaveUp);
        if (task is null)
        {
            return;
        }

        task = task with { Chosen = false };
        _builds[task.TypeId] = task;

        if (e.Action == escalation.GiveUpAction)
        {
            _builds[task.TypeId] = task with { GaveUp = true, NeedsHelp = false, WaitingSinceTick = null, LookedTicks = 0 };
            _inhabitants.Report(new WorldEvent(BuildFailed, e.Tick, task.InhabitantId, null, task.TypeId) { Detail = GaveUpByChoice });
            return;
        }

        if (e.Action == escalation.SmallerAction)
        {
            if (task.StepsDone < 1)
            {
                return;
            }

            // The thing stands as far as it was built: built, not finished in full.
            var built = _objects.Spawn(task.TypeId, new Position(task.SiteX, task.SiteZ));
            _builds[task.TypeId] = task with { BuiltObjectId = built.Id, Smaller = true, NeedsHelp = false, WaitingSinceTick = null, LookedTicks = 0, PendingObjectId = null, PendingType = null, Quality = QualityOf(task) };
            _inhabitants.Report(new WorldEvent(Built, e.Tick, task.InhabitantId, null, built.Id) { Detail = FinishedSmaller });
            KnowOwnWork(e.Tick, task);
            return;
        }

        if (e.Action == escalation.HelpAction && e.TargetId is { } helperId && _inhabitants.TryGet(helperId, out var asked))
        {
            AskedForHelpBy(e, task, asked);
            return;
        }

        if (e.TargetId is null || !_objects.TryGet(e.TargetId, out var thing))
        {
            return;
        }

        // Taking a thing instead: the world judges by the TRUE properties (the builder only believed them), and the
        // builder learns them by trying either way.
        var missing = StepMaterials(ParsePlan(task), task.StepsDone + 1).Except(task.Gathered, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var needed = MissingProperties(missing);
        _inhabitants.Experience(e.Tick, _inhabitants.Get(task.InhabitantId), e.Action!, thing.TypeId, needed);
        if (needed.Count == 0 || !needed.All(p => _types.HasProperty(thing.TypeId, p)))
        {
            _inhabitants.Report(new WorldEvent(SubstituteFailed, e.Tick, task.InhabitantId, e.Action, task.TypeId) { Detail = thing.TypeId });
            return;
        }

        var substitutes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (planType, substitute) in task.Substitutes ?? new Dictionary<string, string>())
        {
            substitutes[planType] = substitute;
        }

        foreach (var planType in missing)
        {
            substitutes[planType] = thing.TypeId;
        }

        _builds[task.TypeId] = task with { Substitutes = substitutes, NeedsHelp = false, WaitingSinceTick = null, LookedTicks = 0 };
        _inhabitants.Report(new WorldEvent(SubstituteTaken, e.Tick, task.InhabitantId, e.Action, task.TypeId) { Detail = thing.TypeId });
    }

    /// <summary>
    /// The decider answered the question about this stuck building (set by the host): the chosen action, when it
    /// finishes, belongs to THIS building, even if its builder has another one (review GW3 #295).
    /// </summary>
    public void EscalationAnswered(string inhabitantId, string typeId)
    {
        if (_builds.TryGetValue(typeId, out var task) && task.InhabitantId == inhabitantId && task.BuiltObjectId is null && !task.GaveUp)
        {
            _builds[typeId] = task with { Chosen = true };
        }
    }

    /// <summary>The skill level of an inhabitant (set by the host from the skill levels, A1376); null = no quality.</summary>
    public Func<string, string, double>? SkillOf { get; set; }

    /// <summary>
    /// D827 R19 (A1376): the quality tier of a thing finished now - the highest tier of the data whose from_level the
    /// builder's skill reaches; null without the data or the skill levels.
    /// </summary>
    private string? QualityOf(BuildTask task)
    {
        if (Build.Settings.Quality is not { } quality || SkillOf is not { } skillOf)
        {
            return null;
        }

        // Built along (A1376): the better hand of the two counts.
        var level = Math.Max(skillOf(task.InhabitantId, quality.Skill), task.Helper is { } helper ? skillOf(helper, quality.Skill) : 0);
        return quality.Tiers.Where(t => level >= t.FromLevel).OrderBy(t => t.FromLevel).LastOrDefault()?.Id;
    }

    /// <summary>The quality tier of a built invention (data id, A1376), or null.</summary>
    public string? QualityOfBuilt(string typeId) => _builds.TryGetValue(typeId, out var task) ? task.Quality : null;

    /// <summary>The options of a stuck building as they are now (A1373 step 2): none if the building is not stuck.</summary>
    public IReadOnlyList<(string Action, string? TargetId)> EscalationOptionsOf(string inhabitantId, string typeId)
    {
        if (!_builds.TryGetValue(typeId, out var task) || task.InhabitantId != inhabitantId || Build.Settings.Escalation is null
            || task.PlanJson is null || task.BuiltObjectId is not null || task.GaveUp)
        {
            return [];
        }

        var missing = StepMaterials(ParsePlan(task), task.StepsDone + 1).Except(task.Gathered, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        return EscalationOptions(task, missing);
    }

    /// <summary>
    /// The builder asked another for help (A1376, B2663 (3), D827 R19): the helper shows where it knows a missing
    /// material (it passes its place memory on), and from now on builds along. The builder looks again at once.
    /// </summary>
    private void AskedForHelpBy(WorldEvent e, BuildTask task, Inhabitant helper)
    {
        var builder = _inhabitants.Get(task.InhabitantId);
        var missing = StepMaterials(ParsePlan(task), task.StepsDone + 1).Except(task.Gathered, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var standsFor = StandsFor(task, missing);
        _inhabitants.Report(new WorldEvent(AskedForHelp, e.Tick, builder.Id, e.Action, helper.Id) { Detail = string.Join(',', MissingProperties(missing)) });
        var max = _inhabitants.Knowledge?.Looking?.PlaceMemoryMax ?? 0;
        if (max > 0)
        {
            foreach (var (objectId, place) in helper.Places.Where(p => standsFor.ContainsKey(p.Value.TypeId)).ToList())
            {
                if (_objects.TryGet(objectId, out var thing) && thing.TypeId == place.TypeId && !builder.Places.ContainsKey(objectId))
                {
                    builder.RememberPlace(objectId, place, max);
                    _inhabitants.Report(new WorldEvent(MaterialShown, e.Tick, helper.Id, e.Action, objectId) { Detail = builder.Id });
                }
            }
        }

        _builds[task.TypeId] = task with { Helper = helper.Id, NeedsHelp = false, WaitingSinceTick = null, LookedTicks = 0 };
    }

    /// <summary>What a stuck building's question tells the decider (A1373 step 2), or null if the building is not stuck.</summary>
    public StuckBuilding? StuckOf(string inhabitantId, string typeId)
    {
        if (!_builds.TryGetValue(typeId, out var task) || task.InhabitantId != inhabitantId || Build.Settings.Escalation is not { } escalation
            || task.PlanJson is null || task.BuiltObjectId is not null || task.GaveUp || !_inventions.TryGetValue(typeId, out var record))
        {
            return null;
        }

        var missing = StepMaterials(ParsePlan(task), task.StepsDone + 1).Except(task.Gathered, StringComparer.Ordinal);
        return new StuckBuilding
        {
            Name = Parse(record).Name,
            MissingProperties = MissingProperties(missing),
            Escalation = Math.Clamp(task.Escalations, 1, escalation.Max),
            MaxEscalations = escalation.Max,
        };
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

            foreach (var task in _builds.Values.Where(b => b.HelperPendingObjectId is not null && b.HelperPendingObjectId == e.TargetId).ToList())
            {
                _builds[task.TypeId] = task with { HelperPendingObjectId = null, HelperPendingType = null };
            }

            return;
        }

        if (e.Kind == InhabitantsSystem.ActionFinished && Build.Settings.Escalation is { } escalation
            && e.Action is { } chosen && (chosen == escalation.GiveUpAction || chosen == escalation.SmallerAction || chosen == escalation.UseInsteadAction))
        {
            Escalated(e, escalation);
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
                WorkIn(e, task with { PendingObjectId = null, PendingType = null }, task.PendingType!);
            }
            else if (_builds.Values.FirstOrDefault(b => b.HelperPendingObjectId == e.TargetId && b.Helper == e.InhabitantId) is { } helped)
            {
                // Built along (A1376): the helper's material counts like the builder's, and they grow fonder of each other.
                WorkIn(e, helped with { HelperPendingObjectId = null, HelperPendingType = null }, helped.HelperPendingType!);
                GrowFond(helped.InhabitantId, e.InhabitantId);
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

    private void WorkIn(WorldEvent e, BuildTask task, string planType)
    {
        var plan = ParsePlan(task);
        var step = task.StepsDone + 1;
        var gathered = task.Gathered.Append(planType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        task = task with { Gathered = gathered };
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
        _builds[task.TypeId] = task with { BuiltObjectId = built.Id, Quality = QualityOf(task) };
        _inhabitants.Report(new WorldEvent(Built, e.Tick, task.InhabitantId, null, built.Id));
        KnowOwnWork(e.Tick, task);
    }

    /// <summary>
    /// What the makers of a thing know of it once it stands (D9). <b>Whoever built it</b> knows by its own
    /// doing that it is made: what every invention is by being made (the data's implied properties), as
    /// experience, by the same path as any experience (<see cref="InhabitantsSystem.Experience"/>; the
    /// doing is the building step). The building task knows one builder; there are no helpers yet.
    /// <b>Whoever had the idea</b> (the inventor, whose wish it came from) has its own idea of what the
    /// thing is: the properties the invention introduces, as guesses - as sure as a guess can be, never
    /// over a belief it has (<see cref="InhabitantsSystem.Guess"/>). Whether the thing is what the
    /// inventor thinks and whether using it helps is the world's matter (true properties, effect rules);
    /// looking and trying replace the guesses like every other guess. Nobody else learns anything here.
    /// </summary>
    private void KnowOwnWork(long tick, BuildTask task)
    {
        var made = _rules.Settings.ImpliedProperties;
        _inhabitants.Experience(tick, _inhabitants.Get(task.InhabitantId), Build.Settings.StepAction, task.TypeId, made);
        var record = _inventions[task.TypeId];
        var inventor = _inhabitants.Get(record.InhabitantId);
        foreach (var property in Parse(record).Introduces.Except(made, StringComparer.Ordinal).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            _inhabitants.Guess(tick, inventor, null, task.TypeId, property, value: true);
        }
    }

    private static SoundDecision? SoundOf(InventionRecord record) =>
        record.SoundJson is null ? null : JsonSerializer.Deserialize<SoundDecision>(record.SoundJson, RecordJson);

    /// <summary>Asks the sound model for the invention's own sound (it runs in the compute night, B7 S4).</summary>
    private void SubmitSound(World world, BuildTask task, SoundDecision decision, int attempt = 1)
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
        var json = ContractSerializer.Serialize(job, ValidateRequests);
        Log.Add(new DecisionLogEntry(DecisionLogKind.JobRequest, requestId, tick, json, null));
        _port!.Submit(requestId, json);
        _sounds[task.TypeId] = new SoundTask(task.TypeId, requestId, null, false, attempt);
    }

    /// <summary>
    /// A1564 (B7 R7): asks the image model for an icon of a new invention - what it is and what it is made of, in
    /// the data's style and size; the job runs in the compute night like every image job. Without the data nothing.
    /// </summary>
    private void SubmitImage(World world, string typeId, int attempt = 1)
    {
        if (Build.Image is not { InventionIcons: { } icons } image || !_inventions.TryGetValue(typeId, out var record))
        {
            return;
        }

        _counter++;
        var tick = world.Tick;
        var requestId = DecisionIds.RequestId(world.Seed, tick, record.InhabitantId + "/image", _counter);
        var job = new GenerationJob
        {
            ContractVersion = ContractInfo.Version,
            RequestId = requestId,
            Tick = tick,
            Seed = SeedOf(requestId),
            DisplayLocale = Enum.Parse<Locale>(_displayLocale, ignoreCase: true),
            Kind = JobKind.Image,
            Priority = image.Priority,
            Inhabitant = record.InhabitantId,
            Object = null,
            Payload = new JobPayload
            {
                Image = new ImagePayload
                {
                    Subject = PictureSubject(Parse(record)),
                    Purpose = ImagePayloadPurpose.Icon,
                    Style = icons.Style,
                    Width = image.Sizes.Icon.Width,
                    Height = image.Sizes.Icon.Height,
                    ObjectType = typeId,
                },
            },
        };
        var json = ContractSerializer.Serialize(job, ValidateRequests);
        Log.Add(new DecisionLogEntry(DecisionLogKind.JobRequest, requestId, tick, json, null));
        _port!.Submit(requestId, json);
        _images[typeId] = new ImageTask(typeId, requestId, null, false, attempt);
    }

    /// <summary>
    /// What the picture shows, in English (the image model's language): the invention's name and what it is made of.
    /// A material that is itself an invention is named by its name; the data's types by their id in words.
    /// </summary>
    private string PictureSubject(Invention invention)
    {
        string Word(string typeId) =>
            _inventions.TryGetValue(typeId, out var made) ? Parse(made).Name.En : typeId.Replace('_', ' ');
        var materials = invention.Materials.Select(Word).Distinct(StringComparer.Ordinal).ToList();
        var subject = materials.Count == 0 ? invention.Name.En : $"{invention.Name.En}, made of {string.Join(", ", materials)}";
        return subject.Length <= 300 ? subject : subject[..300];
    }

    /// <summary>At the start of a compute night, waiting image jobs go out again with a new request id (A1564).</summary>
    private void RetryImagesAtNight(World world)
    {
        foreach (var waiting in _images.Values.Where(i => i.RetryAtNight).ToList())
        {
            SubmitImage(world, waiting.TypeId, waiting.Attempts + 1);
        }
    }

    /// <summary>Routes a job outcome to an image job; false if it belongs to none (A1564).</summary>
    private bool ApplyImage(World world, PortMessage message)
    {
        var image = _images.Values.FirstOrDefault(i => i.RequestId == message.RequestId && i.ResultJson is null && !i.Failed && !i.RetryAtNight);
        if (image is null)
        {
            return false;
        }

        JobStatus? status = null;
        var retryable = message.IsError && IsRetryable(message.Json);
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

        if (status is { State: JobState.Done, Kind: JobKind.Image, Result.Image: { } result } && status.JobId == image.RequestId)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobResult, message.RequestId, world.Tick, message.Json, null));
            _images[image.TypeId] = image with { ResultJson = JsonSerializer.Serialize(result, RecordJson) };
            return true;
        }

        if (status is { State: JobState.Failed, Error: { } error })
        {
            retryable = error.Retryable;
        }

        Log.Add(new DecisionLogEntry(message.IsError ? DecisionLogKind.JobError : DecisionLogKind.JobDiscarded, message.RequestId, world.Tick, message.Json, message.IsError ? ReplayNotes.OfError(message.Json) : "image_failed"));

        // A retryable failure is tried again in the next compute night (up to the data limit); anything else is
        // final: the thing keeps the colours of the construction kit.
        _images[image.TypeId] = retryable && image.Attempts < (Build.Image?.InventionIcons?.MaxAttempts ?? 1)
            ? image with { RetryAtNight = true }
            : image with { Failed = true };
        return true;
    }

    /// <summary>At the start of a compute night, waiting sound jobs go out again with a new request id.</summary>
    private void RetrySoundsAtNight(World world)
    {
        foreach (var waiting in _sounds.Values.Where(s => s.RetryAtNight).ToList())
        {
            var task = _builds[waiting.TypeId];
            if (SoundOf(_inventions[waiting.TypeId]) is { Needed: true } decision)
            {
                SubmitSound(world, task, decision, waiting.Attempts + 1);
            }
        }
    }

    /// <summary>Routes a job outcome to a sound job; false if it belongs to none.</summary>
    private bool ApplySound(World world, PortMessage message)
    {
        var sound = _sounds.Values.FirstOrDefault(s => s.RequestId == message.RequestId && s.ResultJson is null && !s.Failed && !s.RetryAtNight);
        if (sound is null)
        {
            return false;
        }

        JobStatus? status = null;
        var retryable = message.IsError && IsRetryable(message.Json);
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

        if (status is { State: JobState.Failed, Error: { } error })
        {
            retryable = error.Retryable;
        }

        Log.Add(new DecisionLogEntry(message.IsError ? DecisionLogKind.JobError : DecisionLogKind.JobDiscarded, message.RequestId, world.Tick, message.Json, message.IsError ? ReplayNotes.OfError(message.Json) : "sound_failed"));

        // A retryable failure is tried again in the next compute night (up to the data limit); anything else
        // is final: the invention keeps no sound of its own and the game takes the base library (D7).
        _sounds[sound.TypeId] = retryable && sound.Attempts < Build.Settings.SoundMaxAttempts
            ? sound with { RetryAtNight = true }
            : sound with { Failed = true };
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

        // The first signal of a new or loaded world is always sent, even if the port saw this phase before.
        var first = _night is null;
        _night = night;
        _port!.SetPhase(night ? JobPhase.Night : JobPhase.Day, world.Tick, force: first);
        if (night && !first)
        {
            RetrySoundsAtNight(world);
            RetryImagesAtNight(world);
        }
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

            hasher.Write(b.PendingObjectId ?? string.Empty).Write(b.PendingType ?? string.Empty).Write(b.BuiltObjectId ?? string.Empty).Write(b.GaveUp)
                .Write(b.WaitingSinceTick ?? -1).Write(b.NeedsHelp).Write(b.LookedTicks);
            if (b.Quality is { } quality)
            {
                // A1376 (R19), only when there is one: worlds without the data hash as before.
                hasher.Write(quality);
            }

            if (b.Helper is { } helpedBy)
            {
                // A1376, only when there is a helper.
                hasher.Write(helpedBy).Write(b.HelperPendingObjectId ?? string.Empty).Write(b.HelperPendingType ?? string.Empty);
            }

            if (b.Escalations > 0 || b.Smaller || b.Chosen || b.Substitutes is { Count: > 0 })
            {
                // A1373 step 2, only when used: worlds without escalations hash as before.
                hasher.Write(b.Escalations).Write(b.Smaller).Write(b.Chosen).Write(b.Substitutes?.Count ?? 0);
                foreach (var (planType, substitute) in (b.Substitutes ?? new Dictionary<string, string>()).OrderBy(s => s.Key, StringComparer.Ordinal))
                {
                    hasher.Write(planType).Write(substitute);
                }
            }
        }

        hasher.Write(_firstUsed.Count);
        foreach (var t in _firstUsed)
        {
            hasher.Write(t);
        }

        hasher.Write(_sounds.Count);
        foreach (var s in _sounds.Values)
        {
            hasher.Write(s.TypeId).Write(s.RequestId).Write(s.ResultJson ?? string.Empty).Write(s.Failed).Write(s.Attempts).Write(s.RetryAtNight);
        }

        // A1564, only when there are picture jobs: worlds without them hash as before.
        if (_images.Count > 0)
        {
            hasher.Write(_images.Count);
            foreach (var i in _images.Values)
            {
                hasher.Write(i.TypeId).Write(i.RequestId).Write(i.ResultJson ?? string.Empty).Write(i.Failed).Write(i.Attempts).Write(i.RetryAtNight);
            }
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

        foreach (var image in state.Images ?? [])
        {
            _images[image.TypeId] = image;
        }
    }
}
