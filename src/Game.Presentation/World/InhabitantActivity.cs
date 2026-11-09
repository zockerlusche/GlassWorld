using GlassWorld.Contracts;
using GlassWorld.Presentation.Ui;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.View;

namespace GlassWorld.Presentation.World;

/// <summary>Inhabitants and objects of one snapshot by id, built once per screen refresh (review GW3 #146).</summary>
public sealed class SnapshotIndex
{
    /// <summary>Indexes <paramref name="snapshot"/>.</summary>
    public SnapshotIndex(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Inhabitants = snapshot.Inhabitants.ToDictionary(i => i.Id, StringComparer.Ordinal);
        Objects = snapshot.Objects.ToDictionary(o => o.Id, StringComparer.Ordinal);
    }

    /// <summary>Inhabitants by id.</summary>
    public IReadOnlyDictionary<string, InhabitantSnapshot> Inhabitants { get; }

    /// <summary>Objects by id.</summary>
    public IReadOnlyDictionary<string, WorldObject> Objects { get; }
}

/// <summary>
/// What an inhabitant is doing, as the screen shows it, without Godot (Marcel W1687, W1691): what kind of
/// target its action has (the clip of a show towards another inhabitant differs from one towards a thing),
/// whom it faces while an action between two inhabitants runs, and a readable line for the inhabitant list
/// and card ("zeigt Bewohner 2: Quellwasser"), never a bare symbol. Reads snapshots only.
/// </summary>
public static class InhabitantActivity
{
    /// <summary>Text key when there is no action.</summary>
    public const string StandingKey = "ui.activity.standing";

    /// <summary>Text key when there is no action but an open question (the inhabitant waits for an idea, B2487).</summary>
    public const string ThinkingKey = "ui.activity.thinking";

    /// <summary>Prefix of the activity texts: <c>ui.activity.&lt;action&gt;[.inhabitant|.inhabitant_topic|.object]</c>.</summary>
    public const string Prefix = "ui.activity.";

    /// <summary>Whether the action of <paramref name="inhabitant"/> aims at another inhabitant, a thing, or nothing.</summary>
    public static AnimationMappingTarget? TargetKind(InhabitantSnapshot inhabitant, IReadOnlyList<InhabitantSnapshot> all)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        ArgumentNullException.ThrowIfNull(all);
        return inhabitant.TargetId is not { } id ? null
            : all.Any(o => o.Id == id) ? AnimationMappingTarget.Inhabitant
            : AnimationMappingTarget.Object;
    }

    /// <summary>
    /// Whether the inhabitant is in a conversation (W1697): its action aims at another inhabitant, another
    /// inhabitant's action aims at it, or the core turns it to someone. Then it neither sits nor bends.
    /// </summary>
    public static bool InConversation(InhabitantSnapshot inhabitant, IReadOnlyList<InhabitantSnapshot> all)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        ArgumentNullException.ThrowIfNull(all);
        return inhabitant.Facing is not null
            || TargetKind(inhabitant, all) == AnimationMappingTarget.Inhabitant
            || all.Any(o => o.TargetId == inhabitant.Id);
    }

    /// <summary>
    /// Where the inhabitant turns its whole body to (B2528, W1691): the position of the inhabitant the core
    /// says it faces (<see cref="InhabitantSnapshot.Facing"/>), or null (the figure faces where it walked).
    /// </summary>
    public static Position? FacePoint(InhabitantSnapshot inhabitant, IReadOnlyList<InhabitantSnapshot> all)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        ArgumentNullException.ThrowIfNull(all);
        return inhabitant.Facing is { } id ? all.FirstOrDefault(o => o.Id == id)?.Position : null;
    }

    /// <summary>
    /// A readable line for what the inhabitant does (W1687): the most specific text the catalog has for its
    /// action and target (with the other inhabitant's name and what it is about, or the thing's name), then
    /// the action alone, then the action's name; without an action "is thinking" while a question is open,
    /// else "is standing". <paramref name="tr"/> translates a key and returns the key itself when it is unknown;
    /// <paramref name="nameOf"/> gives the shown name of an inhabitant id (the screen's own, names come with B9).
    /// </summary>
    public static string Describe(InhabitantSnapshot inhabitant, SnapshotIndex index, Func<string, string> tr, Func<string, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(tr);
        ArgumentNullException.ThrowIfNull(nameOf);
        if (inhabitant.Action is not { } action)
        {
            return tr(inhabitant.OpenQuestionIds.Count > 0 ? ThinkingKey : StandingKey);
        }

        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        var keys = new List<string>();
        if (inhabitant.TargetId is { } id)
        {
            if (index.Inhabitants.ContainsKey(id))
            {
                args["name"] = nameOf(id);
                if (inhabitant.Topic is { } topic)
                {
                    args["thing"] = tr(VocabKeys.ObjectType(topic));
                    keys.Add(Prefix + action + ".inhabitant_topic");
                }

                keys.Add(Prefix + action + ".inhabitant");
            }
            else if (index.Objects.TryGetValue(id, out var thing))
            {
                args["thing"] = tr(VocabKeys.ObjectType(thing.TypeId));
                keys.Add(Prefix + action + ".object");
            }
        }

        keys.Add(Prefix + action);
        keys.Add(VocabKeys.Action(action));
        foreach (var key in keys)
        {
            var text = tr(key);
            if (!string.Equals(text, key, StringComparison.Ordinal))
            {
                return TextFill.Fill(text, args);
            }
        }

        return tr(StandingKey);
    }
}
