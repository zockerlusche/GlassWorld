using System.Collections.Frozen;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Behaviour;

/// <summary>An option the routine layer can choose.</summary>
/// <param name="Action">Action id.</param>
/// <param name="TargetId">Id of the target (an object, or another inhabitant for a consideration with target_inhabitant) or null.</param>
/// <param name="Score">Utility score (higher is better).</param>
public sealed record ScoredOption(string Action, string? TargetId, double Score)
{
    /// <summary>For a search (looking further away): the search key (need, else action) it serves; null otherwise.</summary>
    public string? SearchFor { get; init; }
}

/// <summary>
/// An option as the brain scores it, before any order (<see cref="UtilityBrain.Candidates"/>): the same
/// values as a <see cref="ScoredOption"/>, as a value, so choosing among thousands of them allocates nothing.
/// </summary>
/// <param name="Action">Action id.</param>
/// <param name="TargetId">Id of the target or null.</param>
/// <param name="Score">Utility score (higher is better).</param>
/// <param name="SearchFor">For a search: the search key it serves; null otherwise.</param>
public readonly record struct Candidate(string Action, string? TargetId, double Score, string? SearchFor = null);

/// <summary>
/// The routine layer (utility AI): scores the options the core allows with weights and curves from data
/// (content/behaviour/utility.json). It sees only the inhabitant's BELIEFS, never the truth. Ties are
/// broken by option order (action, then target id), so the choice is deterministic.
/// </summary>
public sealed class UtilityBrain
{
    private readonly (Consideration Data, Curve Curve, Curve? Novelty)[] _considerations;
    private readonly IReadOnlyDictionary<string, double> _neglectBelow;
    private readonly Familiarity? _familiarity;
    private readonly ActionRules _actions;
    private readonly Curve? _runtimeCurve;

    /// <summary>
    /// Creates the brain and checks ids against the action rules and needs. A search action must be an
    /// action without a target (<paramref name="targetlessActions"/>, the vocabulary's target none; without
    /// it: an action no rule needs a target for) on a consideration with a target property or an inhabitant
    /// target. A trait must be one of <paramref name="traits"/> (when given); exploring (novelty_curve) yields
    /// to the needs in <paramref name="neglectBelow"/> (need id to neglect threshold; without it: to every
    /// need). A consideration with an inhabitant target (W1744) needs an action aimed at inhabitants
    /// (<paramref name="inhabitantActions"/>, the vocabulary's target inhabitant, when given) and neither a
    /// target property nor avoided properties nor a novelty curve; who is familiar and who is asleep comes
    /// from <paramref name="familiarity"/> (without it such a consideration offers nothing; the content
    /// check refuses that combination).
    /// </summary>
    public UtilityBrain(UtilityWeights weights, ActionRules actions, IReadOnlySet<string> needs, IReadOnlySet<string> properties, IReadOnlySet<string>? targetlessActions = null, IReadOnlySet<string>? traits = null, IReadOnlyDictionary<string, double>? neglectBelow = null, Familiarity? familiarity = null, IReadOnlySet<string>? inhabitantActions = null)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(actions);
        Weights = weights;
        _familiarity = familiarity;
        _actions = actions;
        _runtimeCurve = weights.RuntimeNeed is { } runtime ? new Curve(runtime.Curve) : null;
        if (weights.SearchWeight > weights.UnknownTargetBelief)
        {
            // Trying something unknown close by is how inhabitants learn; searching far away must not outbid it.
            throw new InvalidDataException("Utility weights: search_weight must not exceed unknown_target_belief.");
        }

