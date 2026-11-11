using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Behaviour;

/// <summary>
/// Who counts as familiar to an inhabitant and who counts as asleep (W1744): the togetherness data of the
/// inhabitant model (content/inhabitants/model.json), the one definition the routine shares with being
/// together. Only the inhabitant's own knowledge is read: whom it has met and what it feels towards them.
/// </summary>
/// <param name="MinTrust">Trust towards the other from which on it is familiar.</param>
/// <param name="MinAffection">Affection towards the other from which on it is familiar.</param>
/// <param name="Start">Relationship that counts while none is kept (knowledge data: relationship_start).</param>
/// <param name="SleepAction">Action during which an inhabitant counts as asleep (once it has started).</param>
public sealed record Familiarity(double MinTrust, double MinAffection, Relation Start, string SleepAction)
{
    /// <summary>The familiarity of the content: togetherness thresholds and the start relationship; null without togetherness.</summary>
    public static Familiarity? From(Togetherness? togetherness, KnowledgeRelationship? start)
    {
        if (togetherness is null)
        {
            return null;
        }

        return new Familiarity(
            togetherness.MinTrust,
            togetherness.MinAffection,
            start is null ? default : new Relation(start.Affection, start.Trust, start.Respect),
            togetherness.SleepAction);
    }

    /// <summary>
    /// Whether <paramref name="inhabitant"/> is familiar with the other: it has met them and its own
    /// relationship to them reaches both thresholds.
    /// </summary>
    public bool Familiar(Inhabitant inhabitant, string otherId)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        if (!inhabitant.Met.Contains(otherId))
        {
            return false;
        }

        var relation = inhabitant.Relationships.Get(otherId, Start);
        return relation.Trust >= MinTrust && relation.Affection >= MinAffection;
    }

    /// <summary>Whether the inhabitant knows anybody it is familiar with (wherever they are now).</summary>
    public bool KnowsSomeone(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        foreach (var other in inhabitant.Met)
        {
            if (Familiar(inhabitant, other))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether an inhabitant is asleep: its sleep action has started and still runs.</summary>
    public bool Asleep(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        return inhabitant.Activity is { RemainingTicks: not null } activity && string.Equals(activity.Action, SleepAction, StringComparison.Ordinal);
    }
}
