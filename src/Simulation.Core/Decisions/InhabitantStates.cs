using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Simulation.Decisions;

/// <summary>The inhabitant as the AI sees it (contract <c>InhabitantState</c>), shared by decisions and jobs.</summary>
public static class InhabitantStates
{
    /// <summary>Needs, moods above zero, traits and will to live, each sorted by id.</summary>
    public static InhabitantState Of(Inhabitant inhabitant)
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
