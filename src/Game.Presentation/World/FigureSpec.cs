using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Presentation.World;

/// <summary>Which colour a body region under or beside the clothing shows (F4).</summary>
public enum RegionPaint
{
    /// <summary>Bare skin.</summary>
    Skin = 0,

    /// <summary>The top clothing colour.</summary>
    Top = 1,

    /// <summary>The bottom clothing colour.</summary>
    Bottom = 2,
}

/// <summary>
/// How one inhabitant is drawn, resolved from its appearance record (core, B2 R27) and the figure look
/// data, without Godot: body model, hair mesh (null = bald), the clothing part meshes of the outfit,
/// colours (#rrggbb), the paint of the body regions that clothing may cover, scale and blend shapes.
/// </summary>
public sealed record FigureSpec(
    string Model,
    string? HairMesh,
    string Skin,
    string Top,
    string Bottom,
    string Shoes,
    string HairColor,
    IReadOnlyList<string> ClothingMeshes,
    RegionPaint Forearm,
    RegionPaint Thigh,
    RegionPaint LowerThigh,
    RegionPaint Shin,
    double Scale,
    IReadOnlyDictionary<string, double> Shapes,
    double HeadScale,
    bool Elder)
{
    /// <summary>Resolves the figure for an inhabitant; missing table entries are data errors.</summary>
    public static FigureSpec From(Appearance appearance, string sex, LookFigures figures, LookAppearance look)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(sex);
        ArgumentNullException.ThrowIfNull(figures);
        ArgumentNullException.ThrowIfNull(look);
        var body = figures.Bodies.FirstOrDefault(b => b.AgeStage == appearance.AgeStage && b.Sex == sex)
            ?? figures.Bodies.FirstOrDefault(b => b.AgeStage == appearance.AgeStage && b.Sex == "any")
            ?? throw new InvalidDataException($"figures.json has no body for age stage '{appearance.AgeStage}' and sex '{sex}'.");
        var hair = figures.Hair.FirstOrDefault(h => h.Style == appearance.HairStyle)
            ?? throw new InvalidDataException($"figures.json has no hair entry for style '{appearance.HairStyle}'.");
        var outfit = look.Outfits.FirstOrDefault(o => o.Id == appearance.ClothingStyle)
            ?? throw new InvalidDataException($"appearance.json has no outfit '{appearance.ClothingStyle}'.");
        var parts = outfit.Parts
            .Select(id => figures.ClothingParts.FirstOrDefault(p => p.Id == id)
                ?? throw new InvalidDataException($"figures.json has no clothing part '{id}' (outfit '{outfit.Id}')."))
            .ToList();
        var stage = look.AgeStages.FirstOrDefault(s => s.Id == appearance.AgeStage)
            ?? throw new InvalidDataException($"appearance.json has no age stage '{appearance.AgeStage}'.");

        // A region under a part takes that part's colour (the body never shows through); else skin.
        RegionPaint Paint(string region)
        {
            var part = parts.LastOrDefault(p => p.Covers.Contains(region, StringComparer.Ordinal));
            return part is null ? RegionPaint.Skin : part.Color == FigureClothingPartColor.Top ? RegionPaint.Top : RegionPaint.Bottom;
        }

        // Height in the record is the grown-up height; the age stage scales it (a child is smaller).
        var scale = appearance.HeightCm * stage.Scale / body.ModelHeightCm;
        var oldest = look.AgeStages.OrderBy(s => s.FromAgeDays).Last().Id == appearance.AgeStage;
        return new FigureSpec(
            body.Model,
            hair.Mesh,
            appearance.SkinTone,
            appearance.TopColor,
            appearance.BottomColor,
            figures.ShoeColor,
            appearance.HairColor,
            [.. parts.Select(p => p.Mesh)],
            Paint("forearm"),
            Paint("thigh"),
            Paint("lower_thigh"),
            Paint("shin"),
            scale,
            appearance.Shapes,
            appearance.HeadScale,
            oldest);
    }
}
