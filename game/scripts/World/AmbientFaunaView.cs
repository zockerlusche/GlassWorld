using GlassWorld.Contracts;
using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// The ambient animals near the camera (A1383, B5-6 R16): one multimesh per animal of content/fauna/ambient.json - flat
/// wings that beat, small bodies, glowing dots - placed each frame by <see cref="AmbientFauna"/>. Presentation only,
/// no collision, no state; it stands still while the world is paused.
/// </summary>
public partial class AmbientFaunaView : Node3D
{
    private readonly Dictionary<string, (AmbientAnimal Look, MultiMeshInstance3D Instance)> _kinds = new(StringComparer.Ordinal);
    private FaunaAmbient _data = null!;
    private Func<(double X, double Z)?> _target = null!;
    private Func<IReadOnlyList<SoundForAnimal>> _living = null!;
    private Func<double, double, string?> _biomeAt = null!;
    private Func<double, double, double> _ground = null!;
    private ulong _seed;
    private double _seconds;

    /// <summary>Whether the world stands still (pause, speed 0): the animals do too (W1688).</summary>
    public bool Frozen { get; set; }

    /// <summary>How many animals were drawn in the last frame (checks).</summary>
    public int Shown { get; private set; }

    /// <summary>Creates the view.</summary>
    public static AmbientFaunaView Create(FaunaAmbient data, ulong seed, Func<(double X, double Z)?> target, Func<IReadOnlyList<SoundForAnimal>> living, Func<double, double, string?> biomeAt, Func<double, double, double> ground)
    {
        ArgumentNullException.ThrowIfNull(data);
        var view = new AmbientFaunaView { Name = "AmbientFauna", _data = data, _seed = seed, _target = target, _living = living, _biomeAt = biomeAt, _ground = ground };
        foreach (var look in data.Animals)
        {
            var material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = Color.FromHtml(look.Color),
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            if (look.Shape == AmbientAnimalShape.Glow)
            {
                material.EmissionEnabled = true;
                material.Emission = Color.FromHtml(look.Color);
                material.EmissionEnergyMultiplier = 2;
            }

            var size = (float)look.SizeM;
            Mesh mesh = look.Shape switch
            {
                AmbientAnimalShape.Wings => new BoxMesh { Size = new Vector3(size, size * 0.04f, size * 0.35f), Material = material },
                AmbientAnimalShape.Glow => new SphereMesh { Radius = size / 2, Height = size, RadialSegments = 6, Rings = 3, Material = material },
                _ => new BoxMesh { Size = new Vector3(size * 0.5f, size * 0.35f, size), Material = material },
            };
            var instance = new MultiMeshInstance3D
            {
                Name = look.Animal,
                Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh, InstanceCount = 0 },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            view.AddChild(instance);
            view._kinds[look.Animal] = (look, instance);
        }

        return view;
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (!Frozen)
        {
            _seconds += delta;
        }

        if (_target() is not { } target)
        {
            Shown = 0;
            return;
        }

        var critters = AmbientFauna.At(_data, _living(), target.X, target.Z, _seconds, _seed, _biomeAt, _ground);
        Shown = critters.Count;
        var byAnimal = critters.ToLookup(c => c.Animal, StringComparer.Ordinal);
        foreach (var (animal, kind) in _kinds)
        {
            // Every kind each frame: one that is not here now shows none.
            var list = byAnimal[animal].ToList();
            var multimesh = kind.Instance.Multimesh;
            multimesh.InstanceCount = list.Count;
            for (var i = 0; i < list.Count; i++)
            {
                var c = list[i];
                var scale = (float)Math.Max(0.001, c.Scale);
                var basis = new Basis(Vector3.Up, (float)c.Yaw).Scaled(new Vector3(scale * (float)(0.25 + (0.75 * c.Wing)), scale, scale));
                multimesh.SetInstanceTransform(i, new Transform3D(basis, new Vector3((float)c.X, (float)c.Y, (float)c.Z)));
            }
        }
    }
}
