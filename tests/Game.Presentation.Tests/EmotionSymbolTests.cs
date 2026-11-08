using System.Text.Json.Nodes;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Presentation.Tests;

/// <summary>Feeling symbols above the head (B2541 part 4, B2543).</summary>
public sealed class EmotionSymbolTests
{
    private static readonly string DataPath = Path.Combine(TestPaths.Root, "content", "ui", "emotion_symbols.json");

    private static readonly EmotionSymbols Data = ContractSerializer.Deserialize<EmotionSymbols>(File.ReadAllText(DataPath));

    private static readonly string[] Moods = [.. JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "vocabulary", "moods.json")))!["entries"]!.AsArray().Select(e => (string)e!["id"]!)];

    [Fact]
    public void AnInventionLightsABulbThatRisesFadesAndRestsForItsCooldown()
    {
        var planner = New();
        var invented = new WorldEvent("invented", 10, "inh-000001", null, "rt-000001");
        var shown = planner.Update(0, [invented], [Calm("inh-000001")]);
        Assert.Equal(new SymbolView("inh-000001", "bulb", SymbolMotion.Rise, 0), Assert.Single(shown));
        Assert.Single(planner.Update(Data.Rise.DurationS / 2, [], [Calm("inh-000001")]));
        Assert.Empty(planner.Update(Data.Rise.DurationS + 0.01, [], [Calm("inh-000001")]));

        var cooldown = Data.Triggers.First(t => t.Event == "invented").CooldownS;
        Assert.Empty(planner.Update(Math.Max(cooldown, Data.Rise.DurationS) - 0.1, [invented], [Calm("inh-000001")]));
        Assert.Single(planner.Update(Math.Max(cooldown, Data.Rise.DurationS) + 0.1, [invented], [Calm("inh-000001")]));
    }

    [Fact]
    public void AFeelingThatRisesShowsOnceAndAgainOnlyAfterFallingBack()
    {
        var planner = New();
        var love = Data.Triggers.First(t => t.Mood == "love");
        var high = Subject("inh-000002", ("love", love.Threshold + 0.1));
        Assert.Equal("heart", Assert.Single(planner.Update(0, [], [high])).Symbol);
        Assert.Empty(planner.Update(Data.Rise.DurationS + 1, [], [high]));
        planner.Update(love.CooldownS + 5, [], [Calm("inh-000002")]);
        Assert.Equal("heart", Assert.Single(planner.Update(love.CooldownS + 6, [], [high])).Symbol);
    }

    [Fact]
    public void LastingAngerAndConditionsCircleWhileTheyLast()
    {
        var planner = New();
        var anger = Data.Triggers.First(t => t.Mood == "anger");
        var angry = Subject("inh-000003", ("anger", anger.Threshold + 0.1));
        var first = Assert.Single(planner.Update(0, [], [angry]));
        Assert.Equal(("storm_cloud", SymbolMotion.Orbit), (first.Symbol, first.Motion));
        Assert.Equal(0, Assert.Single(planner.Update(30, [], [angry])).StartedAt);
        Assert.Empty(planner.Update(31, [], [Calm("inh-000003")]));

        var hurt = new SymbolSubject("inh-000003", new Dictionary<string, double>(), ["cut"]);
        Assert.Equal("plaster", Assert.Single(planner.Update(32, [], [hurt])).Symbol);
        var sick = new SymbolSubject("inh-000003", new Dictionary<string, double>(), ["fever"]);
        Assert.Equal("sick_face", Assert.Single(planner.Update(33, [], [sick])).Symbol);
    }

    [Fact]
    public void NoMoreThanTheDataAllowsAboveOneHeadAndLastingOnesComeFirst()
    {
        var planner = New();
        var everything = new SymbolSubject(
            "inh-000004",
            Data.Triggers.Where(t => t.Mood is not null).ToDictionary(t => t.Mood!, _ => 1.0),
            ["cut", "cold"]);
        var shown = planner.Update(0, [new WorldEvent("invented", 1, "inh-000004", null, "rt-1")], [everything]);
        Assert.Equal(Data.MaxPerInhabitant, shown.Count);
        Assert.All(shown, s => Assert.Equal(SymbolMotion.Orbit, s.Motion));
    }

    [Fact]
    public void TheDataNamesKnownSymbolsAndMoodsAndBrokenDataIsRefused()
    {
        Assert.Contains(Data.Symbols, s => s.Id == "heart");
        var broken = JsonNode.Parse(File.ReadAllText(DataPath))!;
        broken["triggers"]![0]!["symbol"] = "rainbow";
        Assert.Throws<InvalidDataException>(() => new EmotionSymbolPlanner(ContractSerializer.Deserialize<EmotionSymbols>(broken.ToJsonString()), Moods));
        var twoSources = JsonNode.Parse(File.ReadAllText(DataPath))!;
        twoSources["triggers"]![0]!["mood"] = "joy";
        Assert.Throws<InvalidDataException>(() => new EmotionSymbolPlanner(ContractSerializer.Deserialize<EmotionSymbols>(twoSources.ToJsonString()), Moods));
        var unknownMood = JsonNode.Parse(File.ReadAllText(DataPath))!;
        unknownMood["triggers"]![2]!["mood"] = "glee";
        Assert.Throws<InvalidDataException>(() => new EmotionSymbolPlanner(ContractSerializer.Deserialize<EmotionSymbols>(unknownMood.ToJsonString()), Moods));
    }

    [Fact]
    public void EveryShapeIsDrawnWithOutlineAndFillAndTheSameEveryTime()
    {
        foreach (var symbol in Data.Symbols)
        {
            var pixels = GlassWorld.Presentation.Ui.SymbolIcons.Render(symbol.Shape, symbol.Fill, 64);
            Assert.Equal(pixels, GlassWorld.Presentation.Ui.SymbolIcons.Render(symbol.Shape, symbol.Fill, 64));
            var opaque = Enumerable.Range(0, 64 * 64).Count(i => pixels[(i * 4) + 3] > 200);
            Assert.InRange(opaque, 64 * 64 / 10, 64 * 64 * 8 / 10);

            // Both the fill colour and the dark outline appear.
            var fill = (Convert.ToByte(symbol.Fill[1..3], 16), Convert.ToByte(symbol.Fill[3..5], 16), Convert.ToByte(symbol.Fill[5..7], 16));
            Assert.Contains(Enumerable.Range(0, 64 * 64), i => pixels[(i * 4) + 3] == 255 && (pixels[i * 4], pixels[(i * 4) + 1], pixels[(i * 4) + 2]) == fill);
            Assert.Contains(Enumerable.Range(0, 64 * 64), i => pixels[(i * 4) + 3] == 255 && (pixels[i * 4], pixels[(i * 4) + 1], pixels[(i * 4) + 2]) == (0x2b, 0x2b, 0x3a));
        }
    }

    [Fact]
    public void FeelingSymbolsAreOnByDefault() => Assert.True(new GlassWorld.Presentation.Ui.PlayerSettings().FeelingSymbols);

    private static EmotionSymbolPlanner New() => new(Data, Moods);

    private static SymbolSubject Calm(string id) => new(id, new Dictionary<string, double>(), []);

    private static SymbolSubject Subject(string id, params (string Mood, double Value)[] moods) =>
        new(id, moods.ToDictionary(m => m.Mood, m => m.Value), []);
}
