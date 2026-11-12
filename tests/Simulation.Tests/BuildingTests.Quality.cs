using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;

namespace GlassWorld.Simulation.Tests;

/// <summary>A1376 (D827 R19): every built thing carries a quality tier from the builder's skill when it is finished.</summary>
public sealed partial class BuildingTests
{
    [Fact]
    public void ABuiltThingCarriesTheQualityTierItsBuildersSkillReached()
    {
        var quality = ContractSerializer.Deserialize<BuildSettings>(File.ReadAllText(Path.Combine(EagerContent.Value, "build", "settings.json"))).Quality!;
        var (host, port, typeId) = Invented(36);
        Assert.Null(host.Inventions.QualityOfBuilt(typeId));
        AnswerPlan(port, Plan());
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, 3000);

        // The highest tier whose from_level the builder's skill reaches, at the moment it was finished.
        var level = host.SkillLevels.Level(host.Inhabitants.All.Single().Id, quality.Skill);
        var expected = quality.Tiers.Where(t => level >= t.FromLevel).OrderBy(t => t.FromLevel).Last().Id;
        Assert.Equal(expected, host.Inventions.QualityOfBuilt(typeId));
        Assert.Equal(expected, host.Inventions.Builds.Single().Quality);

        // Every tier has its words (DE/EN).
        foreach (var locale in new[] { "de", "en" })
        {
            var texts = File.ReadAllText(Path.Combine(TestData.Root, "content", "i18n", locale + ".json"));
            Assert.All(quality.Tiers, t => Assert.Contains($"\"ui.build.quality.{t.Id}\"", texts, StringComparison.Ordinal));
        }
    }
}
