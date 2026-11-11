using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>
/// Meeting, relationships and imprinting (B4a R6–R8): who has met whom, how shared experiences change
/// affection, trust and respect, and the counters of repeated experiences. Every value comes from the
/// knowledge data; the code only counts and applies, it decides no behaviour (B2460).
/// </summary>
public sealed partial class InhabitantsSystem
{
    /// <summary>Event: two inhabitants who did not know each other see each other for the first time (B4a R8); target = the other.</summary>
    public const string FirstMeeting = "first_meeting";

    /// <summary>Subject kind of an imprinting counter about an object type.</summary>
    public const string AboutType = "type";

    /// <summary>Subject kind of an imprinting counter about another inhabitant.</summary>
    public const string AboutInhabitant = "inhabitant";

    /// <summary>
    /// Whether an inhabitant-targeted meeting action (knowledge data: meeting) is offered towards
    /// <paramref name="other"/>: all conditions of its option hold (distance, the asker's trust and affection,
    /// one of the asker's needs). Frame, not outcome: whether it is taken is the decider's choice.
    /// </summary>
    public static bool Offered(KnowledgeSettings knowledge, KnowledgeMeetingOption option, Inhabitant asker, Inhabitant other)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(asker);
        ArgumentNullException.ThrowIfNull(other);
        var relation = asker.Relationships.Get(other.Id, Start(knowledge));
        return (option.MinDistanceM is not { } min || DistanceSquared(asker.Position, other.Position) > min * min)
            && (option.MaxTrust is not { } trust || relation.Trust < trust)
            && (option.MaxAffection is not { } affection || relation.Affection < affection)
            && (option.Need is not { } need || option.NeedBelow is not { } below || asker.Needs.GetValueOrDefault(need, 1.0) < below);
    }

    /// <summary>
    /// The start inhabitants (F5249): members of the same start group have met and are partners, the others
    /// are strangers (knowledge data: start_relationships; without it the relationships stay unset).
    /// </summary>
    private void MeetStartGroups()
    {
        var starts = Knowledge?.StartRelationships;
        foreach (var a in _inhabitants.Values)
        {
            foreach (var b in _inhabitants.Values.Where(b => b != a))
            {
                var partners = a.Group == b.Group;
                if (partners)
                {
                    a.Met.Add(b.Id);
                }

                if (starts is not null)
                {
                    var r = partners ? starts.Partners : starts.Strangers;
                    a.Relationships.Set(b.Id, new Relation(r.Affection, r.Trust, r.Respect));
                }
            }
        }
    }

    /// <summary>
    /// The members of each start group have met (B4a R8, F5249); used at the start and after loading, where
    /// a state without meetings (older saves, snapshots) must not let partners meet again.
    /// </summary>
    public void RememberStartGroups()
    {
        foreach (var a in _inhabitants.Values)
        {
            foreach (var b in _inhabitants.Values.Where(b => b != a && b.Group == a.Group))
            {
                a.Met.Add(b.Id);
            }
        }
    }

    /// <summary>
    /// First meetings (B4a R8): two inhabitants that see each other (within the sight of this tick) and have
    /// not met before now have; one first_meeting event per pair (actor = lower id). Only with knowledge data.
    /// </summary>
    private void Meet(World world)
    {
        if (Knowledge is null)
        {
            return;
        }

        var sight = SightRadiusM * SightRadiusM;
        foreach (var a in _inhabitants.Values)
        {
            foreach (var b in _inhabitants.Values)
            {
                if (string.CompareOrdinal(a.Id, b.Id) >= 0 || (a.Met.Contains(b.Id) && b.Met.Contains(a.Id)) || DistanceSquared(a.Position, b.Position) > sight)
                {
                    continue;
                }

                var first = !a.Met.Contains(b.Id) && !b.Met.Contains(a.Id);
                a.Met.Add(b.Id);
                b.Met.Add(a.Id);
                if (first)
                {
                    Report(new WorldEvent(FirstMeeting, world.Tick, a.Id, null, b.Id));
                }
            }
        }
    }

    /// <summary>
    /// Time together and fading (B4a R7, data: relationship_changes): every pair within the data's distance at
    /// the start of the tick changes by the per-hour values; afterwards each value towards another inhabitant
    /// that lies above fade_to sinks by fade_per_hour, never below it. Id order, no random draw.
    /// </summary>
    private void Bond()
    {
        if (Knowledge is not { RelationshipChanges: { } changes } knowledge)
        {
            return;
        }

        var start = Start(knowledge);
        var reach = changes.TogetherDistanceM * changes.TogetherDistanceM;
        var perTick = 1.0 / _ticksPerHour;
        foreach (var a in _inhabitants.Values)
        {
            foreach (var b in _inhabitants.Values)
            {
                if (a != b && DistanceSquared(a.Position, b.Position) <= reach)
                {
                    Change(a, b.Id, changes.TogetherPerHour, perTick, start);
                }
            }

            var step = PerTick(changes.FadePerHour);
            var to = changes.FadeTo;
            foreach (var (other, r) in a.Relationships.All.Where(r => _inhabitants.ContainsKey(r.Other)).ToList())
            {
                var faded = new Relation(Fade(r.Affection, to.Affection, step), Fade(r.Trust, to.Trust, step), Fade(r.Respect, to.Respect, step));
                if (faded != r)
                {
                    a.Relationships.Set(other, faded);
                }
            }
        }
    }

    private static double Fade(double value, double to, double step) => value > to ? Math.Max(to, value - step) : value;

    /// <summary>An action towards another inhabitant has ended: both see each other as the data says (B4a R7).</summary>
    private void BondByAction(Inhabitant actor, Inhabitant listener, string action)
    {
        if (Knowledge is not { RelationshipChanges: { } changes } knowledge
            || changes.Actions.FirstOrDefault(a => string.Equals(a.Action, action, StringComparison.Ordinal)) is not { } rule)
        {
            return;
        }

        var start = Start(knowledge);
        Change(actor, listener.Id, rule.Actor, 1.0, start);
        Change(listener, actor.Id, rule.Listener, 1.0, start);
    }

    /// <summary>Heard knowledge proved right or wrong by one's own experience: the data's change towards the teller (B4a R7).</summary>
    private void BondByProof(Inhabitant learner, string teller, bool proved)
    {
        if (Knowledge is { RelationshipChanges: { } changes } knowledge)
        {
            Change(learner, teller, proved ? changes.HeardTrue : changes.HeardFalse, 1.0, Start(knowledge));
        }
    }

    private static void Change(Inhabitant inhabitant, string other, KnowledgeRelationChange change, double factor, Relation start)
    {
        if (change.Affection == 0 && change.Trust == 0 && change.Respect == 0)
        {
            return;
        }

        var r = inhabitant.Relationships.Get(other, start);
        inhabitant.Relationships.Set(other, new Relation(r.Affection + (change.Affection * factor), r.Trust + (change.Trust * factor), r.Respect + (change.Respect * factor)));
    }

    /// <summary>
    /// Imprinting (B4a R6): a started action counts once for (action, what it is about, time of day), up to the
    /// data's full_at; beyond max_counters the weakest other counter is forgotten (ties by key).
    /// </summary>
    private void Imprint(World world, Inhabitant inhabitant, Activity activity, WorldObject? target, Inhabitant? listener)
    {
        if (Knowledge?.Imprinting is not { } data || data.MaxCounters <= 0 || data.LeftOutActions.Contains(activity.Action))
        {
            return;
        }

        var (kind, id) = target is not null ? (AboutType, target.TypeId) : listener is not null ? (AboutInhabitant, listener.Id) : (string.Empty, string.Empty);
        var key = new ImprintKey(activity.Action, kind, id, world.Now.Phase.ToString().ToLower(CultureInfo.InvariantCulture));
        inhabitant.Imprints[key] = Math.Min(data.FullAt, inhabitant.Imprints.GetValueOrDefault(key) + 1.0);
        while (inhabitant.Imprints.Count > data.MaxCounters)
        {
            inhabitant.Imprints.Remove(inhabitant.Imprints.Where(p => p.Key != key).MinBy(p => p.Value).Key);
        }
    }

    /// <summary>Once per hour every counter fades by the data's value; what falls below forget_below is forgotten.</summary>
    private void FadeImprints(World world, Inhabitant inhabitant)
    {
        if (Knowledge?.Imprinting is not { } data || inhabitant.Imprints.Count == 0 || world.Tick % _ticksPerHour != 0)
        {
            return;
        }

        foreach (var key in inhabitant.Imprints.Keys.ToList())
        {
            var value = inhabitant.Imprints[key] - data.FadePerHour;
            if (value < data.ForgetBelow)
            {
                inhabitant.Imprints.Remove(key);
            }
            else
            {
                inhabitant.Imprints[key] = value;
            }
        }
    }

    /// <summary>
    /// The inhabitant's habits (B4a R6): counters from habit_from on, strongest first (ties by key), at most
    /// max_habits, each with its strength (counter over full_at, at most 1). Information for the decider only.
    /// </summary>
    public static IReadOnlyList<(ImprintKey Key, double Strength)> Habits(KnowledgeSettings knowledge, Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(inhabitant);
        if (knowledge.Imprinting is not { } data)
        {
            return [];
        }

        return [.. inhabitant.Imprints
            .Where(p => p.Value >= data.HabitFrom)
            .OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key, ImprintKey.Order)
            .Take(data.MaxHabits)
            .Select(p => (p.Key, Math.Min(1.0, p.Value / data.FullAt)))];
    }
}
