using GlassWorld.Simulation.Family;

namespace GlassWorld.Presentation.World;

/// <summary>One person in the family tree tab.</summary>
/// <param name="Id">The inhabitant id.</param>
/// <param name="Name">Its name (its id while it has none).</param>
/// <param name="Alive">False once it died (the tree keeps the deceased).</param>
/// <param name="Chosen">Whether it is the inhabitant chosen on the card.</param>
/// <param name="Parents">Ids of its parents in the tree.</param>
public sealed record TreeMember(string Id, string Name, bool Alive, bool Chosen, IReadOnlyList<string> Parents);

/// <summary>One generation of the tree: 0 = the oldest shown.</summary>
/// <param name="Index">Generation, from the oldest shown.</param>
/// <param name="Members">Its members, ordered by name.</param>
public sealed record TreeGeneration(int Index, IReadOnlyList<TreeMember> Members);

/// <summary>The family tree tab's content (A1343 8a, D957 section 8).</summary>
/// <param name="Generations">The generations from the oldest shown.</param>
/// <param name="Placeholder">
/// True while the core knows no kin yet (before B9 families): then everyone living stands in one first generation, as
/// D957 says ("Attrappe aus den Begegnungen ohne Verwandtschaft"), and the tab says so.
/// </param>
public sealed record FamilyTreeView(IReadOnlyList<TreeGeneration> Generations, bool Placeholder);

/// <summary>
/// A1343 8a (B6-11, R17, D957 section 8): the family tree around the chosen inhabitant as generations - parents above
/// children, read from the core's family view (<see cref="IFamilyView.Tree"/>). Without Godot.
/// </summary>
public static class FamilyTree
{
    /// <summary>The tree of <paramref name="nodes"/> around <paramref name="chosenId"/>; <paramref name="everyone"/> fills the placeholder.</summary>
    public static FamilyTreeView Of(IReadOnlyList<FamilyNode> nodes, string? chosenId, IReadOnlyList<(string Id, string? Name)> everyone)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(everyone);
        var placeholder = nodes.All(n => n.Parents.Count == 0) && nodes.Count <= 1;
        IReadOnlyList<FamilyNode> shown = placeholder
            ? [.. everyone.Select(e => new FamilyNode(e.Id, e.Name, null, null, null, []))]
            : nodes;

        var byId = shown.GroupBy(n => n.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        int Depth(string id, int guard)
        {
            if (depth.TryGetValue(id, out var known))
            {
                return known;
            }

            // Parents outside the tree, or a loop in broken data, end the climb.
            var parents = byId[id].Parents.Where(byId.ContainsKey).ToList();
            var value = parents.Count == 0 || guard > byId.Count ? 0 : parents.Max(p => Depth(p, guard + 1)) + 1;
            depth[id] = value;
            return value;
        }

        var generations = byId.Values
            .GroupBy(n => Depth(n.Id, 0))
            .OrderBy(g => g.Key)
            .Select(g => new TreeGeneration(
                g.Key,
                [.. g.OrderBy(n => n.Name ?? n.Id, StringComparer.CurrentCultureIgnoreCase).ThenBy(n => n.Id, StringComparer.Ordinal)
                    .Select(n => new TreeMember(n.Id, n.Name ?? n.Id, n.DiedDay is null, string.Equals(n.Id, chosenId, StringComparison.Ordinal), n.Parents))]))
            .ToList();
        return new FamilyTreeView(generations, placeholder);
    }
}
