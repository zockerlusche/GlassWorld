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

    /// <summary>Things and sites drawn now.</summary>
    public int Drawn => _drawn.Count;

    /// <summary>Parts visible now, over all drawings.</summary>
    public int VisibleParts => _drawn.Values.Sum(d => d.Node.VisibleParts);

    /// <summary>Creates the view with the construction kit and the drawn ground height (x, z) in metres.</summary>
    public static InventionsView Create(BuildKit kit, Func<double, double, double> ground) =>
        new() { Name = "Inventions", _kit = kit ?? throw new ArgumentNullException(nameof(kit)), _ground = ground ?? throw new ArgumentNullException(nameof(ground)) };

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
            drawn.Node.ShowUpToStep(s.Finished ? drawn.Node.LastStep : s.Step);
        }

        foreach (var key in _drawn.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            var node = _drawn[key].Node;
            RemoveChild(node);
            node.QueueFree();
            _drawn.Remove(key);
        }

        GD.Print($"INVENTIONS_DRAWN things={shown.Count(s => s.Finished)} sites={shown.Count(s => !s.Finished)} parts={VisibleParts}");
        return true;
    }
}
