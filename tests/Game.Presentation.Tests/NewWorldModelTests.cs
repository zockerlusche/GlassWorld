using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Safety;

namespace GlassWorld.Presentation.Tests;

/// <summary>New world form (B6 R3): name limits from the data and the content guard, seed field.</summary>
public sealed class NewWorldModelTests
{
    private static readonly Contracts.WorldOptions Options =
        ContractSerializer.Deserialize<Contracts.WorldOptions>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "player", "world_options.json")));

    private static readonly TextGuard Guard = TextGuard.FromJson(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "safety", "rules.json")));

    [Theory]
    [InlineData("Sonneninsel", null)]
    [InlineData("  Nebelbucht  ", null)]
    [InlineData("", "ui.new_world.problem.name_empty")]
    [InlineData("   ", "ui.new_world.problem.name_empty")]
    [InlineData("a\u0007b", "ui.new_world.problem.name_characters")]
    public void NameIsChecked(string name, string? expected) => Assert.Equal(expected, NewWorldModel.NameProblem(name, Options, Guard));

    [Fact]
    public void NameLongerThanTheDataLimitIsRefused()
        => Assert.Equal("ui.new_world.problem.name_long", NewWorldModel.NameProblem(new string('a', Options.NameMaxLength + 1), Options, Guard));

    [Fact]
    public void NameGoesThroughTheContentGuard()
    {
        // Every blocked pre-filter case of the shared guard test set that fits the length limit.
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "tests", "safety", "cases.json")));
        var blocked = doc.RootElement.GetProperty("cases").EnumerateArray()
            .Where(c => c.GetProperty("expect").GetString() == "block" && c.GetProperty("stage").GetString() == "prefilter")
            .Select(c => c.GetProperty("text").GetString()!)
            .Where(t => t.Trim().Length <= Options.NameMaxLength)
            .ToList();
        Assert.NotEmpty(blocked);
        Assert.All(blocked, t => Assert.Equal("ui.new_world.problem.name_blocked", NewWorldModel.NameProblem(t, Options, Guard)));
    }

    [Theory]
    [InlineData("", true, null)]
    [InlineData(" 42 ", true, 42UL)]
    [InlineData("-1", false, null)]
    [InlineData("12a", false, null)]
    [InlineData("99999999999999999999999", false, null)]
    public void SeedFieldIsParsed(string text, bool ok, ulong? seed) => Assert.Equal((ok, seed), NewWorldModel.ParseSeed(text));

    [Fact]
    public void DrawnSeedsDifferForNearbyTimes()
    {
        var a = NewWorldModel.DrawSeed(DateTimeOffset.UnixEpoch.AddSeconds(1));
        var b = NewWorldModel.DrawSeed(DateTimeOffset.UnixEpoch.AddSeconds(2));
        Assert.NotEqual(a, b);
        Assert.InRange(a, 0UL, 999_999_999UL);
    }
}
