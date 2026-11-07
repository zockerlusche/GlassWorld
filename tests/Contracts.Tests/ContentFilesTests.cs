using System.Text.Json.Nodes;
using GlassWorld.Contracts.Validation;
using GlassWorld.Tools.ContractGen;

namespace GlassWorld.Contracts.Tests;

/// <summary>
/// Every data file under content/ is assigned to exactly one schema by contracts/content-map.json
/// (shared with the Python tests) and is valid against it (ADR-0007).
/// </summary>
public sealed class ContentFilesTests
{
    private static readonly string Root = RepoRoot.Find();
    private static readonly string Content = Path.Combine(Root, "content");

    private static readonly Lazy<IReadOnlyList<(string Pattern, string Schema)>> Map = new(() =>
    {
        var map = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "contracts", "content-map.json")))!;
        return [.. map["entries"]!.AsArray().Select(e => ((string)e!["pattern"]!, (string)e["schema"]!))];
    });

    public static TheoryData<string> ContentFiles =>
        [.. Directory.GetFiles(Content, "*.json", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Content, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(ContentFiles))]
    public void ContentFileMatchesExactlyOneSchemaAndIsValid(string relativePath)
    {
        var matches = Map.Value.Where(e => Matches(e.Pattern, relativePath)).ToList();
        Assert.True(matches.Count == 1, $"content/{relativePath} matches {matches.Count} entries in contracts/content-map.json (expected 1)");

        var errors = ContractValidator.Validate(matches[0].Schema, File.ReadAllText(Path.Combine(Content, relativePath)));
        Assert.True(errors.Count == 0, $"content/{relativePath}: {string.Join("; ", errors)}");
    }

    [Fact]
    public void EveryMapEntryNamesAKnownSchemaAndMatchesAFile()
    {
        var files = ContentFiles.Select(row => (string)row.Data).ToList();
        foreach (var (pattern, schema) in Map.Value)
        {
            Assert.Contains(schema, ContractValidator.SchemaNames);
            Assert.Contains(files, f => Matches(pattern, f));
        }
    }

    [Theory]
    [InlineData("vocabulary/*.json", "vocabulary/actions.json", true)]
    [InlineData("vocabulary/*.json", "vocabulary/sub/actions.json", false)]
    [InlineData("vocabulary/*.json", "other/actions.json", false)]
    [InlineData("hardware/tiers.json", "hardware/tiers.json", true)]
    [InlineData("hardware/tiers.json", "hardware/tiers2.json", false)]
    public void PatternMatching(string pattern, string path, bool expected) =>
        Assert.Equal(expected, Matches(pattern, path));

    /// <summary>'*' matches any characters except '/'; everything else literally.</summary>
    private static bool Matches(string pattern, string path)
    {
        var p = pattern.Split('/');
        var s = path.Split('/');
        return p.Length == s.Length && p.Zip(s).All(x => SegmentMatches(x.First, x.Second));
    }

    private static bool SegmentMatches(string pattern, string segment)
    {
        var star = pattern.IndexOf('*', StringComparison.Ordinal);
        if (star < 0)
        {
            return string.Equals(pattern, segment, StringComparison.Ordinal);
        }

        var prefix = pattern[..star];
        var suffix = pattern[(star + 1)..];
        return segment.Length >= prefix.Length + suffix.Length
            && segment.StartsWith(prefix, StringComparison.Ordinal)
            && segment.EndsWith(suffix, StringComparison.Ordinal);
    }
}
