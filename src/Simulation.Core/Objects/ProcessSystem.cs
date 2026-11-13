using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Objects;

/// <summary>A try the world has judged (B2537, D471 P3): feasible with its rule, or 'nothing happens'.</summary>
/// <param name="Id">World rule id from the try's signature ('world_' + 16 hex).</param>
/// <param name="Feasible">Whether something happens.</param>
/// <param name="Rule">The rule as JSON (contract ProcessRule) when feasible, else null.</param>
/// <param name="RequestId">The job that judged it; empty when the core knew without asking (no candidates).</param>
/// <param name="Tick">When it became world truth.</param>
/// <param name="TechLevel">The world's tech level (position) it was judged at (review GW1 #356): a 'nothing happens' of a lower level is judged again once the world is higher. Null = judged before the level could rise.</param>
public sealed record WorldProcessRule(string Id, bool Feasible, string? Rule, string RequestId, long Tick, int? TechLevel = null);

/// <summary>A job that is judging a try now; at most one per signature (review GW1 #23025).</summary>
/// <param name="Id">World rule id of the try.</param>
/// <param name="RequestId">The job's request id.</param>
/// <param name="Tick">When it was sent.</param>
/// <param name="Inputs">The true property sets of the things, in canonical order (to check the answer).</param>
/// <param name="Candidates">The processes the judge may choose from.</param>
/// <param name="TechLevel">The world's tech level (position) when it was sent; null without milestones.</param>
/// <param name="Heat">The heat level at the try (position, B2700), or null.</param>
public sealed record PendingProcess(string Id, string RequestId, long Tick, IReadOnlyList<IReadOnlyList<string>> Inputs, IReadOnlyList<string> Candidates, int? TechLevel = null, int? Heat = null);

/// <summary>A changed thing's type (a processed variant of a base type): its root type gives its look.</summary>
/// <param name="Id">Variant type id.</param>
/// <param name="Root">The catalog or invention type it was made from (first in a chain of changes).</param>
/// <param name="Properties">Its true properties, sorted.</param>
public sealed record ProcessVariant(string Id, string Root, IReadOnlyList<string> Properties);

/// <summary>The processing state of a world as saved.</summary>
public sealed record ProcessState(long Counter, IReadOnlyList<WorldProcessRule> Rules, IReadOnlyList<PendingProcess> Pending, IReadOnlyList<ProcessVariant> Variants);

/// <summary>
/// B2687 K2 (A1451 part 2 = D471 P3, plan D932): what comes of putting two things together when no
/// combination rule fits. A data processing rule that fits applies; else the world's own judged rule for
/// exactly this try (signature from the things' true properties) applies; else the try is judged once by
/// the AI (job <c>process</c>, judge v2: process null + candidates) and the answer - also 'nothing happens' -
/// becomes world truth for good. Until the answer arrives the try gives nothing (no waiting). A change of
/// properties makes the thing a variant type (registered in the world's type truth, its root gives its
/// look); a produced thing comes into the world at the actor. Deterministic: no randomness, the order of
/// rules and things is fixed, the job's answer is replayed from the log.
/// </summary>
public sealed class ProcessSystem : IWorldSystem, IOptionalWorldState
{
    /// <summary>The world's milestones and reached tech level (A1625); null = the start level of the data.</summary>
    public Inventions.MilestoneSystem? Milestones { get; set; }

    private readonly ProcessRuleSet _data;
    private readonly WorldTypeCatalog _types;
    private readonly WorldObjects _objects;
    private readonly InhabitantsSystem _inhabitants;
    private readonly IJobPort? _port;
    private readonly int _jobPriority;
    private readonly string _displayLocale;
    private readonly SortedDictionary<string, WorldProcessRule> _rules = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, PendingProcess> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingByRequest = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, ProcessVariant> _variants = new(StringComparer.Ordinal);
    private long _counter;

