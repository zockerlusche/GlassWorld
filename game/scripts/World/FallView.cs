using GlassWorld.Contracts;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// Small things falling near the camera (B2-5: leaves in autumn, R21; rain and snow, R23), from a
/// <see cref="FallParticles"/> entry of the look data: cheap GPU particles in a box that follows the camera
/// target, never in the rest of the world. The amount (0..1) thins them without a restart; at 0 nothing is
/// emitted or drawn. They drift with the shown wind. Tumbling particles (leaves, flakes) are quads facing
/// the camera, the others (rain) streaks along their fall. Unlit and tinted by the day light, so they are
/// dark at night. Presentation only.
/// </summary>
public partial class FallView : GpuParticles3D
{
    private FallParticles _data = null!;
    private ParticleProcessMaterial _process = null!;
    private StandardMaterial3D _material = null!;
    private Color _color = Colors.White;
    private Color _light = Colors.White;
    private (int X, int Z) _driftShown = (int.MinValue, int.MinValue);

    /// <summary>The amount shown now (0..1).</summary>
    public double AmountShown { get; private set; }

    /// <summary>Creates the particles of an entry; they emit nothing until <see cref="Follow"/> gives an amount.</summary>
    public static FallView Create(string name, FallParticles data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var tumbling = data.SpinDegPerS > 0;
        var (width, length) = ((float)data.SizeM, (float)data.LengthM);
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            BillboardMode = tumbling ? BaseMaterial3D.BillboardModeEnum.Particles : BaseMaterial3D.BillboardModeEnum.Disabled,
            BillboardKeepScale = true,
            Transparency = data.Opacity < 1 ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
        };
        var process = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3((float)data.BoxM / 2, 0.5f, (float)data.BoxM / 2),
            Direction = Vector3.Down,
            Spread = 0,
            Gravity = Vector3.Zero,
            InitialVelocityMin = (float)data.FallMPerS * 0.85f,
            InitialVelocityMax = (float)data.FallMPerS,
            AngularVelocityMin = -(float)data.SpinDegPerS,
            AngularVelocityMax = (float)data.SpinDegPerS,
            AngleMin = tumbling ? -180 : 0,
            AngleMax = tumbling ? 180 : 0,
            ScaleMin = 0.7f,
            ScaleMax = 1.0f,
        };
        process.SetParticleFlag(ParticleProcessMaterial.ParticleFlags.AlignYToVelocity, !tumbling);
        var box = (float)data.BoxM;
        var reach = (float)((data.FallMPerS + data.DriftMPerS) * data.LifetimeS);
        var view = new FallView
        {
            Name = name,
            _data = data,
            _process = process,
            _material = material,
            Amount = data.MaxCount,
            AmountRatio = 0,
            Lifetime = data.LifetimeS,
            LocalCoords = false,
            Emitting = false,
            Visible = false,
            ProcessMaterial = process,
            DrawPass1 = tumbling ? new QuadMesh { Size = new Vector2(width, length), Material = material } : Streak(width, length, material),
            CastShadow = ShadowCastingSetting.Off,
            VisibilityAabb = new Aabb(new Vector3(-box - reach, -reach - 2, -box - reach), new Vector3(2 * (box + reach), reach + 4, 2 * (box + reach))),
        };
        return view;
    }

    /// <summary>The colour of the particles (palette colour) and the day light that tints them.</summary>
    public void Tint(Color color, Color light)
    {
        if (color == _color && light == _light)
        {
            return;
        }

        (_color, _light) = (color, light);
        _material.AlbedoColor = new Color(color.R * light.R, color.G * light.G, color.B * light.B, (float)_data.Opacity);
    }

    /// <summary>
    /// One frame: the box above <paramref name="focus"/> (the camera target on the ground), the amount 0..1
    /// and the shown wind (unit direction on the ground and strength 0..1).
    /// </summary>
    public void Follow(Vector3 focus, double amount, Vector2 windDirection, double windStrength)
    {
        AmountShown = Math.Clamp(amount, 0, 1);
        var on = AmountShown > 0.001;
        if (on != Emitting)
        {
            // Switched off, the particles in the air fall out; nothing new appears.
            Emitting = on;
        }

        if (!on)
        {
            return;
        }

        Visible = true;
        Position = focus + new Vector3(0, (float)_data.HeightM, 0);
        AmountRatio = (float)AmountShown;

        // The drift changes the direction of new particles only; set in steps, not every frame.
        var drift = windDirection * (float)(_data.DriftMPerS * Math.Clamp(windStrength, 0, 1));
        var step = ((int)MathF.Round(drift.X * 10), (int)MathF.Round(drift.Y * 10));
        if (step != _driftShown)
        {
            _driftShown = step;
            var velocity = new Vector3(drift.X, -(float)_data.FallMPerS, drift.Y);
            _process.Direction = velocity.Normalized();
            _process.InitialVelocityMin = velocity.Length() * 0.85f;
            _process.InitialVelocityMax = velocity.Length();
        }
    }

    /// <summary>Screenshot tool: the particles are in full fall at once (as if they had fallen for a lifetime).</summary>
    public void Settle()
    {
        Preprocess = Lifetime;
        Visible = Emitting;
        if (Emitting)
        {
            Restart();
        }
    }

    /// <summary>A streak along the fall (local y): two crossed quads, so it shows from every side.</summary>
    private static ArrayMesh Streak(float width, float length, Material material)
    {
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var side in new[] { Vector3.Right, Vector3.Back })
        {
            var (half, up) = (side * (width / 2), Vector3.Up * (length / 2));
            Vector3[] corners = [-half - up, half - up, half + up, -half - up, half + up, -half + up];
            foreach (var corner in corners)
            {
                tool.SetNormal(Vector3.Up);
                tool.AddVertex(corner);
            }
        }

        tool.SetMaterial(material);
        return tool.Commit();
    }
}
