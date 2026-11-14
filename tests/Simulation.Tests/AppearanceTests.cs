using System.Text.Json.Nodes;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Random;

namespace GlassWorld.Simulation.Tests;

/// <summary>F4 in the core: hair kit by weight, outfits by place and time, sex proportions by sex and age stage.</summary>
public sealed class AppearanceTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly LookAppearance Data = ContentSet.Load(ContentDirectory).Appearance;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public AppearanceTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("winter")]
    [InlineData("summer")]
    public void OutfitsFitTheSeasonAndTheTechLevel(string season)
    {
        var place = new ClothingContext(season, "temperate", "beach_sea", "shaping");
        var drawn = Many(place).Select(a => a.ClothingStyle).ToHashSet(StringComparer.Ordinal);
        var fitting = Data.Outfits.Where(o => place.Fits(o.When, "female")).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(fitting, drawn);
        Assert.True(drawn.SetEquals(fitting), $"{season}: drawn {string.Join(',', drawn)} vs fitting {string.Join(',', fitting)}");

        // A warm-only outfit never shows up in winter, and a later tech level drops the start clothing.
        var warmOnly = Data.Outfits.Where(o => o.When?.Seasons is { } s && !s.Contains("winter")).Select(o => o.Id);
        if (season == "winter")
        {
            Assert.Empty(drawn.Intersect(warmOnly));
        }

        var later = Many(place with { TechLevel = "smelting" }).Select(a => a.ClothingStyle).ToHashSet(StringComparer.Ordinal);
        Assert.All(later, id => Assert.Null(Data.Outfits.First(o => o.Id == id).When));
    }

    [Fact]
    public void AnOutfitLimitedToOneSexIsOnlyWornByIt()
    {
        var place = new ClothingContext("summer", "temperate", null, "shaping");
        var limited = Data.Outfits.Where(o => o.When?.Sexes is { Count: > 0 }).ToList();
        Assert.NotEmpty(limited);
        foreach (var sex in new[] { "female", "male" })
        {
            var drawn = Many(place, 400, sex).Select(a => a.ClothingStyle).ToHashSet(StringComparer.Ordinal);
            Assert.All(limited.Where(o => !o.When!.Sexes!.Contains(sex)), o => Assert.DoesNotContain(o.Id, drawn));
        }
    }

    [Fact]
    public void WithoutKnownPlaceOnlyTheOutfitsWithoutConditionsAreWorn()
    {
        var drawn = Many(ClothingContext.Unknown).Select(a => a.ClothingStyle).Distinct();
        Assert.All(drawn, id => Assert.Null(Data.Outfits.First(o => o.Id == id).When));
    }

    [Fact]
    public void EveryHairStyleOfTheKitTurnsUpByItsWeight()
    {
        var counts = Many(ClothingContext.Unknown, 3000).GroupBy(a => a.HairStyle).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        Assert.Equal(Data.HairStyles.Select(h => h.Id).Order(StringComparer.Ordinal), counts.Keys.Order(StringComparer.Ordinal));
        var total = Data.HairStyles.Sum(h => h.Weight);
        foreach (var style in Data.HairStyles)
        {
            var expected = 3000.0 * style.Weight / total;
            Assert.InRange(counts[style.Id], expected * 0.7, expected * 1.3);
        }
    }

    [Fact]
    public void SexProportionsBelongToTheirSexAndAgeStage()
    {
        var adultAge = Data.StartAgeDays;
        var childAge = Data.AgeStages.First(s => s.Id == "child").FromAgeDays + 1;
        var women = Many(ClothingContext.Unknown, 50, "female", adultAge);
        var men = Many(ClothingContext.Unknown, 50, "male", adultAge);
        var children = Many(ClothingContext.Unknown, 50, "female", childAge);
        string[] Ids(string sex) => [.. Data.SexShapes.Where(s => Appearance.SexId(s.Sex) == sex).Select(s => s.Id)];
        Assert.All(women, a => Assert.Subset(a.Shapes.Keys.ToHashSet(), Ids("female").ToHashSet()));
        Assert.All(women, a => Assert.DoesNotContain(a.Shapes.Keys, k => Ids("male").Contains(k)));
        Assert.All(men, a => Assert.DoesNotContain(a.Shapes.Keys, k => Ids("female").Contains(k)));
        Assert.All(children, a => Assert.DoesNotContain(a.Shapes.Keys, k => Ids("female").Contains(k) || Ids("male").Contains(k)));
        foreach (var form in Data.SexShapes.Where(s => s.AgeStages.Contains("adult")))
        {
            var owners = form.Sex == AppearanceSexShapeSex.Female ? women : men;
            Assert.All(owners, a => Assert.InRange(a.Shapes[form.Id], form.Min, form.Max));
        }
    }

    [Fact]
    public void YouthHasSofterProportionsThanAdults()
    {
        // Review GW3 #108 (12+, B2466): the teenage stage has its own, lower ranges (placeholders).
        var youthAge = Data.AgeStages.First(s => s.Id == "youth").FromAgeDays + 1;
        var youth = Many(ClothingContext.Unknown, 200, "female", youthAge);
        foreach (var id in new[] { "bust", "waist", "hips" })
        {
            var young = Assert.Single(Data.SexShapes, s => s.Id == id && s.AgeStages.Contains("youth"));
            var adult = Assert.Single(Data.SexShapes, s => s.Id == id && s.AgeStages.Contains("adult"));
            Assert.True(young.Max <= adult.Max / 2 + 1e-9 || (id == "bust" && young.Max <= 0.3), $"{id}: youth max {young.Max}");
            Assert.All(youth, a => Assert.InRange(a.Shapes[id], young.Min, young.Max));
        }
    }

    [Fact]
    public void WhenNoOutfitFitsTheDrawFailsInsteadOfIgnoringTheConditions()
    {
        // Review GW3 #108: never fall back to outfits whose conditions (sex included) do not fit.
        var womenOnly = GlassWorld.Contracts.Validation.ContractSerializer.Deserialize<LookAppearance>(
            File.ReadAllText(Path.Combine(ContentDirectory, "look", "appearance.json")));
        womenOnly.Outfits = [.. womenOnly.Outfits.Where(o => o.When?.Sexes is { Count: > 0 })];
        var place = new ClothingContext("summer", "temperate", null, "shaping");
        Assert.NotNull(Appearance.Draw(womenOnly, Stream(1), Data.StartAgeDays, "female", place));
        Assert.Throws<InvalidDataException>(() => Appearance.Draw(womenOnly, Stream(1), Data.StartAgeDays, "male", place));
    }

    [Fact]
    public void RenamedIdsOfTheFirstLookGetTheirCurrentIds()
    {
        var record = Appearance.Draw(Data, Stream(5), Data.StartAgeDays, "male", ClothingContext.Unknown);
        Assert.Same(record, record.WithCurrentIds(Data));
        var old = record with { HairStyle = "curly", ClothingStyle = "wrap" };
        Assert.Equal(record with { HairStyle = "curls", ClothingStyle = "tunic" }, old.WithCurrentIds(Data));
        Assert.Equal("braid", (record with { HairStyle = "braided" }).WithCurrentIds(Data).HairStyle);
        Assert.Equal("bun", (record with { HairStyle = "tied" }).WithCurrentIds(Data).HairStyle);
    }

    [Fact]
    public void TheSameStreamGivesTheSameLookAndTheSexOnlyChangesProportionsAndOutfit()
    {
        var place = new ClothingContext("spring", "temperate", null, "shaping");
        var woman = Appearance.Draw(Data, Stream(3), Data.StartAgeDays, "female", place);
        var again = Appearance.Draw(Data, Stream(3), Data.StartAgeDays, "female", place);
        var man = Appearance.Draw(Data, Stream(3), Data.StartAgeDays, "male", place);
        Assert.Equal(woman with { Shapes = again.Shapes }, again);
        Assert.Equal(woman.Shapes, again.Shapes);

        // Every value is drawn in the same order for both sexes, so only the proportions (and an
        // outfit limited to one sex) differ.
        Assert.Equal(woman with { Shapes = man.Shapes, ClothingStyle = man.ClothingStyle }, man);
        Assert.NotEqual(woman.Shapes.Keys, man.Shapes.Keys);
    }

    [Fact]
    public void StartInhabitantsAreDressedForTheirIslandAndTime()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        var season = host.World.Now.SeasonId;
        foreach (var inhabitant in host.Inhabitants.All)
        {
            var outfit = Data.Outfits.First(o => o.Id == inhabitant.Appearance!.ClothingStyle);
            Assert.True(outfit.When?.Seasons is null || outfit.When.Seasons.Contains(season), $"{outfit.Id} in {season}");
        }
    }

    [Theory]
    [InlineData("unconditional", "no outfit without conditions")]
    [InlineData("season", "unknown season 'monsoon'")]
    [InlineData("stage", "unknown age stage 'teen'")]
    [InlineData("clash", "is also a body shape")]
    [InlineData("rename", "renamed outfit 'wrap' points to unknown 'cape'")]
    public void BrokenAppearanceDataIsRefusedOnLoad(string broken, string message)
    {
        var copy = CopyContent();
        var file = Path.Combine(copy, "look", "appearance.json");
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var outfits = json["outfits"]!.AsArray();
        switch (broken)
        {
            case "unconditional":
                foreach (var outfit in outfits)
                {
                    outfit!["when"] ??= new JsonObject { ["tech_levels"] = new JsonArray("shaping") };
                }

                break;
            case "season":
                outfits[1]!["when"]!["seasons"] = new JsonArray("monsoon");
                break;
            case "stage":
                json["sex_shapes"]![0]!["age_stages"] = new JsonArray("teen");
                break;
            case "rename":
                json["renamed_outfits"]![0]!["new_id"] = "cape";
                break;
            default:
                json["sex_shapes"]![0]!["id"] = "heavy";
                break;
        }

        File.WriteAllText(file, json.ToJsonString());
        var error = Assert.Throws<InvalidDataException>(() => ContentSet.Load(copy));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    private static Pcg32 Stream(int i) => new RandomStreams(99).Get($"inhabitant/{i}/appearance");

    private static List<Appearance> Many(ClothingContext place, int count = 400, string sex = "female", int? ageDays = null) =>
        [.. Enumerable.Range(0, count).Select(i => Appearance.Draw(Data, Stream(i), ageDays ?? Data.StartAgeDays, sex, place))];

    private string CopyContent()
    {
        var target = Path.Combine(_directory, "content");
        foreach (var file in Directory.GetFiles(ContentDirectory, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(ContentDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        return target;
    }
}
