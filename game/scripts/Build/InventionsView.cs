using GlassWorld.Contracts;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Inventions;
using Godot;

namespace GlassWorld.Game.Build;

/// <summary>
/// The inventions in the running world (B4b R14, A1375): every built thing drawn from its building plan, and
/// every building site with the parts of the finished steps, on the drawn ground. What to draw where comes
/// from <see cref="InventionLooks"/>; a drawing is made once per thing and kept while it stays the same.
/// </summary>
public partial class InventionsView : Node3D
{
    private readonly Dictionary<string, (BuildPlanNode Node, string PlanJson)> _drawn = new(StringComparer.Ordinal);
    private BuildKit _kit = null!;
    private Func<double, double, double> _ground = null!;
    private string _signature = string.Empty;
    private double? _minShownM;

    /// <summary>Things and sites drawn now.</summary>
    public int Drawn => _drawn.Count;

    /// <summary>Parts visible now, over all drawings.</summary>
    public int VisibleParts => _drawn.Values.Sum(d => d.Node.VisibleParts);

    /// <summary>
    /// Creates the view with the construction kit and the drawn ground height (x, z) in metres; a finished thing smaller
    /// than <paramref name="minShownM"/> is drawn larger (A1715, look data; null = true size).
    /// </summary>
    public static InventionsView Create(BuildKit kit, Func<double, double, double> ground, double? minShownM = null) =>
        new() { Name = "Inventions", _kit = kit ?? throw new ArgumentNullException(nameof(kit)), _ground = ground ?? throw new ArgumentNullException(nameof(ground)), _minShownM = minShownM };

    /// <summary>Redraws when what is shown changed since the last call; returns whether it did.</summary>
    public bool Refresh(IReadOnlyList<InventionShown> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        var signature = InventionLooks.Signature(shown);
        if (signature == _signature)
        {
            return false;
        }

        _signature = signature;
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in shown)
        {
            keep.Add(s.Key);
            if (!_drawn.TryGetValue(s.Key, out var drawn) || drawn.PlanJson != s.PlanJson)
            {
                if (drawn.Node is not null)
                {
                    RemoveChild(drawn.Node);
                    drawn.Node.QueueFree();
                }

                var node = BuildPlanNode.Create(InventionSystem.PlanOf(s.PlanJson), _kit);
                node.Name = "Invention_" + s.Key.Replace(':', '_');
                AddChild(node);
                drawn = (node, s.PlanJson);
                _drawn[s.Key] = drawn;
            }

            drawn.Node.Position = new Vector3((float)s.X, (float)_ground(s.X, s.Z), (float)s.Z);
            // A1715: a tiny finished thing is drawn larger so it can be seen at all (from its ground point up).
            drawn.Node.Scale = Vector3.One * (float)(s.Finished ? InventionLooks.ShownScale(LargestExtent(drawn.Node), _minShownM) : 1.0);
            // Up to the step the model says: a whole thing says int.MaxValue, one finished smaller its last step (review GW3 #295).
            drawn.Node.ShowUpToStep(Math.Min(s.Step, drawn.Node.LastStep));
        }

        foreach (var key in _drawn.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            var node = _drawn[key].Node;
            RemoveChild(node);
            node.QueueFree();
            _drawn.Remove(key);
        }

        if (OS.IsDebugBuild())
        {
            GD.Print($"INVENTIONS_DRAWN things={shown.Count(s => s.Finished)} sites={shown.Count(s => !s.Finished)} parts={VisibleParts}");
        }

        return true;
    }

    /// <summary>The largest extent of all parts of a drawing in its own space (not its scale), in metres.</summary>
    private static double LargestExtent(BuildPlanNode node)
    {
        Aabb? box = null;
        foreach (var mesh in node.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>())
        {
            var place = mesh.Transform;
            for (var parent = mesh.GetParent(); parent is Node3D up && parent != node; parent = parent.GetParent())
            {
                place = up.Transform * place;
            }

            var part = place * mesh.GetAabb();
            box = box is { } known ? known.Merge(part) : part;
        }

        return box is { } all ? Math.Max(all.Size.X, Math.Max(all.Size.Y, all.Size.Z)) : 0;
    }
}