        _neglectBelow = neglectBelow ?? needs.ToDictionary(n => n, _ => 0.0, StringComparer.Ordinal);
        _considerations = [.. weights.Considerations.Select(c => (c, new Curve(c.Curve), c.NoveltyCurve is { } novelty ? new Curve(novelty) : null))];
        AimsAtInhabitants = _considerations.Any(c => c.Data.TargetInhabitant is not null);
        foreach (var (c, _, novelty) in _considerations)
        {
            if (novelty is not null && (c.TargetProperty is not null || c.SearchAction is not null))
            {
                throw new InvalidDataException($"Utility consideration for '{c.Action}': novelty_curve needs target_property and search_action null.");
            }

            if (c.Trait is { } trait && traits is not null && !traits.Contains(trait))
            {
                throw new InvalidDataException($"Utility consideration for '{c.Action}' uses an unknown trait '{trait}'.");
            }

            _ = actions.Timing(c.Action);
            if (c.TargetInhabitant is not null)
            {
                if (c.TargetProperty is not null || c.AvoidProperties.Count > 0 || novelty is not null)
                {
                    throw new InvalidDataException($"Utility consideration for '{c.Action}': target_inhabitant needs target_property and novelty_curve null and no avoid_properties.");
                }

                if (inhabitantActions is not null && !inhabitantActions.Contains(c.Action))
                {
                    throw new InvalidDataException($"Utility consideration for '{c.Action}': target_inhabitant needs an action aimed at inhabitants (vocabulary target inhabitant).");
                }
            }

            if (c.SearchAction is { } search)
            {
                _ = actions.Timing(search);
                if ((c.TargetProperty is null && c.TargetInhabitant is null) || !(targetlessActions?.Contains(search) ?? !actions.NeedsTarget(search)))
                {
                    throw new InvalidDataException($"Utility consideration for '{c.Action}': search_action '{search}' needs a target_property or a target_inhabitant and must be an action without a target.");
                }
            }

            if (c.RequiresAlso is not null && (c.TargetProperty is null || c.SearchAction is not null))
            {
                throw new InvalidDataException($"Utility consideration for '{c.Action}': requires_also needs a target_property and no search_action.");
            }

            if ((c.Need is not null && !needs.Contains(c.Need))
                || (c.TargetProperty is not null && !properties.Contains(c.TargetProperty))
                || (c.RequiresAlso is not null && !properties.Contains(c.RequiresAlso))
                || c.AvoidProperties.Any(p => !properties.Contains(p)))
            {
                throw new InvalidDataException($"Utility consideration for '{c.Action}' uses an unknown need or property.");
            }
        }
    }

    /// <summary>The data in use.</summary>
    public UtilityWeights Weights { get; }

    /// <summary>
    /// What a thing is by being made (the invention data's implied properties, D9): unknown counts as not, never as
    /// the unknown-target belief - nobody takes a found thing for a made one (GW2 D971).
    /// </summary>
    public FrozenSet<string> MadeProperties { get; internal set; } = FrozenSet<string>.Empty;

    /// <summary>Whether any consideration aims at other inhabitants (target_inhabitant, W1744).</summary>
    public bool AimsAtInhabitants { get; }

    /// <summary>Who is familiar and who is asleep for considerations with an inhabitant target; null without the data.</summary>
    public Familiarity? Familiarity => _familiarity;

    /// <summary>
    /// Whether the inhabitant's needs suggest an action (F5275 A): an action with need-based considerations
    /// is suggested only while one of them gives it some urgency (weight x the need's curve above 0); an
    /// action without any is always suggested. An exploring consideration (novelty_curve) counts the same
    /// way: it suggests its action on <paramref name="targetTypeId"/> while its score there is above 0
    /// (curiosity, novelty, no pressing need; TL #19921). Data only - no action names in code.
    /// </summary>
    public bool NeedSuggests(Inhabitant inhabitant, string action, string? targetTypeId = null)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        var needBased = false;
        foreach (var (c, curve, novelty) in _considerations)
        {
            if (!string.Equals(c.Action, action, StringComparison.Ordinal))
            {
                continue;
            }

            if (novelty is not null)
            {
                needBased = true;
                if (targetTypeId is not null && Exploring(inhabitant, c, curve, novelty, targetTypeId) > 0)
                {
                    return true;
                }

                continue;
            }

            // GW2 D971: a consideration about a made thing suggests nothing for a thing the inhabitant does not know
            // to be made (use is not offered on clay).
            if (targetTypeId is not null && !MadeAsBelieved(inhabitant, c, targetTypeId))
            {
                needBased = true;
                continue;
            }

            // A consideration that yields to the needs (yields_to_needs, B2-6 R14) counts like a need-based
            // one: it suggests its action only while the needs leave room for it.
            var yields = c.YieldsToNeeds ?? false;
            if (c.Need is null && !yields)
            {
                continue;
            }

            needBased = true;
            if (c.Weight * curve.At(c.Need is null ? 1.0 : inhabitant.Needs.GetValueOrDefault(c.Need, 1.0)) * TraitFactor(inhabitant, c) * (yields ? Calm(inhabitant) : 1.0) > 0)
            {
                return true;
            }
        }

        return !needBased;
    }

    /// <summary>Whether the inhabitant believes the target is made, where the consideration asks for it (true otherwise).</summary>
    private bool MadeAsBelieved(Inhabitant inhabitant, Consideration c, string typeId) =>
        new[] { c.TargetProperty, c.RequiresAlso }.All(p => p is null || !MadeProperties.Contains(p)
            || (inhabitant.Beliefs.TryGet(typeId, p, out var belief) && belief.Value));

    /// <summary>Whether an action serves a need: one of its considerations names that need (data).</summary>
    public bool Serves(string action, string need) =>
        _considerations.Any(c => string.Equals(c.Data.Action, action, StringComparison.Ordinal) && string.Equals(c.Data.Need, need, StringComparison.Ordinal));

    /// <summary>The key a search is kept under (its need, else its action).</summary>
    public static string SearchKey(Consideration consideration)
    {
        ArgumentNullException.ThrowIfNull(consideration);
        return consideration.Need ?? consideration.Action;
    }

    /// <summary>All options with their scores, best first; ties in option order.</summary>
    public IReadOnlyList<ScoredOption> Score(Inhabitant inhabitant, IReadOnlyList<WorldObject> perceived) =>
        Score(inhabitant, perceived, new HashSet<string>(StringComparer.Ordinal), null);

    /// <summary>
    /// Working memory of <see cref="Candidates"/>, kept by the caller from call to call (one per world: the
    /// brain itself is content and may serve several worlds at once). The list a call returns is this
    /// scratch's and holds until the next call with it.
    /// </summary>
    public sealed class Scratch
    {
        internal List<Candidate> Options { get; } = [];

        internal Dictionary<string, int> Types { get; } = new(StringComparer.Ordinal);

        internal List<string> TypeIds { get; } = [];

        internal int[] TypeOf { get; set; } = [];

        internal double[] Near { get; set; } = [];

        internal double[] PerType { get; set; } = [];
    }

    /// <summary>
    /// The order of the options: higher score first, then action, then target id (none first), then the
    /// order they were made in. A total order, so "the best" is the same whether one sorts or looks for it.
    /// </summary>
    public static int Compare(in Candidate a, int madeA, in Candidate b, int madeB)
    {
        var order = b.Score.CompareTo(a.Score);
        if (order == 0)
        {
            order = string.CompareOrdinal(a.Action, b.Action);
        }

        if (order == 0)
        {
            order = string.CompareOrdinal(a.TargetId ?? string.Empty, b.TargetId ?? string.Empty);
        }

        return order != 0 ? order : madeA.CompareTo(madeB);
    }

    /// <summary>
    /// All options with their scores, best first. A consideration with a search action also offers looking
    /// further away while the inhabitant believes some kind of thing helps, scored urgency x surest such
    /// belief x search_weight, so a weak target in sight competes fairly. Searches in
    /// <paramref name="givenUp"/> are not offered; <paramref name="searching"/> receives the keys whose
    /// search is still called for (need not met, nothing in sight that seems as good as searching).
    /// <para>
    /// A consideration with an inhabitant target (W1744) offers its action on every inhabitant of
    /// <paramref name="inSight"/> (the others the inhabitant sees and can walk to) that it is familiar with
    /// and that is awake, scored urgency x distance factor and only while that is above 0. Its search action
    /// is offered (urgency x search_weight) while the inhabitant knows someone familiar and none of them is
    /// there: in sight with the action offered on them, or in sight and asleep (nobody walks off from a
    /// sleeping partner).
    /// </para>
    /// </summary>
    public IReadOnlyList<ScoredOption> Score(Inhabitant inhabitant, IReadOnlyList<WorldObject> perceived, IReadOnlySet<string> givenUp, ISet<string>? searching, IReadOnlyList<Inhabitant>? inSight = null)
    {
        var options = Candidates(inhabitant, perceived, givenUp, searching, inSight, new Scratch());
        var order = new int[options.Count];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (x, y) => Compare(options[x], x, options[y], y));
        var sorted = new ScoredOption[order.Length];
        for (var i = 0; i < order.Length; i++)
        {
            var option = options[order[i]];
            sorted[i] = new ScoredOption(option.Action, option.TargetId, option.Score) { SearchFor = option.SearchFor };
        }

        return sorted;
    }

    /// <summary>
    /// The options of <see cref="Score(Inhabitant, IReadOnlyList{WorldObject}, IReadOnlySet{string}, ISet{string}?, IReadOnlyList{Inhabitant}?)"/>
    /// with the same scores, in the order they are made (consideration by consideration, thing by thing),
    /// not sorted: the routine needs the best one, found with <see cref="Compare"/>, not the whole order.
    /// <para>
    /// What a consideration asks about a thing in sight depends on the thing's type and its distance only,
    /// so both are worked out once (per type, per thing) and not once per consideration and thing: on the
    /// start island an inhabitant sees more than a thousand things of a few dozen kinds.
    /// </para>
    /// </summary>
    public List<Candidate> Candidates(Inhabitant inhabitant, IReadOnlyList<WorldObject> perceived, IReadOnlySet<string> givenUp, ISet<string>? searching, IReadOnlyList<Inhabitant>? inSight, Scratch scratch)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        ArgumentNullException.ThrowIfNull(perceived);
        ArgumentNullException.ThrowIfNull(givenUp);
        ArgumentNullException.ThrowIfNull(scratch);
        var options = scratch.Options;
        options.Clear();
        var types = scratch.Types;
        var typeIds = scratch.TypeIds;
        types.Clear();
        typeIds.Clear();
        if (scratch.TypeOf.Length < perceived.Count)
        {
            scratch.TypeOf = new int[Math.Max(perceived.Count, 2 * scratch.TypeOf.Length)];
            scratch.Near = new double[scratch.TypeOf.Length];
        }

        var typeOf = scratch.TypeOf;
        var near = scratch.Near;
        for (var i = 0; i < perceived.Count; i++)
        {
            var obj = perceived[i];
            if (!types.TryGetValue(obj.TypeId, out var type))
            {
                type = typeIds.Count;
                types.Add(obj.TypeId, type);
                typeIds.Add(obj.TypeId);
            }

            typeOf[i] = type;
            near[i] = DistanceFactor(inhabitant.Position, obj.Position);
        }

        if (scratch.PerType.Length < typeIds.Count)
        {
            scratch.PerType = new double[Math.Max(typeIds.Count, 2 * scratch.PerType.Length)];
        }

        var perType = scratch.PerType;
        foreach (var (c, curve, novelty) in _considerations)
        {
            if (novelty is not null)
            {
                // Exploring (TL #19921): every thing in sight, by how little the inhabitant knows of its kind.
                for (var type = 0; type < typeIds.Count; type++)
                {
                    perType[type] = Exploring(inhabitant, c, curve, novelty, typeIds[type]);
                }

                for (var i = 0; i < perceived.Count; i++)
                {
                    var score = perType[typeOf[i]] * near[i];
                    if (score > 0)
                    {
                        options.Add(new Candidate(c.Action, perceived[i].Id, score));
                    }
                }

                continue;
            }

            var urgency = c.Weight * curve.At(c.Need is null ? 1.0 : inhabitant.Needs.GetValueOrDefault(c.Need, 1.0)) * TraitFactor(inhabitant, c);
            if (c.YieldsToNeeds ?? false)
            {
                // B2-6 R14: gives way to pressing needs, like exploring by novelty.
                urgency *= Calm(inhabitant);
            }

            if (c.TargetInhabitant is not null)
            {
                Company(inhabitant, c, urgency, inSight ?? [], givenUp, searching, options);
                continue;
            }

            if (c.TargetProperty is null)
            {
                options.Add(new Candidate(c.Action, null, urgency));
                continue;
            }

            for (var type = 0; type < typeIds.Count; type++)
            {
                perType[type] = BeliefFactor(inhabitant, typeIds[type], c.TargetProperty) * AvoidFactor(inhabitant, typeIds[type], c.AvoidProperties) * AlsoFactor(inhabitant, typeIds[type], c.RequiresAlso);
            }

            var bestInSight = 0.0;
            for (var i = 0; i < perceived.Count; i++)
            {
                var belief = perType[typeOf[i]];
                if (belief > 0)
                {
                    var score = urgency * belief * near[i];
                    options.Add(new Candidate(c.Action, perceived[i].Id, score));
                    bestInSight = Math.Max(bestInSight, score);
                }
            }

            var known = SurestHelpfulBelief(inhabitant, c.TargetProperty, c.AvoidProperties);
            if (c.SearchAction is { } search && urgency > 0 && known > 0)
            {
                // The inhabitant knows of a kind of thing that would help: the need's urgency drives looking
                // for it further away (the data names the action), instead of waiting where nothing turns up.
                var key = SearchKey(c);
                var score = urgency * known * Weights.SearchWeight;
                if (score > bestInSight)
                {
                    searching?.Add(key);
                }

                if (!givenUp.Contains(key))
                {
                    options.Add(new Candidate(search, null, score, key));
                }
            }
        }

        return options;
    }

    /// <summary>
    /// A1372 (TL B2658): options for the needs inventions brought, after <see cref="Candidates"/>. For each such
    /// need the inhabitant has, its urgency (data runtime_need: weight x curve of its value, yielding to pressing
    /// needs if the data say so) for the need's first action with timing on every thing of the invention's type
    /// in sight, nearer scores higher. No search: only things in sight. Without the data nothing is added.
    /// </summary>
    public void AddRuntimeNeeds(Inhabitant inhabitant, IReadOnlyList<WorldObject> perceived, IReadOnlyList<Inhabitants.NeedInfo> needs, List<Candidate> options)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        ArgumentNullException.ThrowIfNull(perceived);
        ArgumentNullException.ThrowIfNull(needs);
        ArgumentNullException.ThrowIfNull(options);
        if (Weights.RuntimeNeed is not { } data || _runtimeCurve is null)
        {
            return;
        }

        foreach (var need in needs)
        {
            if (!inhabitant.Needs.TryGetValue(need.Id, out var value) || need.Actions.FirstOrDefault(_actions.HasTiming) is not { } action)
            {
                continue;
            }

            var urgency = data.Weight * _runtimeCurve.At(value) * (data.YieldsToNeeds ?? false ? Calm(inhabitant) : 1.0);
            if (urgency <= 0)
            {
                continue;
            }

            foreach (var obj in perceived)
            {
                if (string.Equals(obj.TypeId, need.SourceTypeId, StringComparison.Ordinal))
                {
                    options.Add(new Candidate(action, obj.Id, urgency * DistanceFactor(inhabitant.Position, obj.Position)));
                }
            }
        }
    }

    /// <summary>
    /// Options of a consideration with an inhabitant target (W1744): the action on every familiar, waking
    /// inhabitant in sight (nearer scores higher, ties by id), else the search action while the inhabitant
    /// knows someone familiar and sees none of them. Only its own relationships decide who is familiar.
    /// </summary>
    private void Company(Inhabitant inhabitant, Consideration c, double urgency, IReadOnlyList<Inhabitant> inSight, IReadOnlySet<string> givenUp, ISet<string>? searching, List<Candidate> options)
    {
        if (urgency <= 0 || _familiarity is not { } familiarity)
        {
            return;
        }

        var there = false;
        foreach (var other in inSight)
        {
            if (ReferenceEquals(other, inhabitant) || !familiarity.Familiar(inhabitant, other.Id))
            {
                continue;
            }

            // There = a sleeper (nobody walks off from a sleeping partner) or somebody the action is offered
            // on. Somebody at the edge of sight whose option scores 0 (distance_falloff 1) is neither: the
            // need must not wait without an option and without a search.
            var score = urgency * DistanceFactor(inhabitant.Position, other.Position);
            if (familiarity.Asleep(other))
            {
                there = true;
            }
            else if (score > 0)
            {
                there = true;
                options.Add(new Candidate(c.Action, other.Id, score));
            }
        }

        if (c.SearchAction is { } search && !there && familiarity.KnowsSomeone(inhabitant))
        {
            var key = SearchKey(c);
            searching?.Add(key);
            if (!givenUp.Contains(key))
            {
                options.Add(new Candidate(search, null, urgency * Weights.SearchWeight, key));
            }
        }
    }

    /// <summary>
    /// Belief that an object of <paramref name="typeId"/> has <paramref name="property"/>: the confidence
    /// of a positive belief, 0 for a negative belief, and the data value for "not known yet" (trying
    /// unknown things is how inhabitants learn without prior knowledge).
    /// </summary>
    public double BeliefFactor(Inhabitant inhabitant, string typeId, string property)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        return inhabitant.Beliefs.TryGet(typeId, property, out var belief)
            ? (belief.Value ? belief.Confidence : 0.0)
            : MadeProperties.Contains(property) ? 0.0 : Weights.UnknownTargetBelief;
    }

    /// <summary>
    /// How new a type is to the inhabitant: 1 - its surest belief about the type (any property, any
    /// source); 1 = it knows nothing about it, 0 = it is sure of something.
    /// </summary>
    public static double Novelty(Inhabitant inhabitant, string typeId)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        return 1.0 - inhabitant.Beliefs.Surest(typeId);
    }

    /// <summary>
    /// How calm the inhabitant's needs are for exploring: 0 while a need is below its neglect threshold,
    /// else 1 - the highest urgency (weight x curve) of a consideration for a need that can be neglected.
    /// </summary>
    public double Calm(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        var pressing = 0.0;
        foreach (var (c, curve, _) in _considerations)
        {
            if (c.Need is not { } need || !_neglectBelow.TryGetValue(need, out var below))
            {
                continue;
            }

            var value = inhabitant.Needs.GetValueOrDefault(need, 1.0);
            if (value < below)
            {
                return 0.0;
            }

            pressing = Math.Max(pressing, c.Weight * curve.At(value));
        }

        return Math.Clamp(1.0 - pressing, 0.0, 1.0);
    }

    /// <summary>Score of an exploring consideration on a type (without distance).</summary>
    private double Exploring(Inhabitant inhabitant, Consideration c, Curve curve, Curve novelty, string typeId) =>
        c.Weight * curve.At(c.Need is null ? 1.0 : inhabitant.Needs.GetValueOrDefault(c.Need, 1.0)) * TraitFactor(inhabitant, c)
            * novelty.At(Novelty(inhabitant, typeId)) * Calm(inhabitant);

    /// <summary>The trait's value (0 if the inhabitant lacks it), or 1 without a trait.</summary>
    private static double TraitFactor(Inhabitant inhabitant, Consideration c) =>
        c.Trait is { } trait ? inhabitant.Traits.GetValueOrDefault(trait, 0.0) : 1.0;

    /// <summary>
    /// The surest belief that some type has the property, times what the inhabitant would avoid in it
    /// (its own beliefs only; 0 = it knows of nothing that helps).
    /// </summary>
    private static double SurestHelpfulBelief(Inhabitant inhabitant, string property, IEnumerable<string> avoid) =>
        inhabitant.Beliefs.All
            .Where(b => b.PropertyId == property && b.Belief.Value)
            .Select(b => b.Belief.Confidence * AvoidFactor(inhabitant, b.TypeId, avoid))
            .DefaultIfEmpty(0.0)
            .Max();

    /// <summary>
    /// B2668: the second property a consideration needs (requires_also) counts only when the inhabitant believes the
    /// type has it (its confidence); unknown or believed absent = 0, so a natural thing is never taken for a made one.
    /// </summary>
    private static double AlsoFactor(Inhabitant inhabitant, string typeId, string? also) =>
        also is null ? 1.0 : inhabitant.Beliefs.TryGet(typeId, also, out var belief) && belief.Value ? belief.Confidence : 0.0;

    /// <summary>For each avoided property the inhabitant believes the type has: factor 1 - confidence.</summary>
    private static double AvoidFactor(Inhabitant inhabitant, string typeId, IEnumerable<string> avoid)
    {
        var factor = 1.0;
        foreach (var property in avoid)
        {
            if (inhabitant.Beliefs.TryGet(typeId, property, out var belief) && belief.Value)
            {
                factor *= 1.0 - belief.Confidence;
            }
        }

        return factor;
    }

    private double DistanceFactor(Position from, Position to)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        var distance = Math.Sqrt((dx * dx) + (dz * dz));
        return 1.0 - (Weights.DistanceFalloff * Math.Min(1.0, distance / Weights.PerceptionRadiusM));
    }
}
