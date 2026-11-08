using GlassWorld.Contracts;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Objects;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// The core's objects on the island (main game, island mode): one MultiMesh per object look from
/// <c>content/look/objects.json</c>, each object on the drawn ground under its core position, lying
/// objects tilted to its slope (<see cref="ObjectLooks.Place"/>). The view is
/// rebuilt only when the object set changes (<see cref="ObjectLooks.Signature"/>). A type without an entry
/// is drawn as the calm fallback marker and logged once. Presentation only: it never writes core state.
/// </summary>
public partial class ObjectsView : Node3D
{
    private readonly Dictionary<string, MultiMeshInstance3D> _layers = new(StringComparer.Ordinal);
    private ObjectLooks _looks = null!;
    private Func<double, double, double> _ground = (_, _) => 0;
    private long _signature = long.MinValue;

    /// <summary>Objects drawn after the last refresh (none-shapes excluded).</summary>
    public int Drawn { get; private set; }

    /// <summary>Number of visuals with a mesh.</summary>
    public int Layers => _layers.Count;

    /// <summary>Creates the view: one mesh per visual in palette colours, the comic shader and the ink outline.</summary>
    public static ObjectsView Create(LookObjects data, LookPalette palette, Func<double, double, double> ground)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(ground);
        var colors = palette.Colors.ToDictionary(c => c.Id, c => new Color(c.Color), StringComparer.Ordinal);
        var view = new ObjectsView { Name = "Objects", _looks = new ObjectLooks(data, colors.Keys.ToHashSet(StringComparer.Ordinal)), _ground = ground };
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/warm_comic.gdshader") };
        material.SetShaderParameter("use_vertex_color", true);
        material.SetShaderParameter("use_regions", false);
        if (data.Outline && data.OutlineWidthM > 0)
        {
            var ink = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/outline_hull.gdshader") };
            ink.SetShaderParameter("width", (float)data.OutlineWidthM);
            material.NextPass = ink;
        }

        foreach (var look in view._looks.Visuals.Where(v => v.Shape != ObjectLookShape.None))
        {
            var mesh = ObjectShapes.Build(look.Shape, role => colors[look.Color(role)].SrgbToLinear());
            var layer = new MultiMeshInstance3D
            {
                Name = look.VisualId,
                Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = 0 },
                MaterialOverride = material,
            };
            view.AddChild(layer);
            view._layers[look.VisualId] = layer;
        }

        return view;
    }

    /// <summary>Redraws when the objects changed since the last call; returns whether it redrew.</summary>
    public bool Refresh(IEnumerable<WorldObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var list = objects as IReadOnlyCollection<WorldObject> ?? [.. objects];
        var signature = ObjectLooks.Signature(list);
        if (signature == _signature)
        {
            return false;
        }

        _signature = signature;
        var layout = _looks.Layout(list, _ground, type => GD.Print($"OBJECT_LOOK_FALLBACK type={type} (no entry in content/look/objects.json, drawn as the neutral marker)"));
        Drawn = 0;
        foreach (var (id, layer) in _layers)
        {
            var placed = layout.TryGetValue(id, out var items) ? items : [];
            var multi = layer.Multimesh;
            multi.InstanceCount = placed.Count;
            for (var i = 0; i < placed.Count; i++)
            {
                var p = placed[i];
                // Turned, sized and (lying objects) tilted to the ground by the Godot-free placement (D576).
                var (ax, ay, az) = p.Axes();
                var basis = new Basis(new Vector3((float)ax.X, (float)ax.Y, (float)ax.Z), new Vector3((float)ay.X, (float)ay.Y, (float)ay.Z), new Vector3((float)az.X, (float)az.Y, (float)az.Z));
                multi.SetInstanceTransform(i, new Transform3D(basis, new Vector3((float)p.X, (float)p.Y, (float)p.Z)));
            }

            layer.Visible = placed.Count > 0;
            Drawn += placed.Count;
        }

        return true;
    }
}
