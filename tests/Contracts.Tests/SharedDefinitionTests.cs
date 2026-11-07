using System.Text.Json.Nodes;
using GlassWorld.Tools.ContractGen;

namespace GlassWorld.Contracts.Tests;

/// <summary>
/// The generator emits a type for a <c>$defs</c> name only once (all generated types share one
/// namespace). Two schemas may therefore only use the same definition name for the same shape;
/// otherwise one of them would silently get the other's type.
/// </summary>
public sealed class SharedDefinitionTests
{
    [Fact]
    public void SameNamedDefinitionsAreIdenticalAcrossSchemas()
    {
        var schemas = Path.Combine(RepoRoot.Find(), "contracts", "schemas");
        var byName = new Dictionary<string, List<(string File, string Json)>>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(schemas, "*.schema.json").Order(StringComparer.Ordinal))
        {
            if (JsonNode.Parse(File.ReadAllText(file))!["$defs"] is not JsonObject defs)
            {
                continue;
            }

            foreach (var (name, definition) in defs)
            {
                if (!byName.TryGetValue(name, out var list))
                {
                    byName[name] = list = [];
                }

                list.Add((Path.GetFileName(file), definition!.ToJsonString()));
            }
        }

        var conflicts = byName
            .Where(e => e.Value.Select(v => v.Json).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(e => $"{e.Key}: {string.Join(", ", e.Value.Select(v => v.File))}")
            .ToList();
        Assert.True(conflicts.Count == 0, "same $defs name with different content (rename one or make them equal):\n" + string.Join("\n", conflicts));
    }
}
