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

        // Every shape of the kit data is drawn once (a shape added to kit.json without a mesh fails here).
        foreach (var shape in kit.Shapes)
        {
            var single = new BuildPlan();
            single.Parts.Add(new BuildPart
            {
                Shape = shape,
                SizeM = new SizeM2 { X = 0.4, Y = 0.2, Z = 0.3 },
                PositionM = new PositionM { X = 0, Y = 0.1, Z = 0 },
                Material = "check",
                Color = kit.Colors.First().Id,
                Step = 1,
            });
            single.Parts.Add(new BuildPart
            {
                Shape = shape,
                SizeM = new SizeM2 { X = 0.2, Y = 0.4, Z = 0.2 },
                PositionM = new PositionM { X = 0.5, Y = 0.2, Z = 0 },
                Material = "check",
                Color = kit.Colors.Last().Id,
                Step = 2,
            });
            try
            {
                var node = BuildPlanNode.Create(single, kit);
                AddChild(node);
                if (node.PartCount != 2)
                {
                    failures.Add($"kit shape '{shape}': {node.PartCount} meshes for 2 parts");
                }

                seenShapes.Add(shape);
            }
            catch (InvalidDataException e)
            {
                failures.Add($"kit shape '{shape}': {e.Message}");
            }
        }

        CheckInventionsView(kit, files, failures);

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

    /// <summary>
    /// The inventions in the running world (A1375): a building site shows the parts of its finished steps, a built
    /// thing all of them, on the ground; a site that is finished becomes the thing; what is gone is taken away.
    /// </summary>
    private void CheckInventionsView(BuildKit kit, IReadOnlyList<string> files, List<string> failures)
    {
        var file = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith("job-status.build-plan-", StringComparison.Ordinal));
        if (file is null || ContractSerializer.Deserialize<JobStatus>(File.ReadAllText(file)).Result?.BuildPlan is not { } plan)
        {
            failures.Add("inventions view: no example building plan");
            return;
        }

        // As the core keeps a plan (BuildTask.PlanJson).
        var json = System.Text.Json.JsonSerializer.Serialize(plan);
        var all = plan.Parts.Count;
        var firstStep = plan.Parts.Count(p => p.Step <= 1);
        var view = InventionsView.Create(kit, (_, _) => 2.0);
        AddChild(view);
        const string Type = "inv_check";
        var site = new Presentation.World.InventionShown("site:" + Type, Type, json, 3, 4, 1, false);
        view.Refresh([site]);
        if (view.Drawn != 1 || view.VisibleParts != firstStep)
        {
            failures.Add($"inventions view: a site after step 1 shows {view.VisibleParts} of {firstStep} parts in {view.Drawn} drawings");
        }

        var thing = new Presentation.World.InventionShown("obj-1", Type, json, 3, 4, int.MaxValue, true);
        view.Refresh([thing]);
        var node = view.GetChildren().OfType<BuildPlanNode>().FirstOrDefault(n => !n.IsQueuedForDeletion());
        if (view.Drawn != 1 || view.VisibleParts != all || node is null || !Mathf.IsEqualApprox(node.Position.Y, 2f))
        {
            failures.Add($"inventions view: the built thing shows {view.VisibleParts} of {all} parts in {view.Drawn} drawings, on the ground: {node?.Position.Y}");
        }

        view.Refresh([]);
        if (view.Drawn != 0)
        {
            failures.Add($"inventions view: {view.Drawn} drawings left after the things are gone");
        }

        GD.Print($"INVENTIONS_VIEW_OK site_parts={firstStep} thing_parts={all}");
    }
}
