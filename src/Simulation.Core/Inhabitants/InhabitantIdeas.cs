using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>A root of an idea: a subject of the inhabitant's knowledge (kind and id) or another idea (kind <c>idea</c>).</summary>
/// <param name="Kind">Subject kind (<c>type</c>, <c>object</c>, <c>inhabitant</c>) or <c>idea</c>.</param>
/// <param name="Id">The subject's or idea's id.</param>
public sealed record IdeaRoot(string Kind, string Id);

/// <summary>The root kinds an idea is built on.</summary>
public static class IdeaRootKinds
{
    /// <summary>A type of thing.</summary>
    public const string Type = "type";

    /// <summary>Another idea.</summary>
    public const string Idea = "idea";
}

/// <summary>
/// An idea of an inhabitant (B2578, B4b R28): a "What if I ...?" from the nightly look back (A1584), built only on
/// what it knows. The text is internal English for the AI, never shown as is; what an idea does (grow, ripen,
/// become a wish) is data. Kept in the save game and the state hash.
/// </summary>
/// <param name="Id">Stable id within the inhabitant (<c>idea-NNNN</c>).</param>
/// <param name="Text">Short English text (internal).</param>
/// <param name="Roots">What it is built on, in the order the AI named it.</param>
/// <param name="Strength">How grown it is, 0..1 (grows with each look back that takes it up; data).</param>
/// <param name="Tick">When it came up or last grew.</param>
/// <param name="Tried">True once it became a wish to invent (B2687 step 2): a ripe idea is tried once.</param>
public sealed record Idea(string Id, string Text, IReadOnlyList<IdeaRoot> Roots, double Strength, long Tick, bool Tried = false);

/// <summary>
/// Something the inhabitant means to do (A1584, B2592 "Vorsätze"): an action and what it is about, from the nightly
/// look back. Offered in a free moment (B2687); it never forces anything.
/// </summary>
/// <param name="Action">An action of the vocabulary.</param>
/// <param name="AboutKind">Subject kind, or null.</param>
/// <param name="AboutId">Subject id, or null.</param>
/// <param name="Tick">When it was made (the night).</param>
/// <param name="Thought">Its morning thought in the player's language (passed the content guard), or null.</param>
public sealed record Intention(string Action, string? AboutKind, string? AboutId, long Tick, string? Thought = null);

/// <summary>The ideas and intentions of one inhabitant (A1584). Empty until the first look back with ideas.</summary>
public sealed class InhabitantIdeas
{
    private readonly List<Idea> _ideas = [];
    private readonly List<Intention> _intentions = [];

    /// <summary>The ideas, oldest first.</summary>
    public IReadOnlyList<Idea> Ideas => _ideas;

    /// <summary>The intentions of the coming day, in the order the look back named them.</summary>
    public IReadOnlyList<Intention> Intentions => _intentions;

    /// <summary>Running number of the newest idea ever made (ids are not reused).</summary>
    public long Counter { get; private set; }

    /// <summary>True once anything is held (the state hash writes the ideas only then).</summary>
    public bool HasState => _ideas.Count > 0 || _intentions.Count > 0 || Counter > 0;

    /// <summary>
    /// Takes a new idea (id null) or lets one grow (its id): a new one gets the next id and the data's start strength;
    /// beyond <paramref name="max"/> ideas the weakest (then oldest) is forgotten. Returns the idea as it is now.
    /// </summary>
    public Idea Take(string? growsId, string text, IReadOnlyList<IdeaRoot> roots, long tick, double start, double grow, int max)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentNullException.ThrowIfNull(roots);
        var index = growsId is null ? -1 : _ideas.FindIndex(i => i.Id == growsId);
        Idea idea;
        if (index >= 0)
        {
            var old = _ideas[index];
            idea = old with { Text = text, Roots = roots, Strength = Math.Min(1.0, old.Strength + grow), Tick = tick };
            _ideas[index] = idea;
            return idea;
        }

        Counter++;
        idea = new Idea(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"idea-{Counter:D4}"), text, roots, Math.Clamp(start, 0, 1), tick);
        _ideas.Add(idea);
        while (_ideas.Count > Math.Max(1, max))
        {
            var weakest = _ideas.OrderBy(i => i.Strength).ThenBy(i => i.Tick).ThenBy(i => i.Id, StringComparer.Ordinal).First();
            _ideas.Remove(weakest);
        }

        return idea;
    }

    /// <summary>Replaces the intentions with the ones of a new look back.</summary>
    public void Intend(IEnumerable<Intention> intentions)
    {
        ArgumentNullException.ThrowIfNull(intentions);
        _intentions.Clear();
        _intentions.AddRange(intentions);
    }

    /// <summary>An intention was offered (B2687 step 2): it is used up, whatever was chosen - it is not offered again.</summary>
    public void Use(Intention intention) => _intentions.Remove(intention);

    /// <summary>The idea became a wish to invent (B2687 step 2): it is not tried again.</summary>
    public void MarkTried(string id)
    {
        var index = _ideas.FindIndex(i => i.Id == id);
        if (index >= 0)
        {
            _ideas[index] = _ideas[index] with { Tried = true };
        }
    }

    /// <summary>Replaces everything with a saved state (save games).</summary>
    public void Restore(long counter, IEnumerable<Idea> ideas, IEnumerable<Intention> intentions)
    {
        ArgumentNullException.ThrowIfNull(ideas);
        ArgumentNullException.ThrowIfNull(intentions);
        Counter = counter;
        _ideas.Clear();
        _ideas.AddRange(ideas);
        _intentions.Clear();
        _intentions.AddRange(intentions);
    }

    /// <summary>Writes the ideas and intentions in a fixed order.</summary>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write("ideas").Write(Counter).Write(_ideas.Count);
        foreach (var idea in _ideas)
        {
            hasher.Write(idea.Id).Write(idea.Text).Write(idea.Strength).Write(idea.Tick).Write(idea.Tried).Write(idea.Roots.Count);
            foreach (var root in idea.Roots)
            {
                hasher.Write(root.Kind).Write(root.Id);
            }
        }

        hasher.Write(_intentions.Count);
        foreach (var intention in _intentions)
        {
            hasher.Write(intention.Action).Write(intention.AboutKind ?? string.Empty).Write(intention.AboutId ?? string.Empty).Write(intention.Tick).Write(intention.Thought ?? string.Empty);
        }
    }
}
