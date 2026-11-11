using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Simulation.Decisions;

/// <summary>The inhabitant as the AI sees it (contract <c>InhabitantState</c>), shared by decisions and jobs.</summary>
public static class InhabitantStates
{
    /// <summary>Needs, moods above zero, traits and will to live, each sorted by id.</summary>
    public static InhabitantState Of(Inhabitant inhabitant) => Of(inhabitant, null, [], []);

    /// <summary>
    /// As <see cref="Of(Inhabitant)"/>, plus what the inhabitant believes about the types it perceives (with
    /// source and confidence; never the truth) and its relationships to the inhabitants in sight (B4a R3/R7,
    /// contract 0.2). Limits from the knowledge data keep the text budget.
    /// </summary>
    /// <param name="inhabitant">The inhabitant.</param>
    /// <param name="knowledge">Knowledge data; null = needs, moods, traits and memory only.</param>
    /// <param name="perceivedTypes">Object types in sight.</param>
    /// <param name="perceivedInhabitants">Inhabitants in sight.</param>
    /// <param name="label">What the inhabitant calls the subject of a remembered event (<see cref="InhabitantsSystem.LabelOf"/>); null = no labels.</param>
    /// <param name="typeLabel">What the inhabitant calls a kind of thing its knowledge or a habit is about (<see cref="InhabitantsSystem.TypeLabelOf"/>); null = no labels.</param>
    public static InhabitantState Of(Inhabitant inhabitant, KnowledgeSettings? knowledge, IEnumerable<string> perceivedTypes, IEnumerable<string> perceivedInhabitants, Func<Inhabitant, MemoryEntry, LocalizedName?>? label = null, Func<Inhabitant, string, LocalizedName?>? typeLabel = null)
    {
        ArgumentNullException.ThrowIfNull(perceivedTypes);
        ArgumentNullException.ThrowIfNull(perceivedInhabitants);
        var state = Basic(inhabitant, label);
        if (knowledge is null)
        {
            return state;
        }

        var types = perceivedTypes.Distinct(StringComparer.Ordinal).ToList();
        var visible = knowledge.Looking?.VisibleProperties.ToHashSet(StringComparer.Ordinal) ?? [];
        state.Knowledge = [.. types

            // What a thing is not by its look (not a stone ...) says little and would crowd out the rest.
            .SelectMany(type => inhabitant.Beliefs.About(type).Where(b => b.Belief.Value || !visible.Contains(b.PropertyId)).Select(b => (Type: type, b.PropertyId, b.Belief)))
            .Take(knowledge.RequestMaxKnowledge)
            .Select(b => Entry(inhabitant, b.Type, b.PropertyId, b.Belief, typeLabel))];
        var start = new Objects.Relation(knowledge.RelationshipStart.Affection, knowledge.RelationshipStart.Trust, knowledge.RelationshipStart.Respect);
        state.Relationships = [.. perceivedInhabitants.Take(knowledge.RequestMaxInhabitants).Select(other =>
        {
            var r = inhabitant.Relationships.Get(other, start);
            return new Relationship { Other = other, Affection = r.Affection, Trust = r.Trust, Respect = r.Respect };
        })];

        // B4a R6: habits grown from repeated experience, strongest first (information only).
        state.Habits = [.. InhabitantsSystem.Habits(knowledge, inhabitant).Select(h => new Habit
        {
            Action = h.Key.Action,
            TimeOfDay = h.Key.TimeOfDay,
            About = h.Key.AboutKind.Length == 0 ? null : new Subject { Kind = Enum.Parse<SubjectKind>(h.Key.AboutKind, ignoreCase: true), Id = h.Key.AboutId },
            Strength = h.Strength,

            // What the inhabitant calls the kind of thing its habit is about (an invention it knows); else absent.
            Label = h.Key.AboutKind == InhabitantsSystem.AboutType ? typeLabel?.Invoke(inhabitant, h.Key.AboutId) : null,
        })];
        return state;
    }

    internal static KnowledgeEntry Entry(Inhabitant inhabitant, string type, string property, Objects.Belief belief, Func<Inhabitant, string, LocalizedName?>? typeLabel = null)
    {
        var seen = LatestPlace(inhabitant, type);
        return new KnowledgeEntry
        {
            Subject = new Subject { Kind = SubjectKind.Type, Id = type },
            Property = property,
            Value = belief.Value,
            Confidence = belief.Confidence,
            Source = Enum.Parse<KnowledgeSource>(belief.Source, ignoreCase: true),
            ToldBy = belief.ToldBy,
            Tick = belief.Tick,

            // Where the inhabitant last saw a thing of this type (place memory, B2533); absent if never.
            Place = seen is { } s ? new Place { XM = s.Place.Position.X, ZM = s.Place.Position.Z } : null,
            Object = seen?.ObjectId,

            // What the inhabitant itself calls this kind of thing (an invention it knows); absent for what the data name.
            Label = typeLabel?.Invoke(inhabitant, type),
        };
    }

