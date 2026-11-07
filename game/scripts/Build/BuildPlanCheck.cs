using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using Godot;

namespace GlassWorld.Game.Build;

/// <summary>
/// Headless check for D471 D6: every building plan from the contract examples and the check data is
/// drawn as a node with exactly one mesh per part, every part lies inside the plan's bounds, and the
/// parts appear step by step. Run: <c>godot --headless --path game res://build/build_plan_check.tscn</c>
/// → <c>BUILD_PLAN_OK …</c>.
/// </summary>
public partial class BuildPlanCheck : Node3D
{
    private const float ToleranceM = 0.02f;

    /// <inheritdoc/>
    public override void _Ready()
    {
        var content = ContentLocator.ContentDirectory();
        var kit = ContractSerializer.Deserialize<BuildKit>(File.ReadAllText(Path.Combine(content, "build", "kit.json")));
        var repository = Path.GetDirectoryName(content)!;
        var files = Directory.GetFiles(Path.Combine(repository, "contracts", "examples", "valid"), "job-status.build-plan-*.json")
            .Concat(Directory.GetFiles(ProjectSettings.GlobalizePath("res://build/check"), "*.json"))
            .Order(StringComparer.Ordinal)
            .ToList();
        var failures = new List<string>();
        int plans = 0, parts = 0, shapes = 0;
        var seenShapes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var status = ContractSerializer.Deserialize<JobStatus>(File.ReadAllText(file));
            if (status.Result?.BuildPlan is not { } plan)
            {
                failures.Add($"{Path.GetFileName(file)}: no building plan");
                continue;
            }

            var node = BuildPlanNode.Create(plan, kit);
            AddChild(node);
            plans++;
            parts += plan.Parts.Count;
            if (node.PartCount != plan.Parts.Count)
            {
                failures.Add($"{Path.GetFileName(file)}: {node.PartCount} meshes for {plan.Parts.Count} parts");
            }

            foreach (var part in plan.Parts)
            {
                seenShapes.Add(part.Shape);
            }

            // Step by step: nothing before step 1, all parts after the last step, never fewer than before.
            node.ShowUpToStep(0);
            var visible = node.VisibleParts;
            if (visible != 0)
            {
                failures.Add($"{Path.GetFileName(file)}: {visible} parts visible before building");
            }

            for (var step = 1; step <= node.LastStep; step++)
            {
                node.ShowUpToStep(step);
                var expected = plan.Parts.Count(p => p.Step <= step);
                if (node.VisibleParts != expected)
                {
                    failures.Add($"{Path.GetFileName(file)}: step {step} shows {node.VisibleParts} of {expected} parts");
                }
            }

            // Each part's mesh fits its size from the plan (rotation about y aside).
            foreach (var mesh in node.GetChildren().OfType<MeshInstance3D>())
            {
                var box = mesh.GetAabb();
                var part = plan.Parts.OrderBy(p => p.Step).ElementAt(mesh.GetIndex());
                var want = new Vector3((float)part.SizeM.X, (float)part.SizeM.Y, (float)part.SizeM.Z);
                var have = box.Size;
                if (Mathf.Abs(have.Y - want.Y) > ToleranceM || Mathf.Max(have.X, have.Z) > Mathf.Max(want.X, want.Z) + ToleranceM)
                {
                    failures.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Path.GetFileName(file)}: part {mesh.Name} is {have} for size {want}"));
                }
            }
        }

        shapes = seenShapes.Count;
        var missing = kit.Shapes.Where(s => !seenShapes.Contains(s)).ToList();
        if (missing.Count > 0)
        {
            failures.Add($"kit shapes never checked: {string.Join(',', missing)}");
        }

        if (failures.Count > 0)
        {
            GD.PushError("BUILD_PLAN_FAIL " + string.Join("; ", failures));
        }

        GD.Print($"BUILD_PLAN_OK plans={plans} parts={parts} shapes={shapes} failures={failures.Count}");
        GetTree().Quit(failures.Count == 0 ? 0 : 1);
    }
}
