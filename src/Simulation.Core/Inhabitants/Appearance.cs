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
/// <param name="HairStyle">Hair style id (F4 hair kit; bald has no hair mesh).</param>
/// <param name="HairColor">Hair colour as #rrggbb.</param>
/// <param name="ClothingStyle">Outfit id: a set of clothing parts that fits the place and time of the start (F4; always dressed, B2472).</param>
/// <param name="TopColor">Colour of the upper clothing as #rrggbb.</param>
/// <param name="BottomColor">Colour of the lower clothing as #rrggbb.</param>
/// <param name="AgeStage">Age stage id from the data.</param>
/// <param name="Shapes">Blend shape weights 0..1 by id (ordinal order): body shapes (most people have none) and the sex proportions of the age stage (F4).</param>
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
    /// <summary>
    /// Draws an appearance from the data ranges with the given stream. <paramref name="sex"/> keeps the
    /// matching sex proportions; <paramref name="place"/> says which outfits fit (season, climate,
    /// biome, tech level).
    /// </summary>
    public static Appearance Draw(LookAppearance data, Pcg32 rng, int ageDays, string sex, ClothingContext place)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(sex);
        ArgumentNullException.ThrowIfNull(place);
        var stages = data.AgeStages.OrderBy(s => s.FromAgeDays).ToList();
        var stage = stages.LastOrDefault(s => s.FromAgeDays <= ageDays) ?? stages[0];
        var oldest = ReferenceEquals(stage, stages[^1]) && stages.Count > 1;

        int Between(int min, int max) => min + rng.NextInt(0, (max - min) + 1);
        double Range(double min, double max) => min + ((max - min) * rng.NextDouble());
        string Pick(ICollection<string> values) => values.ElementAt(rng.NextInt(0, values.Count));
        T Weighted<T>(IReadOnlyList<T> items, Func<T, int> weight)
        {
            // One draw whatever the list, so the following values never shift.
            var roll = rng.NextInt(0, items.Sum(weight));
            foreach (var item in items)
            {
                roll -= weight(item);
                if (roll < 0)
                {
                    return item;
                }
            }

            return items[^1];
        }

        var height = Between(data.HeightCm.Min, data.HeightCm.Max);
        var build = Between(data.BuildPermille.Min, data.BuildPermille.Max);
        var skin = Pick(data.SkinTones);
        var hairStyle = Weighted([.. data.HairStyles], h => h.Weight).Id;
        var hair = Pick(data.HairColors);
        var elderHair = data.ElderHairColors.Count > 0 ? Pick(data.ElderHairColors) : hair;
        // Only outfits whose conditions fit (sex included); the data keep one without conditions, so
        // an empty list is a data error, never a reason to ignore the conditions.
        var fitting = data.Outfits.Where(o => place.Fits(o.When, sex)).ToList();
        if (fitting.Count == 0)
        {
            throw new InvalidDataException($"appearance.json: no outfit fits sex '{sex}' at {place} (one outfit needs no conditions).");
        }

        var clothing = Weighted(fitting, o => o.Weight).Id;
        var top = Pick(data.ClothingColors);
        var bottom = Pick(data.ClothingColors);
        var shapes = new SortedDictionary<string, double>(StringComparer.Ordinal);
        if (data.Shapes.Count > 0 && rng.NextInt(0, 1000) < data.ShapeChancePermille)
        {
            var shape = data.Shapes.ElementAt(rng.NextInt(0, data.Shapes.Count));
            shapes[shape.Id] = Math.Round(Range(shape.Min, shape.Max), 3);
        }

        var head = Math.Round(Range(data.HeadScale.Min, data.HeadScale.Max), 3);

        // Sex proportions (F4): every entry draws, the inhabitant keeps those of its sex and age stage.
        foreach (var form in data.SexShapes)
        {
            var weight = Math.Round(Range(form.Min, form.Max), 3);
            if (SexId(form.Sex) == sex && form.AgeStages.Contains(stage.Id, StringComparer.Ordinal))
            {
                shapes[form.Id] = weight;
            }
        }

        // Every value is drawn in the same order whatever the stage, so a later stage change (B9) does
        // not shift the other values; the oldest stage wears the elder hair colour.
        return new Appearance(height, build, skin, hairStyle, oldest ? elderHair : hair, clothing, top, bottom, stage.Id, shapes, head);
    }

    /// <summary>
    /// The record with renamed hair style and outfit ids replaced by their current ids
    /// (<c>renamed_hair_styles</c>, <c>renamed_outfits</c> in the data), so worlds saved before a rename
    /// keep their figures (F4: curly, braided, tied and wrap of the first look).
    /// </summary>
    public Appearance WithCurrentIds(LookAppearance data)
    {
        ArgumentNullException.ThrowIfNull(data);
        static string Current(string id, ICollection<AppearanceRename>? renamed) =>
            renamed?.FirstOrDefault(r => r.OldId == id)?.NewId ?? id;
        var hair = Current(HairStyle, data.RenamedHairStyles);
        var outfit = Current(ClothingStyle, data.RenamedOutfits);
        return hair == HairStyle && outfit == ClothingStyle ? this : this with { HairStyle = hair, ClothingStyle = outfit };
    }

    /// <summary>The sex id of the data (the inhabitant's sex) for a sex proportion entry.</summary>
    public static string SexId(AppearanceSexShapeSex sex) => sex switch
    {
        AppearanceSexShapeSex.Female => "female",
        AppearanceSexShapeSex.Male => "male",
        _ => throw new ArgumentOutOfRangeException(nameof(sex), sex, "unknown sex"),
    };

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

/// <summary>
/// Where and when an inhabitant is dressed (F4): season, climate, biome at the place and the world's
/// tech level. Null values are unknown; an outfit that names a list for an unknown value does not fit.
/// </summary>
/// <param name="Season">Season id.</param>
/// <param name="Climate">Climate id of the world.</param>
/// <param name="Biome">Biome at the inhabitant's place.</param>
/// <param name="TechLevel">Tech level of the world.</param>
public sealed record ClothingContext(string? Season, string? Climate, string? Biome, string? TechLevel)
{
    /// <summary>Nothing known: only outfits without conditions fit.</summary>
    public static ClothingContext Unknown { get; } = new(null, null, null, null);

    /// <summary>True if every list of the condition contains the matching value (a missing list means any).</summary>
    public bool Fits(AppearanceOutfitCondition? when, string? sex = null) =>
        when is null || (Match(when.Seasons, Season) && Match(when.Climates, Climate) && Match(when.Biomes, Biome)
            && Match(when.TechLevels, TechLevel) && Match(when.Sexes, sex));

    private static bool Match(ICollection<string>? allowed, string? value) =>
        allowed is null || allowed.Count == 0 || (value is not null && allowed.Contains(value, StringComparer.Ordinal));
}
