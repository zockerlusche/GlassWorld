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
/// is drawn as the calm fallback marker and logged once. Decorative plants (<see cref="Decor"/>) are drawn
/// the same way next to the core's objects. Tree crowns near the line from the camera to a followed figure
/// fade (W1689, <see cref="FadeCrowns"/>): such a tree leaves its layer for a small see-through layer.
/// Presentation only: it never writes core state.
/// </summary>
public partial class ObjectsView : Node3D
{
    private readonly Dictionary<string, MultiMeshInstance3D> _layers = new(StringComparer.Ordinal);
    private ObjectLooks _looks = null!;
    private Func<double, double, double> _ground = (_, _) => 0;
    private long _signature = long.MinValue;
    private IReadOnlyList<WorldObject> _decor = [];
    private readonly Dictionary<string, CrownLayer> _crowns = new(StringComparer.Ordinal);
    private FigureCrownFade? _fade;

    /// <summary>Decorative plants drawn with the objects (presentation only).</summary>
    public int DecorCount => _decor.Count;

    /// <summary>Objects drawn after the last refresh (none-shapes excluded).</summary>
    public int Drawn { get; private set; }

    /// <summary>Number of visuals with a mesh.</summary>
    public int Layers => _layers.Count;

    /// <summary>Creates the view: one mesh per visual in palette colours, the comic shader and the ink outline.</summary>
    public static ObjectsView Create(LookObjects data, LookPalette palette, Func<double, double, double> ground, IReadOnlyList<WorldObject>? decor = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(ground);
        var colors = palette.Colors.ToDictionary(c => c.Id, c => new Color(c.Color), StringComparer.Ordinal);
        var view = new ObjectsView { Name = "Objects", _looks = new ObjectLooks(data, colors.Keys.ToHashSet(StringComparer.Ordinal)), _ground = ground, _decor = decor ?? [] };
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

    /// <summary>Faded crowns now (smoke and tests).</summary>
    public int CrownsFaded { get; private set; }

    /// <summary>Sets up the crown fade of the data (W1689): one see-through layer per tree visual.</summary>
    public void SetupCrownFade(FigureCrownFade data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _fade = data;
        if (!data.Enabled)
        {
            return;
        }

        var material = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            CullMode = BaseMaterial3D.CullModeEnum.Back,
        };
        foreach (var id in data.Visuals)
        {
            if (!_layers.TryGetValue(id, out var layer))
            {
                continue;
            }

            var faded = new MultiMeshInstance3D
            {
                Name = id + "_faded",
                Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = layer.Multimesh.Mesh, InstanceCount = 0 },
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(faded);
            _crowns[id] = new CrownLayer(layer, faded);
        }
    }

    /// <summary>
    /// Fades the crowns between the camera and the figures (head points) and brings back the others. Cheap:
    /// only trees in the box around a line are measured, and layers change only when the faded set does.
    /// </summary>
    public void FadeCrowns(Vector3 camera, IReadOnlyList<Vector3> figures)
    {
        ArgumentNullException.ThrowIfNull(figures);
        if (_fade is not { Enabled: true } fade)
        {
            return;
        }

        var faded = 0;
        var eye = new Point3(camera.X, camera.Y, camera.Z);
        foreach (var crown in _crowns.Values)
        {
            crown.Next.Clear();
            for (var i = 0; i < crown.Placed.Count; i++)
            {
                var t = crown.Placed[i];
                var top = t.Origin + (t.Basis.Y * (float)fade.CrownHeightM);
                var point = new Point3(top.X, top.Y, top.Z);
                var alpha = 1.0;
                foreach (var f in figures)
                {
                    var figure = new Point3(f.X, f.Y, f.Z);
                    if (CrownFade.NearLine(fade, point, eye, figure))
                    {
                        alpha = Math.Min(alpha, CrownFade.Alpha(fade, point, eye, figure));
                    }
                }

                if (alpha < 1)
                {
                    // In steps of 5 %, so a slowly moving camera does not rebuild the layer every frame.
                    crown.Next[i] = (float)(Math.Round(alpha * 20) / 20);
                }
            }

            faded += crown.Next.Count;
            if (!crown.Changed())
            {
                continue;
            }

            foreach (var i in crown.Shown.Keys)
            {
                if (!crown.Next.ContainsKey(i))
                {
                    crown.Layer.Multimesh.SetInstanceTransform(i, crown.Placed[i]);
                }
            }

            var multi = crown.Faded.Multimesh;
            multi.InstanceCount = crown.Next.Count;
            var n = 0;
            foreach (var (i, alpha) in crown.Next)
            {
                crown.Layer.Multimesh.SetInstanceTransform(i, new Transform3D(new Basis(Vector3.Zero, Vector3.Zero, Vector3.Zero), crown.Placed[i].Origin));
                multi.SetInstanceTransform(n, crown.Placed[i]);
                multi.SetInstanceColor(n, new Color(1, 1, 1, alpha));
                n++;
            }

            crown.Keep();
        }

        CrownsFaded = faded;
    }

    /// <summary>Redraws when the objects changed since the last call; returns whether it redrew.</summary>
    public bool Refresh(IEnumerable<WorldObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        // Only the core's objects are hashed each call; the decorative plants never change, so the combined
        // list is built only when the objects did (review GW3 #134: no 6-10k list and hash per frame).
        var core = objects as IReadOnlyCollection<WorldObject> ?? [.. objects];
        var signature = ObjectLooks.Signature(core);
        if (signature == _signature)
        {
            return false;
        }

        _signature = signature;
        IReadOnlyCollection<WorldObject> list = _decor.Count == 0 ? core : [.. core, .. _decor];
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

            if (_crowns.TryGetValue(id, out var crown))
            {
                crown.Reset(multi);
            }

            layer.Visible = placed.Count > 0;
            Drawn += placed.Count;
        }

        return true;
    }

    /// <summary>A tree layer and its see-through twin: which trees are faded now and at what opacity.</summary>
    private sealed class CrownLayer(MultiMeshInstance3D layer, MultiMeshInstance3D faded)
    {
        public MultiMeshInstance3D Layer { get; } = layer;

        public MultiMeshInstance3D Faded { get; } = faded;

        public List<Transform3D> Placed { get; } = [];

        public Dictionary<int, float> Shown { get; } = [];

        public Dictionary<int, float> Next { get; } = [];

        /// <summary>The layer was redrawn: every tree is back in it, nothing is faded.</summary>
        public void Reset(MultiMesh multi)
        {
            Placed.Clear();
            for (var i = 0; i < multi.InstanceCount; i++)
            {
                Placed.Add(multi.GetInstanceTransform(i));
            }

            Shown.Clear();
            Faded.Multimesh.InstanceCount = 0;
        }

        public bool Changed()
        {
            if (Next.Count != Shown.Count)
            {
                return true;
            }

            foreach (var (i, alpha) in Next)
            {
                if (!Shown.TryGetValue(i, out var shown) || shown != alpha)
                {
                    return true;
                }
            }

            return false;
        }

        public void Keep()
        {
            Shown.Clear();
            foreach (var (i, alpha) in Next)
            {
                Shown[i] = alpha;
            }
        }
    }
}
