using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>
/// B2687 (A1451 part 1, plan D932): using things together. What comes of it is the world's truth: the
/// combination rules of the data, matched by the TRUE properties of the things (never by their names),
/// so found and invented things take part without code. The core checks only what the spec asks (R39):
/// the things are there and within reach; it never judges whether a try makes sense.
/// </summary>
public sealed partial class InhabitantsSystem
{
    /// <summary>
    /// Combination rules judged against the world's type truth (set by the host); null = using things
    /// together has no result (the action still takes its time and is remembered as without effect).
    /// </summary>
    public CombinationRuleSet? Combinations { get; set; }

    /// <summary>
    /// Whether every partner thing of the activity is still there, is not the target itself, appears once
    /// and lies within the action's partner reach of the target. True for activities without partners.
    /// </summary>
    private bool PartnersReady(Activity activity, WorldObject? target)
    {
        if (activity.Partners is not { Count: > 0 } partners)
        {
            return true;
        }

        if (target is null || partners.Distinct(StringComparer.Ordinal).Count() != partners.Count)
        {
            return false;
        }

        var reach = _actions.HasTiming(activity.Action) ? _actions.Timing(activity.Action).PartnerReachM : 0;
        foreach (var id in partners)
        {
            if (id == target.Id || !_objects.TryGet(id, out var partner) || Distance(target.Position, partner.Position) > reach)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Applies the first combination rule (rule-id order) that fits the action and the things - the target
    /// and its partners in entity-id order, so the same things give the same assignment in every run and in
    /// replays. The new things lie where the actor stands; the rule's consumed inputs are used up. The actor
    /// experiences the true properties of what came of it, and the world reports it (memory, chronicle).
    /// Returns whether anything came of it; nothing fitting is an outcome too (without effect, so the
    /// repetition guard lets the same try rest, W1696).
    /// </summary>
    private bool Combine(World world, Inhabitant inhabitant, Activity activity, WorldObject target)
    {
        if (Combinations is not { } rules)
        {
            return false;
        }

        var things = new List<WorldObject>(activity.Partners!.Count + 1) { target };
        foreach (var id in activity.Partners!)
        {
            if (_objects.TryGet(id, out var partner))
            {
                things.Add(partner);
            }
        }

        things.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        var matches = rules.Match(activity.Action, [.. things.Select(t => t.TypeId)]);
        if (matches.Count == 0)
        {
            return false;
        }

        var (rule, assignment) = matches[0];
        foreach (var slot in rule.Consumes.Distinct().Order())
        {
            _objects.Remove(things[assignment[slot]].Id);
        }

        foreach (var typeId in rule.Creates)
        {
            var made = _objects.Spawn(typeId, inhabitant.Position);
            Experience(world.Tick, inhabitant, activity.Action, made.TypeId, _objects.Catalog.TrueProperties(made.TypeId));
            Report(new WorldEvent(Combined, world.Tick, inhabitant.Id, activity.Action, made.Id) { TargetType = made.TypeId, Detail = rule.Id });
        }

        return true;
    }
}
