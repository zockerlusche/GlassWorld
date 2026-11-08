using GlassWorld.Contracts;
using GlassWorld.Simulation.Random;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>
/// How an inhabitant looks (B2 R27): a core record drawn from the value ranges in
/// <c>content/look/appearance.json</c>, so the same seed always gives the same people. The game only
/// draws it; voice and other presentations read the body values from here. Inheritance follows in B9.
/// </summary>
/// <param name="HeightCm">Body height in centimetres (adult; the age stage scales it).</param>
/// <param name="BuildPermille">Build from slim (0) to sturdy (1000).</param>
/// <param name="SkinTone">Skin colour as #rrggbb.</param>
/// <param name="HairStyle">Hair style id.</param>
/// <param name="HairColor">Hair colour as #rrggbb.</param>
/// <param name="ClothingStyle">Clothing style id (always dressed, B2472).</param>
/// <param name="TopColor">Colour of the upper clothing as #rrggbb.</param>
/// <param name="BottomColor">Colour of the lower clothing as #rrggbb.</param>
/// <param name="AgeStage">Age stage id from the data.</param>
/// <param name="Shapes">Body shape weights 0..1 by shape id (ordinal order; most people have none).</param>
/// <param name="HeadScale">Head size factor.</param>
public sealed record Appearance(
    int HeightCm,
    int BuildPermille,
    string SkinTone,
    string HairStyle,
    string HairColor,
    string ClothingStyle,
    string TopColor,
    string BottomColor,
    string AgeStage,
    IReadOnlyDictionary<string, double> Shapes,
    double HeadScale)
{
    /// <summary>Draws an appearance from the data ranges with the given stream.</summary>
    public static Appearance Draw(LookAppearance data, Pcg32 rng, int ageDays)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(rng);
        var stages = data.AgeStages.OrderBy(s => s.FromAgeDays).ToList();
        var stage = stages.LastOrDefault(s => s.FromAgeDays <= ageDays) ?? stages[0];
        var oldest = ReferenceEquals(stage, stages[^1]) && stages.Count > 1;

        int Between(int min, int max) => min + rng.NextInt(0, (max - min) + 1);
        double Range(double min, double max) => min + ((max - min) * rng.NextDouble());
        string Pick(ICollection<string> values) => values.ElementAt(rng.NextInt(0, values.Count));

        var height = Between(data.HeightCm.Min, data.HeightCm.Max);
        var build = Between(data.BuildPermille.Min, data.BuildPermille.Max);
        var skin = Pick(data.SkinTones);
        var hairStyle = Pick(data.HairStyles);
        var hair = Pick(data.HairColors);
        var elderHair = data.ElderHairColors.Count > 0 ? Pick(data.ElderHairColors) : hair;
        var clothing = Pick(data.ClothingStyles);
        var top = Pick(data.ClothingColors);
        var bottom = Pick(data.ClothingColors);
        var shapes = new SortedDictionary<string, double>(StringComparer.Ordinal);
        if (data.Shapes.Count > 0 && rng.NextInt(0, 1000) < data.ShapeChancePermille)
        {
            var shape = data.Shapes.ElementAt(rng.NextInt(0, data.Shapes.Count));
            shapes[shape.Id] = Math.Round(Range(shape.Min, shape.Max), 3);
        }

        var head = Math.Round(Range(data.HeadScale.Min, data.HeadScale.Max), 3);

        // Every value is drawn in the same order whatever the stage, so a later stage change (B9) does
        // not shift the other values; the oldest stage wears the elder hair colour.
        return new Appearance(height, build, skin, hairStyle, oldest ? elderHair : hair, clothing, top, bottom, stage.Id, shapes, head);
    }

    /// <summary>Writes the record in a fixed order.</summary>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(HeightCm).Write(BuildPermille).Write(SkinTone).Write(HairStyle).Write(HairColor)
            .Write(ClothingStyle).Write(TopColor).Write(BottomColor).Write(AgeStage).Write(Shapes.Count);
        foreach (var (id, weight) in Shapes.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            hasher.Write(id).Write(weight);
        }

        hasher.Write(HeadScale);
    }
}
