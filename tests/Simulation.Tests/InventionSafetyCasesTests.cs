using System.Text.Json;
using System.Text.Json.Nodes;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Inventions;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B4b R16 (A1372): the youth-protection cases of the invention test set (GW2, #277,
/// <c>ai-service/testsets/invention/cases.v3.json</c>, <c>guard_cases</c>) run through the core's final check. The
/// core has the last word: every forbidden proposal - hunting, trapping, poisoning, a weapon - is refused, whatever
/// the AI guard said; the allowed border cases (a hand axe for wood, a knife for plants, a torch) are not refused
/// for youth protection. The same list as the AI guard's measurement, so both sides are judged on the same cases.
/// </summary>
public sealed class InventionSafetyCasesTests
{
    private static readonly string Root = TestData.Root;
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(Path.Combine(Root, "content")));

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Load())
        {
            data.Add(c.GetProperty("id").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheCoreRefusesEveryForbiddenProposalAndNoAllowedOneForYouthProtection(string id)
    {
        var c = Load().Single(x => x.GetProperty("id").GetString() == id);
        var forbidden = c.GetProperty("forbidden").GetBoolean();
        var rules = Content.Value.Inventions;
        var result = Approved(c.GetProperty("invention").GetRawText());
        var perceived = c.GetProperty("materials").EnumerateArray().Select(m => m[0].GetString()!).ToHashSet(StringComparer.Ordinal);

        var reasons = InventionCheck.Check(result, perceived, rules);
        if (forbidden)
        {
            Assert.True(reasons.Contains(InventionCheck.ForbiddenPurpose), $"{id}: the core lets a forbidden proposal through ({string.Join(',', reasons)})");
        }
        else
        {
            Assert.DoesNotContain(InventionCheck.ForbiddenPurpose, reasons);
            Assert.DoesNotContain(InventionCheck.NameNotAllowed, reasons);
            Assert.DoesNotContain(InventionCheck.LivingProperty, reasons);
        }
    }

    [Theory]
    [InlineData("Tierfalle", "*falle", true)]
    [InlineData("Fallenbauer", "*falle", false)]
    [InlineData("Jagdspeer", "jagd*", true)]
    [InlineData("Wasserjagd", "jagd*", false)]
    [InlineData("Ein Giftpfeil", "*pfeil", true)]
    [InlineData("Pfeiler aus Stein", "*pfeil", false)]
    [InlineData("Fackel", "*keule", false)]
    [InlineData("Steinmesser für Pflanzen", "lanze*", false)]
    [InlineData("Gefallen", "*falle", false)]
    [InlineData("club", "club", true)]
    [InlineData("Honigschleuder", "schleuder", false)]
    [InlineData("Tragschlinge", "wurfschlinge*", false)]
    [InlineData("Regenbogen", "bogen", false)]
    [InlineData("Ein Bogen", "bogen", true)]
    [InlineData("spearmint tea", "spear", false)]
    [InlineData("trapdoor", "trap", false)]
    [InlineData("clubhouse", "club", false)]
    public void ATermMatchesWordsAsItsStarsSay(string name, string term, bool hit) =>
        Assert.Equal(hit, InventionCheck.Holds(name, [term]));

    [Fact]
    public void ANeedOrAbilityWithAForbiddenNameIsRefusedToo()
    {
        var c = Load().First(x => !x.GetProperty("forbidden").GetBoolean());
        var result = Approved(c.GetProperty("invention").GetRawText());
        var perceived = c.GetProperty("materials").EnumerateArray().Select(m => m[0].GetString()!).ToHashSet(StringComparer.Ordinal);
        var action = Content.Value.Inventions.Actions.Order(StringComparer.Ordinal).First();
        result.Invention!.IntroducesAbilities = [new NewAbility { Id = "test_ability", NameDe = "Jagen", NameEn = "hunting", StartLevel = 0.1, Actions = [action] }];
        Assert.Contains(InventionCheck.ForbiddenPurpose, InventionCheck.Check(result, perceived, Content.Value.Inventions));
    }

    [Fact]
    public void TheListHasForbiddenAndAllowedCases()
    {
        var cases = Load();
        Assert.Contains(cases, c => c.GetProperty("forbidden").GetBoolean());
        Assert.Contains(cases, c => !c.GetProperty("forbidden").GetBoolean());
    }

    private static List<JsonElement> Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "ai-service", "testsets", "invention", "cases.v3.json")));
        return [.. document.RootElement.GetProperty("guard_cases").EnumerateArray().Select(e => e.Clone())];
    }

    /// <summary>The proposal as the AI guard approved it in every round (the core must not rely on that).</summary>
    private static InventionResult Approved(string inventionJson)
    {
        // The case's invention put into a valid job status as it is, so the contract checks it like an answer of the
        // service (v3, GW2 #289: every guard proposal is a valid invention; the test set's own test checks that too).
        var example = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "contracts", "examples", "valid", "job-status.invention-approved.json")))!;
        example["result"]!["invention"]!["invention"] = JsonNode.Parse(inventionJson);
        var result = ContractSerializer.Deserialize<JobStatus>(example.ToJsonString()).Result!.Invention!;
        result.Decision = InventionResultDecision.Approve;
        foreach (var round in result.Rounds)
        {
            round.Decision = GuardVerdictDecision.Approve;
        }

        return result;
    }
}
