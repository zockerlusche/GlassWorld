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
        // The builder's skill at the moment the thing is finished (review GW3 #310: the last step's practice comes after).
        double? level = null;
        host.Inhabitants.Reported += e =>
        {
            if (e.Kind == GlassWorld.Simulation.Inventions.InventionSystem.Built)
            {
                level ??= host.SkillLevels.Level(e.InhabitantId, quality.Skill);
            }
        };
        AnswerPlan(port, Plan());
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, 3000);
        Assert.NotNull(level);

        // The highest tier whose from_level that skill reaches.
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
