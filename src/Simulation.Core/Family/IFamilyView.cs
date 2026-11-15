using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Simulation.Family;

/// <summary>One person in a family tree (B9 R29, D1049 v2): enough for the look's small portrait and the chronicle.</summary>
/// <param name="Id">The inhabitant id.</param>
/// <param name="Name">Its name, or null.</param>
/// <param name="LifeStage">Its life phase (vocabulary life_stage), or null.</param>
/// <param name="BornDay">Game day of birth (negative for the start inhabitants), or null.</param>
/// <param name="DiedDay">Game day of death, or null while it lives.</param>
/// <param name="Parents">Ids of its parents in the tree.</param>
public sealed record FamilyNode(string Id, string? Name, string? LifeStage, long? BornDay, long? DiedDay, IReadOnlyList<string> Parents);

/// <summary>
/// The read-only family view of the core (B9 R29, D1049 v2) for the game (tree, card) and the prompts: kinship, the tree,
/// reputation, settlement and the culture an inhabitant knows. Without Godot. Until B9-4/B9-6/B9-7 fill it the answers are
/// empty - the game and the prompts can build against it now.
/// </summary>
public interface IFamilyView
{
    /// <summary>The kinship of an inhabitant as it knows it (living and deceased).</summary>
    IReadOnlyList<FamilyTie> FamilyOf(string inhabitantId);

    /// <summary>The family tree around an inhabitant, up to <paramref name="generations"/> back and forth.</summary>
    IReadOnlyList<FamilyNode> Tree(string inhabitantId, int generations);

    /// <summary>Its reputation 0..1, or null while there is none.</summary>
    double? ReputationOf(string inhabitantId);

    /// <summary>The settlement it belongs to, or null.</summary>
    string? SettlementOf(string inhabitantId);

    /// <summary>The culture entries it knows.</summary>
    IReadOnlyList<CultureEntry> CultureKnownBy(string inhabitantId);
}

/// <summary>
/// The family view before families exist (B9-1b): everyone is alone in its tree with its own node, nobody has kin,
/// reputation, settlement or culture yet.
/// </summary>
public sealed class FirstGenerationView(InhabitantsSystem inhabitants, long ticksPerDay) : IFamilyView
{
    /// <inheritdoc/>
    public IReadOnlyList<FamilyTie> FamilyOf(string inhabitantId) => [];

    /// <inheritdoc/>
    public IReadOnlyList<FamilyNode> Tree(string inhabitantId, int generations)
    {
        ArgumentNullException.ThrowIfNull(inhabitantId);
        if (!inhabitants.TryGet(inhabitantId, out var who))
        {
            return [];
        }

        var day = who.BornTick is { } born ? (long?)Math.Floor(born / (double)Math.Max(1, ticksPerDay)) : null;
        return [new FamilyNode(who.Id, who.Name, who.LifeStage, day, null, [])];
    }

    /// <inheritdoc/>
    public double? ReputationOf(string inhabitantId) => null;

    /// <inheritdoc/>
    public string? SettlementOf(string inhabitantId) => null;

    /// <inheritdoc/>
    public IReadOnlyList<CultureEntry> CultureKnownBy(string inhabitantId) => [];
}