    /// <summary>Creates the system; <paramref name="jobs"/> null = no AI (unknown tries give nothing and are not stored).</summary>
    public ProcessSystem(ProcessRuleSet data, WorldTypeCatalog types, WorldObjects objects, InhabitantsSystem inhabitants, JobRouter? jobs, DecisionLog log, int jobPriority, string displayLocale)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(inhabitants);
        ArgumentNullException.ThrowIfNull(log);
        _data = data;
        _types = types;
        _objects = objects;
        _inhabitants = inhabitants;
        Log = log;
        _jobPriority = jobPriority;
        _displayLocale = displayLocale;
        _port = jobs?.Claim(id => _pendingByRequest.ContainsKey(id));
    }

    /// <summary>The heat level a thing gives as a heat source (a burning fire, B2700), position in the table, or null.</summary>
    public Func<WorldObject, int?>? HeatOf { get; set; }

    /// <summary>The highest heat level of burning things within reach of a place for an action (B2700), or null.</summary>
    public Func<Position, string, int?>? HeatNear { get; set; }

    /// <summary>Properties every made thing has (content/invention/settings.json implied_properties: crafted, B2700).</summary>
    public IReadOnlyList<string> Implied { get; init; } = [];

    /// <summary>The world's one job log (shared with the inventions).</summary>
    public DecisionLog Log { get; }

    /// <summary>Whether requests are checked against the contract before they are sent.</summary>
    public bool ValidateRequests { get; init; } = true;

    /// <inheritdoc/>
    public string Name => "processes";

    /// <inheritdoc/>
    public bool HasState => _rules.Count > 0 || _pending.Count > 0 || _variants.Count > 0;

    /// <summary>The tries the world has judged, in id order.</summary>
    public IEnumerable<WorldProcessRule> Rules => _rules.Values;

    /// <summary>The tries being judged now, in id order.</summary>
    public IEnumerable<PendingProcess> Pending => _pending.Values;

    /// <summary>The root type of a variant (its look), or null for a type that is no variant.</summary>
    public string? RootOf(string typeId) => _variants.TryGetValue(typeId, out var variant) ? variant.Root : null;

    /// <summary>
    /// The try with the target and its partner (no combination rule fitted). Returns whether anything
    /// came of it now.
    /// </summary>
    public bool Try(World world, Inhabitant inhabitant, string action, IReadOnlyList<WorldObject> things)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(inhabitant);
        ArgumentNullException.ThrowIfNull(things);
        if (things.Count != 2)
        {
            return false;
        }

        var truth = things.Select(t => _types.TrueProperties(t.TypeId)).ToList();

        // B2700: the heat at the try - a burning thing put together with the other, or burning near the target.
        int? heatSource = null;
        int? heat = HeatNear?.Invoke(things[0].Position, action);
        for (var i = 0; i < things.Count; i++)
        {
            if (HeatOf?.Invoke(things[i]) is { } own)
            {
                heatSource ??= i;
                heat = Math.Max(heat ?? own, own);
            }
        }

        if (_data.Match(truth, Milestones?.TechLevelIndex, heat, heatSource) is { } known)
        {
            return Apply(world, inhabitant, action, known, things, truth);
        }

        // With the fire as one of the two, the try is what happens to the other thing in that heat.
        List<FrozenSet<string>> tried = heatSource is { } src ? [truth[1 - src]] : truth;
        var heatLevel = heat is { } hl ? _data.HeatLevels[hl] : null;
        var id = ProcessSignatures.RuleId(ProcessSignatures.CombineTrial, tried, null, heatLevel);
        if (_rules.TryGetValue(id, out var judged))
        {
            if (!judged.Feasible || judged.Rule is null)
            {
                // 'Nothing happens' holds at the level it was judged at; a world that has risen since asks again.
                if (judged.TechLevel is not { } level || Milestones?.TechLevelIndex is not { } now || now <= level)
                {
                    return false;
                }

                _rules.Remove(id);
            }
            else if (Stored(judged.Rule) is { } rule)
            {
                return _data.Applies(rule, Milestones?.TechLevelIndex, heat) && ProcessRuleSet.Fit(rule, truth, heatSource) is { } match && Apply(world, inhabitant, action, match, things, truth);
            }

            else
            {
                // A stored rule this core cannot read any more (a later change of the rule's form, review GW1
                // #349): it no longer counts, the try is judged again.
                _rules.Remove(id);
            }
        }

        if (_pending.ContainsKey(id) || _port is null)
        {
            // Being judged (one job per try), or no AI: nothing this time.
            return false;
        }

        var candidates = _data.Candidates(things.Count, Milestones?.TechLevelIndex, heat, heatSource is not null);
        if (candidates.Count == 0)
        {
            // Nothing could happen to such things at this level: nothing now, nothing kept (review GW1 #356) -
            // the same try asks once the world has risen and a process fits.
            return false;
        }

        Submit(world, inhabitant, id, heatSource is { } fire ? new[] { things[1 - fire] } : things, tried, candidates, heat);
        return false;
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
            Receive(world, message);
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_counter).Write(_rules.Count);
        foreach (var rule in _rules.Values)
        {
            hasher.Write(rule.Id).Write(rule.Feasible).Write(rule.Rule ?? string.Empty).Write(rule.RequestId).Write(rule.Tick);
            if (rule.TechLevel is { } level)
            {
                hasher.Write("tech").Write(level);
            }
        }

        hasher.Write(_pending.Count);
        foreach (var pending in _pending.Values)
        {
            hasher.Write(pending.Id).Write(pending.RequestId).Write(pending.Tick).Write(string.Join('|', pending.Candidates));
            if (pending.TechLevel is { } level)
            {
                hasher.Write("tech").Write(level);
            }
        }

        // A variant's truth is hashed with it (review GW1 #23025): a changed rule file cannot give one id another truth unnoticed.
        hasher.Write(_variants.Count);
        foreach (var variant in _variants.Values)
        {
            hasher.Write(variant.Id).Write(variant.Root).Write(string.Join(',', variant.Properties));
        }
    }

    /// <summary>The state for a save game.</summary>
    public ProcessState Capture() => new(_counter, [.. _rules.Values], [.. _pending.Values], [.. _variants.Values]);

    /// <summary>Restores a saved state; the variants come back into the world's type truth.</summary>
    public void Restore(ProcessState? state)
    {
        if (state is null)
        {
            return;
        }

        _counter = state.Counter;
        foreach (var variant in state.Variants)
        {
            _types.Register(variant.Id, variant.Properties);
            _variants[variant.Id] = variant;
        }

        foreach (var rule in state.Rules)
        {
            _rules[rule.Id] = rule;
        }

        foreach (var pending in state.Pending)
        {
            _pending[pending.Id] = pending;
            _pendingByRequest[pending.RequestId] = pending.Id;
        }
    }

    private bool Apply(World world, Inhabitant inhabitant, string action, ProcessMatch match, IReadOnlyList<WorldObject> things, List<FrozenSet<string>> truth)
    {
        var rule = match.Rule;
        var result = rule.Result;
        var consumed = result.Consumes.Distinct().Select(i => match.Inputs[i]).ToHashSet();
        var changed = false;

        // The first input changes (add/remove) unless it is used up for a produced thing (contract ProcessOutcome).
        var first = match.Inputs[0];
        if (!(consumed.Contains(first) && result.Produces is not null))
        {
            var properties = truth[first].Union(result.AddProperties, StringComparer.Ordinal).Except(result.RemoveProperties, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToList();
            if (!properties.SequenceEqual(truth[first].Order(StringComparer.Ordinal), StringComparer.Ordinal) && !consumed.Contains(first))
            {
                // B2700: what was processed is made - it carries the implied properties (crafted) like an invention.
                properties = [.. properties.Union(Implied.Where(_types.PropertyVocabulary.Contains), StringComparer.Ordinal).Order(StringComparer.Ordinal)];
                var thing = things[first];
                var variant = VariantFor(thing.TypeId, rule.Id, properties);
                _objects.ChangeType(thing.Id, variant);
                _inhabitants.Experience(world.Tick, inhabitant, action, variant, _types.TrueProperties(variant));
                _inhabitants.Report(new WorldEvent(InhabitantsSystem.Combined, world.Tick, inhabitant.Id, action, thing.Id) { TargetType = variant, Detail = rule.Id });
                changed = true;
            }
        }

        foreach (var index in consumed.Order())
        {
            _objects.Remove(things[index].Id);
            changed = true;
        }

        if (result.Produces is { } produces && _types.Contains(produces))
        {
            // At the place of the thing worked on (things[first]), not under the actor's feet (GW3 D990).
            var made = _objects.Spawn(produces, things[first].Position);
            _inhabitants.Experience(world.Tick, inhabitant, action, made.TypeId, _types.TrueProperties(made.TypeId));
            _inhabitants.Report(new WorldEvent(InhabitantsSystem.Combined, world.Tick, inhabitant.Id, action, made.Id) { TargetType = made.TypeId, Detail = rule.Id });
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// The variant type of a changed thing: id from its type and the rule (the same change of the same
    /// type is one type in every world), root = the first type of the chain.
    /// </summary>
    private string VariantFor(string typeId, string ruleId, IReadOnlyList<string> properties)
    {
        var root = RootOf(typeId) ?? typeId;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(typeId + "|" + ruleId));
        var id = root.Length > 48 ? root[..48] : root;
        id += "_v" + Convert.ToHexStringLower(hash.AsSpan(0, 6));
        if (_variants.TryGetValue(id, out var known))
        {
            if (!known.Properties.SequenceEqual(properties, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Variant '{id}' exists with other properties.");
            }

            return id;
        }

        _types.Register(id, properties);
        _variants[id] = new ProcessVariant(id, root, properties);
        return id;
    }

    private void Submit(World world, Inhabitant inhabitant, string id, IReadOnlyList<WorldObject> things, List<FrozenSet<string>> truth, IReadOnlyList<string> candidates, int? heat)
    {
        _counter++;
        var requestId = DecisionIds.RequestId(world.Seed, world.Tick, inhabitant.Id + "/process", _counter);
        var canonical = ProcessSignatures.CanonicalInputs(truth);
        var job = new GenerationJob
        {
            ContractVersion = ContractInfo.Version,
            RequestId = requestId,
            Tick = world.Tick,
            Seed = DecisionIds.SeedOf(requestId),
            DisplayLocale = Enum.Parse<Locale>(_displayLocale, ignoreCase: true),
            Kind = JobKind.Process,
            Priority = _jobPriority,
            Inhabitant = inhabitant.Id,
            Object = null,
            Payload = new JobPayload
            {
                Process = new ProcessPayload
                {
                    Process = null,
                    Inputs = [.. things.Select(t => new ObservedThing
                    {
                        Type = t.TypeId,
                        Observed = [.. inhabitant.Beliefs.About(t.TypeId).Where(b => b.Belief.Value).Select(b => b.PropertyId).Order(StringComparer.Ordinal).Take(12)],
                    })],
                    Tool = null,
                    HeatLevel = heat is { } h ? _data.HeatLevels[h] : null,
                    TechLevel = Milestones?.TechLevel ?? _data.TechLevel,
                    Inhabitant = InhabitantStates.Of(inhabitant, null, [], [], _inhabitants.LabelOf, needLabel: _inhabitants.NeedLabelOf),
                    Signature = new ProcessSignature
                    {
                        Process = ProcessSignatures.CombineTrial,
                        Inputs = [.. canonical.Select(i => (ICollection<string>)[.. i])],
                        Tool = null,
                        HeatLevel = heat is { } sh ? _data.HeatLevels[sh] : null,
                    },
                    Candidates = [.. candidates],
                },
            },
        };
        var json = ContractSerializer.Serialize(job, ValidateRequests);
        Log.Add(new DecisionLogEntry(DecisionLogKind.JobRequest, requestId, world.Tick, json, null));
        var pending = new PendingProcess(id, requestId, world.Tick, [.. canonical.Select(i => (IReadOnlyList<string>)i)], candidates, Milestones?.TechLevelIndex, heat);
        _pending[id] = pending;
        _pendingByRequest[requestId] = id;
        _port!.Submit(requestId, json);
    }

    private void Receive(World world, PortMessage message)
    {
        if (!_pendingByRequest.Remove(message.RequestId, out var id) || !_pending.Remove(id, out var pending))
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobDiscarded, message.RequestId, world.Tick, message.Json, ReplayNotes.QuestionClosed));
            return;
        }

        if (message.IsError)
        {
            // An error (also 'needs judge v2' from an older service) is no answer: a later try asks again.
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

        if (status.JobId != pending.RequestId || status.Kind != JobKind.Process || status.State != JobState.Done || status.Result?.Process is not { } result)
        {
            Log.Add(new DecisionLogEntry(DecisionLogKind.JobError, message.RequestId, world.Tick, message.Json, status.Error?.Code.ToString() ?? "not_final"));
            return;
        }

        var reasons = Check(result, pending);
        Log.Add(new DecisionLogEntry(DecisionLogKind.JobResult, message.RequestId, world.Tick, message.Json, reasons.Count == 0 ? null : "rejected:" + string.Join(',', reasons)));

        // A rejected answer counts as 'nothing happens': the world does not ask the same again (the rule checker decided).
        var feasible = result.Feasible && reasons.Count == 0;
        _rules[id] = new WorldProcessRule(id, feasible, feasible ? System.Text.Json.JsonSerializer.Serialize(result.Rule!) : null, pending.RequestId, world.Tick, pending.TechLevel);
    }

    /// <summary>A stored world rule, or null when it cannot be read as a rule of this contract.</summary>
    private static ProcessRule? Stored(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<ProcessRule>(json) is { Inputs.Count: > 0, Result: not null } rule ? rule : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The core's own check of a judged rule (D471 P3 step 4): it must fit the try it answers.</summary>
    private List<string> Check(ProcessResult result, PendingProcess pending)
    {
        var reasons = new List<string>();
        if (!result.Feasible)
        {
            return reasons;
        }

        if (result.Rule is not { } rule)
        {
            reasons.Add("no_rule");
            return reasons;
        }

        if (rule.Id != pending.Id)
        {
            reasons.Add("rule_id");
        }

        if (!pending.Candidates.Contains(rule.Process, StringComparer.Ordinal))
        {
            reasons.Add("not_a_candidate");
        }

        if (!_data.Applies(rule, Milestones?.TechLevelIndex, pending.Heat))
        {
            reasons.Add("heat_or_tech_level");
        }

        var vocabulary = _types.PropertyVocabulary;
        if (rule.Result.AddProperties.Concat(rule.Result.RemoveProperties).Any(p => !vocabulary.Contains(p)))
        {
            reasons.Add("unknown_property");
        }

        if (rule.Result.Produces is { } produces && !_types.Contains(produces))
        {
            reasons.Add("unknown_type");
        }

        if (rule.Result.Consumes.Any(i => i >= rule.Inputs.Count))
        {
            reasons.Add("consumes");
        }

        var truth = pending.Inputs.Select(i => i.ToFrozenSet(StringComparer.Ordinal)).ToList();
        if (truth.Count == 1 ? !ProcessRuleSet.FitsOne(rule, truth[0]) : ProcessRuleSet.Fit(rule, truth) is null)
        {
            reasons.Add("does_not_fit");
        }

        return reasons;
    }

    /// <summary>A short id for logs.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"processes: {_rules.Count} judged, {_pending.Count} pending, {_variants.Count} variants");
}
