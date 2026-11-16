using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Writing;

/// <summary>
/// A1357 part 2 (B8 R13-R15, spec 33 section 9a): the actions write and read on a thing. The core offers them only where
/// they can work (skill, carrier, tool, text) and decides what a writer would write (its surest own belief, like teaching
/// passes its belief); whether and where to write is the decider's choice. The end of the action is applied here, in
/// the same tick, after the inhabitants (the system runs after them).
/// </summary>
public sealed partial class WritingSystem
{
    /// <summary>Event: an inhabitant wrote a text (target = the carrier, detail = the knowledge ref).</summary>
    public const string TextWritten = "text_written";

    private readonly List<(string Who, string Action, string? TargetId)> _pending = [];

    /// <summary>What the inhabitants hold (the hand slot); none = nobody holds a tool.</summary>
    public IHeldItems Held { get; init; } = NothingHeld.Instance;

    /// <summary>The write action's id from the data, or null without one.</summary>
    public string? WriteAction => _rules.Write.Action;

    /// <summary>The read action's id from the data, or null without one.</summary>
    public string? ReadAction => _rules.Read.Action;

    /// <summary>
    /// Whether <paramref name="action"/> is offered to <paramref name="asker"/> on <paramref name="target"/>: write only on a
    /// carrier it can write on and with something it knows to write; read only on a text it can read and has not read yet.
    /// Every other action: true (this rule says nothing about it).
    /// </summary>
    public bool Offers(Inhabitant asker, string action, WorldObject target)
    {
        ArgumentNullException.ThrowIfNull(asker);
        ArgumentNullException.ThrowIfNull(target);
        if (Skills is null)
        {
            return action != WriteAction && action != ReadAction;
        }

        if (action == WriteAction)
        {
            return MediumFor(asker, target) is not null && BeliefToWrite(asker) is not null;
        }

        if (action == ReadAction)
        {
            return On(target.Id) is { } text && CanRead(asker, text, Skills) is null && !_seen.Contains(ReadMark + asker.Id + "|" + target.Id);
        }

        return true;
    }

    /// <summary>The first carrier of the data (in its order) the writer can write on this thing with, or null.</summary>
    public string? MediumFor(Inhabitant writer, WorldObject target)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(target);
        if (Skills is null)
        {
            return null;
        }

        foreach (var medium in _rules.Media)
        {
            if (CanWriteOn(writer, target, medium.Id, Skills, Held) is null)
            {
                return medium.Id;
            }
        }

        return null;
    }

    /// <summary>
    /// What a writer would write: its surest own belief (not one it only read) of at least the data's confidence that no
    /// readable text within its sight says yet (review GW3 #549: not the same sentence on every stone); ties by type, then
    /// property. The same choice the decider is shown as the option's topic.
    /// </summary>
    public (string TypeId, string PropertyId, bool Value)? BeliefToWrite(Inhabitant writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var least = _rules.Write.MinConfidence ?? 0;
        var near = WrittenNear(writer.Position);
        (string TypeId, string PropertyId, bool Value)? best = null;
        var surest = double.MinValue;
        foreach (var (typeId, propertyId, belief) in writer.Beliefs.All)
        {
            if (belief.Source == KnowledgeSources.Read || belief.Confidence < least || belief.Confidence <= surest
                || near.Contains(RefOf(typeId, propertyId, belief.Value)))
            {
                continue;
            }

            best = (typeId, propertyId, belief.Value);
            surest = belief.Confidence;
        }

        return best;
    }

    /// <summary>The knowledge refs of the readable texts within sight of a position (none without inhabitants).</summary>
    private HashSet<string> WrittenNear(Position at)
    {
        var near = new HashSet<string>(StringComparer.Ordinal);
        if (Inhabitants is null || _texts.Count == 0)
        {
            return near;
        }

        var radius = Inhabitants.Sight.Metres(PerceptionRadiusM);
        foreach (var text in _texts.Values)
        {
            if (Readable(text) && _objects.TryGet(text.ObjectId, out var obj)
                && ((obj.Position.X - at.X) * (obj.Position.X - at.X)) + ((obj.Position.Z - at.Z) * (obj.Position.Z - at.Z)) <= radius * radius)
            {
                near.Add(text.KnowledgeRef);
            }
        }

        return near;
    }

    private static string RefOf(string typeId, string propertyId, bool value) => $"belief:{typeId}|{propertyId}|{(value ? "true" : "false")}";

    /// <summary>An inhabitant event: the end of a write or read action is applied in this tick's step of the system.</summary>
    public void Observe(WorldEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Kind == InhabitantsSystem.ActionFinished && e.Action is { } action && (action == WriteAction || action == ReadAction))
        {
            _pending.Add((e.InhabitantId, action, e.TargetId));
        }
    }

    private void ApplyPending(long tick)
    {
        if (_pending.Count == 0 || Inhabitants is null || Skills is null)
        {
            _pending.Clear();
            return;
        }

        foreach (var (who, action, targetId) in _pending)
        {
            if (targetId is null || !Inhabitants.TryGet(who, out var inhabitant) || !_objects.TryGet(targetId, out var target))
            {
                continue;
            }

            if (action == WriteAction)
            {
                Write(inhabitant, target, tick);
            }
            else if (On(target.Id) is { } text && CanRead(inhabitant, text, Skills) is null
                && _seen.Add(ReadMark + inhabitant.Id + "|" + target.Id))
            {
                // Read on purpose: as in passing, but curiosity does not matter.
                Learn(inhabitant, text, tick);
            }
        }

        _pending.Clear();
    }

    private void Write(Inhabitant writer, WorldObject target, long tick)
    {
        // The rule checker at the end (B2462): things may have changed since the choice - then nothing is written.
        if (MediumFor(writer, target) is not { } medium || BeliefToWrite(writer) is not { } belief)
        {
            return;
        }

        var knowledgeRef = RefOf(belief.TypeId, belief.PropertyId, belief.Value);
        var legibility = LegibilityFor(medium, Skills!.Level(writer.Id, _rules.Write.Ability));
        Place(new WrittenText(target.Id, medium, knowledgeRef, writer.Id, tick, legibility));
        Inhabitants!.Report(new WorldEvent(TextWritten, tick, writer.Id, WriteAction, target.Id) { Detail = knowledgeRef });
    }
}
