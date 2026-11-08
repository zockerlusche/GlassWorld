using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Presentation.World;

/// <summary>
/// How one inhabitant is drawn, resolved from its appearance record (core, B2 R27) and the figure look
/// data, without Godot: body model, hair mesh, colours (#rrggbb), sleeves and legs, scale, body shapes.
/// </summary>
public sealed record FigureSpec(
    string Model,
    string HairMesh,
    string Skin,
    string Top,
    string Bottom,
    string Shoes,
    string HairColor,
    bool ShortSleeves,
    bool Shorts,
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
            ?? throw new InvalidDataException($"figures.json has no hair mesh for style '{appearance.HairStyle}'.");
        var clothing = figures.Clothing.FirstOrDefault(c => c.Style == appearance.ClothingStyle)
            ?? throw new InvalidDataException($"figures.json has no clothing entry for style '{appearance.ClothingStyle}'.");
        var stage = look.AgeStages.FirstOrDefault(s => s.Id == appearance.AgeStage)
            ?? throw new InvalidDataException($"appearance.json has no age stage '{appearance.AgeStage}'.");

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
            clothing.ShortSleeves,
            clothing.Shorts,
            scale,
            appearance.Shapes,
            appearance.HeadScale,
            oldest);
    }
}
