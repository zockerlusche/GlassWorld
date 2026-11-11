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
/// Every look has its own material of the one warm comic shader with the living nature switched on (B2-5
/// R21, R24): the meshes carry the shares of the seasonal colours, plants sway by their <c>sway_m</c>, and
/// the ink outline sways with them. The outline is pushed out along the directions the meshes carry
/// (<see cref="ObjectShapes"/>, TL #20750), so it sits on flat-shaded rocks too. A mesh with a second
/// surface (the dark inside of a rock niche) carries its materials itself: the look's material and the
/// unlit one of the inside. Presentation only: it never writes core state.
/// </summary>
public partial class ObjectsView : Node3D
{
    private static readonly Shader Comic = GD.Load<Shader>("res://shaders/warm_comic.gdshader");
    private static readonly Shader ComicFade = GD.Load<Shader>("res://shaders/warm_comic_fade.gdshader");
    private static readonly Shader Ink = GD.Load<Shader>("res://shaders/outline_hull.gdshader");
    private static readonly Shader NicheInside = GD.Load<Shader>("res://shaders/niche_inside.gdshader");

    private readonly Dictionary<string, MultiMeshInstance3D> _layers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ObjectLook> _byVisual = new(StringComparer.Ordinal);
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

    /// <summary>The object looks of the data (for the grass view, which draws its plants the same way).</summary>
    public ObjectLooks Looks => _looks;

    /// <summary>Mesh and material of a visual, or null if it has no mesh.</summary>
    public (Mesh Mesh, Material Material)? LayerOf(string visualId) =>
        _layers.TryGetValue(visualId, out var layer) ? (layer.Multimesh.Mesh, layer.MaterialOverride ?? layer.Multimesh.Mesh.SurfaceGetMaterial(0)) : null;

    /// <summary>Creates the view: one mesh per visual in palette colours, the comic shader and the ink outline.</summary>
    /// <param name="data">The object looks.</param>
    /// <param name="palette">The palette the meshes are built with.</param>
    /// <param name="ground">Ground height at a world position.</param>
    /// <param name="decor">Decorative plants drawn with the objects.</param>
    /// <param name="tintSlots">The tint slots of the nature look (seasons and look epochs), so meshes and shader parameters agree; null = the palette's seasons only.</param>
    /// <param name="streamSources">Where the stream bands of the island start at a spring (W1724): an object the data names for it is drawn there as a spring; null = none.</param>
    public static ObjectsView Create(LookObjects data, LookPalette palette, Func<double, double, double> ground, IReadOnlyList<WorldObject>? decor = null, SeasonSlots? tintSlots = null, IEnumerable<StreamSource>? streamSources = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(ground);
        var colors = palette.Colors.ToDictionary(c => c.Id, c => new Color(c.Color), StringComparer.Ordinal);
        var slots = tintSlots ?? SeasonSlots.From(palette);
        var view = new ObjectsView { Name = "Objects", _looks = new ObjectLooks(data, colors.Keys.ToHashSet(StringComparer.Ordinal)), _ground = ground, _decor = decor ?? [] };
        view._looks.UseStreamSources(streamSources ?? []);
        foreach (var look in view._looks.Visuals.Where(v => v.Shape != ObjectLookShape.None))
        {
            (Color First, Color More) Weights(int role)
            {
                var w = slots.WeightsOf(look.Color(role));
                return (new Color(w.A, w.B, w.C, w.D), new Color(w.E, w.F, w.G, w.H));
            }

            var mesh = ObjectShapes.Build(look.Shape, role => colors[look.Color(role)].SrgbToLinear(), Weights, look.Niche);
            var material = Land(Comic, look, mesh);
            if (data.Outline && data.OutlineWidthM > 0)
            {
                // The outline sways with its plant: the same sway values as the material it follows.
                var ink = new ShaderMaterial { Shader = Ink };
                ink.SetShaderParameter("width", (float)data.OutlineWidthM);
                ink.SetShaderParameter("use_hull_directions", true);
                Sway(ink, look, mesh);
                material.NextPass = ink;
            }

            // A second surface is the unlit inside of a rock niche: then the mesh carries both materials
            // (an override on the layer would paint the inside with the look's material too).
            var surfaces = mesh.GetSurfaceCount() > 1;
            if (surfaces)
            {
                mesh.SurfaceSetMaterial(0, material);
                mesh.SurfaceSetMaterial(1, new ShaderMaterial { Shader = NicheInside });
            }

            view._byVisual[look.VisualId] = look;
            var layer = new MultiMeshInstance3D
            {
                Name = look.VisualId,
                Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = 0 },
                MaterialOverride = surfaces ? null : material,
            };
            view.AddChild(layer);
            view._layers[look.VisualId] = layer;
        }

        return view;
    }

    /// <summary>A warm comic material of the land for a look: vertex colours, living nature on, the look's sway.</summary>
    private static ShaderMaterial Land(Shader shader, ObjectLook look, Mesh mesh)
    {
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("use_vertex_color", true);
        material.SetShaderParameter("use_regions", false);
        material.SetShaderParameter("use_nature", true);
        Sway(material, look, mesh);
        return material;
    }

    /// <summary>The sway of a look: how far its top moves in full wind, and the height of that top in the mesh.</summary>
    private static void Sway(ShaderMaterial material, ObjectLook look, Mesh mesh)
    {
        material.SetShaderParameter("sway_m", (float)look.SwayM);
        material.SetShaderParameter("sway_height_m", Math.Max(mesh.GetAabb().End.Y, 0.01f));
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

        foreach (var id in data.Visuals)
        {
            if (!_layers.TryGetValue(id, out var layer))
            {
                continue;
            }

            // The see-through twin keeps the look, the season colours and the sway of its tree (B2-5).
            var material = Land(ComicFade, _byVisual[id], layer.Multimesh.Mesh);
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
