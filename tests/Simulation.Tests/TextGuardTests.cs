using System.Text.Json;
using GlassWorld.Simulation.Safety;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// Core content check against the shared edge cases (tests/safety/cases.json): the same set the AI
/// service passes, so both sides decide identically (B6 R13, D334 mandatory edge-case tests).
/// </summary>
public sealed class TextGuardTests
{
    private static readonly string Root = FindRoot();
    private static readonly TextGuard Guard = TextGuard.FromJson(File.ReadAllText(Path.Combine(Root, "content", "safety", "rules.json")));

    public static TheoryData<string, string, string, string?> PrefilterCases => Cases("prefilter");

    public static TheoryData<string, string, string, string?> ModelCases => Cases("model");

    [Theory]
    [MemberData(nameof(PrefilterCases))]
    public void PrefilterDecidesLikeTheEdgeCaseSet(string id, string text, string expect, string? category)
    {
        var verdict = Guard.Check(text);
        if (expect == "allow")
        {
            Assert.True(verdict.Allowed, $"{id} '{text}' blocked: {verdict}");
        }
        else
        {
            Assert.False(verdict.Allowed, $"{id} '{text}' allowed");
            Assert.Equal(category, verdict.Category);
        }
    }

    [Theory]
    [MemberData(nameof(ModelCases))]
    public void ModelStageCasesPassThePrefilter(string id, string text, string expect, string? category)
    {
        _ = (expect, category);
        Assert.True(Guard.Check(text).Allowed, $"{id}: only the AI rule guard may decide this case");
    }

    [Fact]
    public void TooLongTextIsBlocked()
    {
        var verdict = Guard.Check(new string('a', 281));
        Assert.False(verdict.Allowed);
        Assert.Equal("too_long", verdict.Reason);
    }

    [Theory]
    [InlineData("se\u200Bx")]
    [InlineData("s\u0000e\u0000x")]
    [InlineData("\uFF53\uFF45\uFF58")]
    [InlineData("to\u0308ten")]
    public void HiddenOrWideCharactersDoNotHideWords(string text) => Assert.False(Guard.Check(text).Allowed);

    private static TheoryData<string, string, string, string?> Cases(string stage)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "tests", "safety", "cases.json")));
        var data = new TheoryData<string, string, string, string?>();
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray().Where(c => c.GetProperty("stage").GetString() == stage))
        {
            var category = c.GetProperty("category");
            data.Add(c.GetProperty("id").GetString()!, c.GetProperty("text").GetString()!, c.GetProperty("expect").GetString()!, category.ValueKind == JsonValueKind.Null ? null : category.GetString());
        }

        return data;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GlassWorld.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("GlassWorld.slnx not found");
    }
}
