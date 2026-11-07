using System.Text.Json.Nodes;
using GlassWorld.Tools.ContractGen;

namespace GlassWorld.Contracts.Tests;

public sealed class ContractInfoTests
{
    [Fact]
    public void VersionMatchesTheSchemaConstant()
    {
        var path = Path.Combine(RepoRoot.Find(), "contracts", "schemas", "common.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(ContractInfo.Version, schema["$defs"]!["ContractVersion"]!["const"]!.GetValue<string>());
    }
}
