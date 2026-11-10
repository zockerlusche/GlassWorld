using System.Text.Json.Nodes;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Simulation.Tests;

/// <summary>The core's mock picks exactly like the AI service's mock: all shared vectors of tests/mock/cases.json (Bogen 34 R26).</summary>
public sealed class MockRuleTests
{
    private static readonly string CasesFile = Path.Combine(TestData.Root, "tests", "mock", "cases.json");

    public static TheoryData<string> CaseIds()
    {
        var data = new TheoryData<string>();
        foreach (var node in Cases())
        {
            data.Add((string)node!["id"]!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void TheCoreGivesTheSharedExpectedOption(string id)
    {
        var node = Cases().Single(c => (string)c!["id"]! == id)!;
        var weights = new MockWeights { DefaultPermille = (int)node["weights"]!["default_permille"]! };
        foreach (var w in node["weights"]!["weights"]!.AsArray())
        {
            weights.Weights.Add(new MockWeight { Action = (string)w!["action"]!, Permille = (int)w["permille"]! });
        }

        var options = node["options"]!.AsArray().Select(o => ((string)o!["option_id"]!, (string)o["action"]!));
        Assert.Equal((string)node["expected"]!, MockDecisionPort.Pick(options, (long)node["seed"]!, weights));
    }

    [Fact]
    public void ThereAreSharedVectors() => Assert.True(Cases().Count >= 10);

    [Fact]
    public void TheShippedWeightsLoad()
    {
        var weights = ContractSerializer.Deserialize<MockWeights>(File.ReadAllText(Path.Combine(TestData.Root, "content", MockDecisionPort.WeightsFile)));
        Assert.True(weights.DefaultPermille >= 0);
        _ = MockDecisionPort.FromContent(Path.Combine(TestData.Root, "content"));
    }

    private static JsonArray Cases() => JsonNode.Parse(File.ReadAllText(CasesFile))!["cases"]!.AsArray();
}
