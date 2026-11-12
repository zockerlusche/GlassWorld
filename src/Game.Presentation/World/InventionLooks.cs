using System.Globalization;
using System.Text;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.World;

/// <summary>An invention to draw from its building plan: a finished thing or a building site.</summary>
/// <param name="Key">Stable key of the drawing: the object's id, or <c>site:</c> and the type for a building site.</param>
/// <param name="TypeId">The invented type.</param>
/// <param name="PlanJson">The checked building plan as the core keeps it.</param>
/// <param name="X">Position x (m).</param>
/// <param name="Z">Position z (m).</param>
/// <param name="Step">Building steps shown (all of them for a finished thing).</param>
/// <param name="Finished">True for a finished thing, false for a building site.</param>
public sealed record InventionShown(string Key, string TypeId, string PlanJson, double X, double Z, int Step, bool Finished);

/// <summary>
/// What of the inventions is drawn from its building plan (B4b R14, A1375): every object of an invented type
/// whose plan was accepted and built, with all its parts, and every building site while the inventor builds,
/// with the parts of the finished steps. The plans come from the core, which checked them; this only says what
/// to draw where. Without Godot, so it can be tested.
/// </summary>
public static class InventionLooks
{
    /// <summary>Everything to draw, objects first (by id), then the building sites (by type).</summary>
    public static IReadOnlyList<InventionShown> Shown(IEnumerable<BuildTask> builds, IEnumerable<WorldObject> objects)
    {
        ArgumentNullException.ThrowIfNull(builds);
        ArgumentNullException.ThrowIfNull(objects);
        var tasks = builds.Where(b => b.PlanJson is not null).ToList();
        var finished = tasks.Where(b => b.BuiltObjectId is not null).ToDictionary(b => b.TypeId, b => b.PlanJson!, StringComparer.Ordinal);
        var shown = new List<InventionShown>();
        foreach (var obj in objects.Where(o => finished.ContainsKey(o.TypeId)).OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            shown.Add(new InventionShown(obj.Id, obj.TypeId, finished[obj.TypeId], obj.Position.X, obj.Position.Z, int.MaxValue, true));
        }

        // A site shows once its first step is done: before that there is nothing built to see.
        foreach (var site in tasks.Where(b => b.BuiltObjectId is null && !b.GaveUp && b.StepsDone > 0).OrderBy(b => b.TypeId, StringComparer.Ordinal))
        {
            shown.Add(new InventionShown("site:" + site.TypeId, site.TypeId, site.PlanJson!, site.SiteX, site.SiteZ, site.StepsDone, false));
        }

        return shown;
    }

    /// <summary>The invented types drawn from a plan: the object view leaves their objects out (no neutral marker).</summary>
    public static IReadOnlySet<string> DrawnTypes(IEnumerable<BuildTask> builds)
    {
        ArgumentNullException.ThrowIfNull(builds);
        return builds.Where(b => b.PlanJson is not null && b.BuiltObjectId is not null).Select(b => b.TypeId).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>A short text that changes whenever what is drawn changes (so the drawing is redone only then).</summary>
    public static string Signature(IReadOnlyList<InventionShown> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        var text = new StringBuilder();
        foreach (var s in shown)
        {
            text.Append(s.Key).Append('|').Append(s.Step.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(s.X.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(s.Z.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        }

        return text.ToString();
    }
}