    /// <summary>The most recent remembered place of a type (ties by object id), or null.</summary>
    private static (string ObjectId, PlaceMemory Place)? LatestPlace(Inhabitant inhabitant, string typeId)
    {
        (string ObjectId, PlaceMemory Place)? latest = null;
        foreach (var (objectId, place) in inhabitant.Places)
        {
            if (place.TypeId == typeId && (latest is null || place.Tick > latest.Value.Place.Tick))
            {
                latest = (objectId, place);
            }
        }

        return latest;
    }

    private static InhabitantState Basic(Inhabitant inhabitant, Func<Inhabitant, MemoryEntry, LocalizedName?>? label)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        return new InhabitantState
        {
            Id = inhabitant.Id,
            Needs = [.. inhabitant.Needs.Select(n => new LevelEntry { Id = n.Key, Value = n.Value })],
            Mood = [.. inhabitant.Moods.Where(m => m.Value > 0).Select(m => new LevelEntry { Id = m.Key, Value = m.Value })],
            Traits = [.. inhabitant.Traits.Select(t => new LevelEntry { Id = t.Key, Value = t.Value })],
            WillToLive = inhabitant.WillToLive,

            // W1698: its own name, so it can introduce itself; absent while it has none.
            Name = inhabitant.Name,

            // Contract 0.2 (B4a): knowledge, relationships and habits are filled above when there is knowledge data.
            Memory = MemoryOf(inhabitant, label),
            Knowledge = [],
            Relationships = [],
            Habits = [],
        };
    }

    /// <summary>Most recent events a request may carry (contract <c>Memory</c>: recent maxItems).</summary>
    public const int MaxRecent = 12;

    /// <summary>
    /// The inhabitant's memory as the AI sees it (B4a R5): the summary and the newest remembered events,
    /// oldest first, at most <see cref="MaxRecent"/>. Empty while it remembers nothing. A counted event
    /// carries how often it happened; <paramref name="label"/> gives what the inhabitant calls the subject
    /// now (null = no labels).
    /// </summary>
    public static Memory MemoryOf(Inhabitant inhabitant, Func<Inhabitant, MemoryEntry, LocalizedName?>? label = null)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        return new Memory
        {
            Summary = inhabitant.Memory.Summary,
            Recent = [.. inhabitant.Memory.Events.TakeLast(MaxRecent).Select(e => new MemoryEvent
            {
                Tick = e.Tick,
                Kind = e.Kind,
                About = About(e),
                Times = TimesOf(e),
                Outcome = OutcomeOf(e),
                Label = label?.Invoke(inhabitant, e),
            })],
        };
    }

    /// <summary>What came of a remembered action as the contract carries it; null for other events.</summary>
    public static MemoryOutcome? OutcomeOf(MemoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Outcome switch
        {
            null => null,
            ActionOutcomes.Done => MemoryOutcome.Done,
            ActionOutcomes.FoundOut => MemoryOutcome.Found_out,
            _ => MemoryOutcome.Nothing,
        };
    }

    /// <summary>How often a remembered event happened as the contract carries it: absent for once.</summary>
    public static int? TimesOf(MemoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Times > 1 ? entry.Times : null;
    }

    /// <summary>What a remembered event is about as a contract subject (an object type or an inhabitant), or null.</summary>
    public static Subject? About(MemoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.AboutKind is null || entry.AboutId is null
            ? null
            : new Subject { Kind = Enum.Parse<SubjectKind>(entry.AboutKind, ignoreCase: true), Id = entry.AboutId };
    }

    /// <summary>
    /// The inhabitant asking itself about one object type (job question): what it believes about that type
    /// first, then its newest other beliefs, within the knowledge data's limit. Beliefs only, never the truth.
    /// </summary>
    public static InhabitantState Asking(Inhabitant inhabitant, KnowledgeSettings knowledge, string typeId, Func<Inhabitant, MemoryEntry, LocalizedName?>? label = null, Func<Inhabitant, string, LocalizedName?>? typeLabel = null)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentException.ThrowIfNullOrEmpty(typeId);
        var state = Of(inhabitant, knowledge, [typeId], [], label, typeLabel);
        var known = state.Knowledge.ToList();
        var visible = knowledge.Looking?.VisibleProperties.ToHashSet(StringComparer.Ordinal) ?? [];
        known.AddRange(inhabitant.Beliefs.All

            // As for the type itself: what a thing is not by its look says little and would crowd out the rest.
            .Where(b => b.TypeId != typeId && (b.Belief.Value || !visible.Contains(b.PropertyId)))
            .OrderByDescending(b => b.Belief.Tick)
            .ThenBy(b => b.TypeId, StringComparer.Ordinal)
            .ThenBy(b => b.PropertyId, StringComparer.Ordinal)
            .Take(Math.Max(0, knowledge.RequestMaxKnowledge - known.Count))
            .Select(b => Entry(inhabitant, b.TypeId, b.PropertyId, b.Belief, typeLabel)));
        state.Knowledge = known;
        return state;
    }
}
