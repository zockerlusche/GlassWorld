using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using Godot;

namespace GlassWorld.Game.Build;

/// <summary>
/// Window run for D471 D6 screenshots: the building plans of the check data stand on a plain ground in
/// warm light and are built step by step; with <c>-- --shots=DIR</c> one picture per step is saved and
/// the run quits. Presentation only.
/// </summary>
public partial class BuildPlanView : Node3D
{
    private readonly List<BuildPlanNode> _nodes = [];
    private string? _shots;
    private int _step;
    private int _frames;

    /// <inheritdoc/>
    public override void _Ready()
    {
        _shots = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--shots=", StringComparison.Ordinal))?["--shots=".Length..];
        var content = ContentLocator.ContentDirectory();
        var kit = ContractSerializer.Deserialize<BuildKit>(File.ReadAllText(Path.Combine(content, "build", "kit.json")));
        var repository = Path.GetDirectoryName(content)!;
        var files = Directory.GetFiles(ProjectSettings.GlobalizePath("res://build/check"), "*.json")
            .Concat(Directory.GetFiles(Path.Combine(repository, "contracts", "examples", "valid"), "job-status.build-plan-*.json"))
            .ToList();
        var x = 0f;
        foreach (var file in files)
        {
            var plan = ContractSerializer.Deserialize<JobStatus>(File.ReadAllText(file)).Result!.BuildPlan!;
            var node = BuildPlanNode.Create(plan, kit);
            node.Position = new Vector3(x, 0, 0);
            node.ShowUpToStep(0);
            AddChild(node);
            _nodes.Add(node);
            x += 3.2f;
        }

        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.51f, 0.75f, 0.78f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(1f, 0.97f, 0.92f),
            AmbientLightEnergy = 0.42f,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        AddChild(new WorldEnvironment { Environment = environment });
        AddChild(new DirectionalLight3D
        {
            LightEnergy = 0.6f,
            LightColor = new Color(1f, 0.96f, 0.88f),
            ShadowEnabled = true,
            RotationDegrees = new Vector3(-50, 35, 0),
        });
        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(40, 20) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.86f, 0.66f) },
        });
        var camera = new Camera3D { Fov = 40 };
        AddChild(camera);
        var centre = new Vector3(x / 2f - 1.6f, 0.25f, 0.4f);
        camera.Position = centre + new Vector3(0, 2.6f, 5.4f);
        camera.LookAt(centre, Vector3.Up);
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (++_frames < 20)
        {
            return;
        }

        _frames = 0;
        if (_shots is not null)
        {
            Directory.CreateDirectory(_shots);
            var size = GetViewport().GetTexture().GetSize();
            var file = Path.Combine(_shots, $"build_step{_step}_{size.X}x{size.Y}.png");
            GetViewport().GetTexture().GetImage().SavePng(file);
            GD.Print($"BUILD_SHOT {file}");
        }

        var last = _nodes.Count == 0 ? 0 : _nodes.Max(n => n.LastStep);
        if (_step >= last)
        {
            GetTree().Quit();
            return;
        }

        _step++;
        foreach (var node in _nodes)
        {
            node.ShowUpToStep(_step);
        }
    }
}
