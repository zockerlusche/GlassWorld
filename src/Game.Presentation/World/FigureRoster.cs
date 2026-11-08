namespace GlassWorld.Presentation.World;

/// <summary>
/// Which figures the island view must add or remove after a snapshot (births, deaths, new inhabitants,
/// a loaded world), without Godot. Both lists are in id order, so the scene changes the same way every run.
/// </summary>
public static class FigureRoster
{
    /// <summary>Ids in <paramref name="current"/> without a figure, and figures whose id is gone.</summary>
    public static (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) Diff(IEnumerable<string> shown, IEnumerable<string> current)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(current);
        var have = shown.ToHashSet(StringComparer.Ordinal);
        var now = current.ToHashSet(StringComparer.Ordinal);
        return (
            [.. now.Where(id => !have.Contains(id)).Order(StringComparer.Ordinal)],
            [.. have.Where(id => !now.Contains(id)).Order(StringComparer.Ordinal)]);
    }
}
