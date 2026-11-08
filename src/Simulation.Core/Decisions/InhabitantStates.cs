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
    public static InhabitantState Of(Inhabitant inhabitant, KnowledgeSettings? knowledge, IEnumerable<string> perceivedTypes, IEnumerable<string> perceivedInhabitants)
    {
        ArgumentNullException.ThrowIfNull(perceivedTypes);
        ArgumentNullException.ThrowIfNull(perceivedInhabitants);
        var state = Basic(inhabitant);
        if (knowledge is null)
        {
            return state;
        }

        var types = perceivedTypes.Distinct(StringComparer.Ordinal).ToList();
        state.Knowledge = [.. types
            .SelectMany(type => inhabitant.Beliefs.About(type).Select(b => (Type: type, b.PropertyId, b.Belief)))
            .Take(knowledge.RequestMaxKnowledge)
            .Select(b => new KnowledgeEntry
            {
                Subject = new Subject { Kind = SubjectKind.Type, Id = b.Type },
                Property = b.PropertyId,
                Value = b.Belief.Value,
                Confidence = b.Belief.Confidence,
                Source = Enum.Parse<KnowledgeSource>(b.Belief.Source, ignoreCase: true),
                ToldBy = b.Belief.ToldBy,
                Tick = b.Belief.Tick,
            })];
        var start = new Objects.Relation(knowledge.RelationshipStart.Affection, knowledge.RelationshipStart.Trust, knowledge.RelationshipStart.Respect);
        state.Relationships = [.. perceivedInhabitants.Take(knowledge.RequestMaxInhabitants).Select(other =>
        {
            var r = inhabitant.Relationships.Get(other, start);
            return new Relationship { Other = other, Affection = r.Affection, Trust = r.Trust, Respect = r.Respect };
        })];
        return state;
    }

    private static InhabitantState Basic(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        return new InhabitantState
        {
            Id = inhabitant.Id,
            Needs = [.. inhabitant.Needs.Select(n => new LevelEntry { Id = n.Key, Value = n.Value })],
            Mood = [.. inhabitant.Moods.Where(m => m.Value > 0).Select(m => new LevelEntry { Id = m.Key, Value = m.Value })],
            Traits = [.. inhabitant.Traits.Select(t => new LevelEntry { Id = t.Key, Value = t.Value })],
            WillToLive = inhabitant.WillToLive,

            // Contract 0.2 (B4a): filled with real memory, knowledge, relationships and habits in B4a-S5.
            Memory = new Memory { Summary = string.Empty, Recent = [] },
            Knowledge = [],
            Relationships = [],
            Habits = [],
        };
    }
}
