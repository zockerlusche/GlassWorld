using GlassWorld.Contracts;

namespace GlassWorld.Simulation.Inventions;

/// <summary>
/// The core's check of a building plan from the builder (B4b R14, D471 stage 5), deterministic and only
/// against data: the construction kit, the build settings and the approved invention. Returns the reasons
/// (sorted codes) why a plan is refused; empty when it may be built.
/// </summary>
public static class BuildPlanCheck
{
    /// <summary>Fewer than two or more parts than allowed.</summary>
    public const string PartCount = "part_count";

    /// <summary>A shape that is not in the construction kit.</summary>
    public const string UnknownShape = "unknown_shape";

    /// <summary>A colour that is not in the construction kit.</summary>
    public const string UnknownColor = "unknown_color";

    /// <summary>A part made of something the invention does not name as material.</summary>
    public const string MaterialNotInInvention = "material_not_in_invention";

    /// <summary>Steps are not numbered 1..n without gaps.</summary>
    public const string StepsNotContinuous = "steps_not_continuous";

    /// <summary>A part is larger than the kit allows.</summary>
    public const string PartTooLarge = "part_too_large";

    /// <summary>All parts together are larger than the invention's size allows.</summary>
    public const string OutOfBounds = "out_of_bounds";

    /// <summary>Checks <paramref name="plan"/> for <paramref name="invention"/>.</summary>
    public static IReadOnlyList<string> Check(BuildPlan plan, Invention invention, BuildKit kit, BuildSettings settings, int maxParts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(invention);
        ArgumentNullException.ThrowIfNull(kit);
        ArgumentNullException.ThrowIfNull(settings);
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var parts = plan.Parts.ToList();
        if (parts.Count < 2 || parts.Count > Math.Min(maxParts, kit.MaxParts))
        {
            reasons.Add(PartCount);
        }

        var colors = kit.Colors.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var materials = invention.Materials.Concat(invention.Steps.SelectMany(s => s.Materials)).ToHashSet(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            if (!kit.Shapes.Contains(part.Shape, StringComparer.Ordinal))
            {
                reasons.Add(UnknownShape);
            }

            if (!colors.Contains(part.Color))
            {
                reasons.Add(UnknownColor);
            }

            if (!materials.Contains(part.Material))
            {
                reasons.Add(MaterialNotInInvention);
            }

            if (Math.Max(part.SizeM.X, Math.Max(part.SizeM.Y, part.SizeM.Z)) > kit.MaxPartSizeM)
            {
                reasons.Add(PartTooLarge);
            }
        }

        var steps = parts.Select(p => p.Step).Distinct().Order().ToList();
        if (steps.Count == 0 || steps[0] != 1 || steps[^1] != steps.Count)
        {
            reasons.Add(StepsNotContinuous);
        }

        if (parts.Count > 0)
        {
            // Box around all parts; a part turned about y may reach out to half its horizontal diagonal.
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (var part in parts)
            {
                var reach = part.RotationYDeg % 180 == 0
                    ? (X: part.SizeM.X / 2, Z: part.SizeM.Z / 2)
                    : (X: Math.Sqrt((part.SizeM.X * part.SizeM.X) + (part.SizeM.Z * part.SizeM.Z)) / 2, Z: Math.Sqrt((part.SizeM.X * part.SizeM.X) + (part.SizeM.Z * part.SizeM.Z)) / 2);
                minX = Math.Min(minX, part.PositionM.X - reach.X);
                maxX = Math.Max(maxX, part.PositionM.X + reach.X);
                minZ = Math.Min(minZ, part.PositionM.Z - reach.Z);
                maxZ = Math.Max(maxZ, part.PositionM.Z + reach.Z);
                minY = Math.Min(minY, part.PositionM.Y - (part.SizeM.Y / 2));
                maxY = Math.Max(maxY, part.PositionM.Y + (part.SizeM.Y / 2));
            }

            var f = settings.BoundsTolerance;
            var size = invention.SizeM;
            if (maxX - minX > size.X * f || maxY - minY > size.Y * f || maxZ - minZ > size.Z * f)
            {
                reasons.Add(OutOfBounds);
            }
        }

        return [.. reasons];
    }
}
