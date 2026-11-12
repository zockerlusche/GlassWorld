using GlassWorld.Contracts;
using GlassWorld.Simulation.Inventions;

namespace GlassWorld.Presentation.World;

/// <summary>
/// How an invention shows next to its name (A1603): the painted icon the computing night made (#298), else the
/// colours of the construction kit its building plan uses (most of the thing first), else nothing.
/// </summary>
/// <param name="PicturePath">The painted icon (a PNG in the service's picture folder), or null.</param>
/// <param name="KitColors">Kit colours as #rrggbb, the colour of the largest share first; empty with a picture or without a plan.</param>
public sealed record InventionIcon(string? PicturePath, IReadOnlyList<string> KitColors);

/// <summary>Finds the icon of an invention type (A1603); presentation only, no Godot.</summary>
/// <param name="isInvention">Whether a type id is an invention of this world.</param>
/// <param name="pictureFileOf">The file name of an invention's painted icon, or null (<see cref="InventionSystem.ImageOf"/>).</param>
/// <param name="planOf">An invention's building plan as the core keeps it, or null (<see cref="BuildTask.PlanJson"/>).</param>
/// <param name="kit">The construction kit (colour ids to colours).</param>
/// <param name="pictureFolder">Folder of the pictures the service made, or null.</param>
/// <param name="maxColors">How many kit colours the fallback shows at most.</param>
/// <param name="fileExists">File check (tests pass a stand-in).</param>
public sealed class InventionIconSource(
    Func<string, bool> isInvention,
    Func<string, string?> pictureFileOf,
    Func<string, string?> planOf,
    BuildKit kit,
    string? pictureFolder,
    int maxColors = 3,
    Func<string, bool>? fileExists = null)
{
    private readonly Func<string, bool> _exists = fileExists ?? File.Exists;
    private readonly Dictionary<string, string> _colors = kit?.Colors.ToDictionary(c => c.Id, c => c.Color, StringComparer.Ordinal) ?? throw new ArgumentNullException(nameof(kit));

    /// <summary>The icons of a world's inventions.</summary>
    public static InventionIconSource Of(InventionSystem inventions, BuildKit kit, string? pictureFolder, int maxColors = 3)
    {
        ArgumentNullException.ThrowIfNull(inventions);
        return new InventionIconSource(
            t => inventions.Invention(t) is not null,
            t => inventions.ImageOf(t)?.File,
            t => inventions.Builds.FirstOrDefault(b => b.TypeId == t && b.PlanJson is not null)?.PlanJson,
            kit,
            pictureFolder,
            maxColors);
    }

    /// <summary>The icon of an invention type, or null when it is none or has neither a picture nor a plan yet.</summary>
    public InventionIcon? For(string typeId)
    {
        ArgumentNullException.ThrowIfNull(isInvention);
        ArgumentNullException.ThrowIfNull(pictureFileOf);
        ArgumentNullException.ThrowIfNull(planOf);
        if (!isInvention(typeId))
        {
            return null;
        }

        // The file name comes from the contract (a plain name); only the name is taken, never a path.
        if (pictureFileOf(typeId) is { Length: > 0 } file && pictureFolder is not null)
        {
            var path = Path.Combine(pictureFolder, Path.GetFileName(file));
            if (_exists(path))
            {
                return new InventionIcon(path, []);
            }
        }

        if (planOf(typeId) is not { } plan)
        {
            return null;
        }

        var shares = InventionSystem.PlanOf(plan).Parts
            .Where(p => _colors.ContainsKey(p.Color))
            .GroupBy(p => p.Color, StringComparer.Ordinal)
            .Select(g => (Id: g.Key, Volume: g.Sum(p => p.SizeM.X * p.SizeM.Y * p.SizeM.Z)))
            .OrderByDescending(s => s.Volume)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, maxColors))
            .Select(s => _colors[s.Id])
            .ToList();
        return shares.Count == 0 ? null : new InventionIcon(null, shares);
    }
}
