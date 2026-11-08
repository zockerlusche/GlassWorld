using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;
using GlassWorld.Simulation.Weather;
using GlassWorld.Simulation.Writing;

namespace GlassWorld.Simulation.Tests;

/// <summary>Writing as a frame in the core (A1357 part A, B8 R13-R15, spec 33 §9a).</summary>
public sealed class WritingTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gw-writing-" + Guid.NewGuid().ToString("N"));

    public WritingTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void RainWipesMarksInTheSandButHardlyTouchesCarvedStone()
    {
        // Criterion 1 with a stand-in for GW1's weather core (exactly its interface, #19313).
        var (host, writing, sand, stone) = WorldWithTexts(new FixedWeather("rain"));
        var hour = host.World.Calendar.Settings.TicksPerHour;
        Step(host, writing, hour / 4);
        Assert.NotNull(writing.On(sand));
        Step(host, writing, 2 * hour);
        Assert.Null(writing.On(sand));
        Assert.NotNull(host.Objects.All.SingleOrDefault(o => o.Id == sand));
        Assert.True(writing.On(stone)!.Legibility > 0.8, "carved stone stays readable in the rain");
    }

    [Fact]
    public void InDryWeatherSandMarksOnlyFadeSlowly()
    {
        var (host, writing, sand, _) = WorldWithTexts(new FixedWeather("clear"));
        Step(host, writing, 3 * host.World.Calendar.Settings.TicksPerHour);
        var text = writing.On(sand)!;
        Assert.True(text.Legibility is > 0.4 and < 0.6, $"legibility {text.Legibility:0.000}");
        Assert.True(writing.Readable(text));
    }

    [Fact]
    public void WithoutWeatherTextsWearOnlyByThemselvesAndTheSameSeedGivesTheSameWorld()
    {
        var (first, a, _, _) = WorldWithTexts(null);
        var (second, b, _, _) = WorldWithTexts(null);
        Step(first, a, 500);
        Step(second, b, 500);
        Assert.Equal(a.Texts, b.Texts);
        Assert.Equal(first.World.StateHash(), second.World.StateHash());
    }

    [Fact]
    public void ATextGoesWithItsObjectAndALostObjectKeepsIt()
    {
        var (host, writing, sand, stone) = WorldWithTexts(null);
        host.Objects.Remove(sand);
        Step(host, writing, 1);
        Assert.Null(writing.On(sand));

        // Lost somewhere, found generations later: the text is still on the stone (B8 R15).
        Step(host, writing, 10 * host.World.Calendar.TicksPerDay);
        Assert.True(writing.Readable(writing.On(stone)!));
    }

    [Fact]
    public void ATextNeedsAnObjectAndAKnownCarrierAndIsCappedByTheCarrier()
    {
        var (host, writing, sand, _) = WorldWithTexts(null);
        Assert.Throws<InvalidOperationException>(() => writing.Place(new WrittenText("obj-999999", "sand_marks", "k", "inh-000001", 0, 0.5)));
        Assert.Throws<InvalidOperationException>(() => writing.Place(new WrittenText(sand, "papyrus", "k", "inh-000001", 0, 0.5)));
        writing.Place(new WrittenText(sand, "sand_marks", "k", "inh-000001", 0, 1.0));
        Assert.Equal(host.Content.Writing.Media.Single(m => m.Id == "sand_marks").Legibility, writing.On(sand)!.Legibility);
    }

    [Fact]
    public void TheStartHasNoWritingAndStaysOutOfTheHash()
    {
        // Criteria 4 and 5: nobody starts with writing, and worlds without texts keep their golden hash.
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 1, new MockDecisionPort(), "de");
        Assert.Empty(host.Writing.Texts);
        Assert.False(host.Writing.HasState);
        host.World.Run(200);
        Assert.Empty(host.Writing.Texts);

        var withText = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 1, new MockDecisionPort(), "de");
        var before = withText.World.StateHash();
        withText.Writing.Place(new WrittenText(withText.Objects.All.First().Id, "carving_stone", "k", "inh-000001", 0, 0.5));
        Assert.NotEqual(before, withText.World.StateHash());
    }

    [Fact]
    public void TextsSurviveSaveAndLoad()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 1, new MockDecisionPort(), "de");
        host.Writing.Place(new WrittenText(host.Objects.All.First().Id, "carving_wood", "belief:apple|edible|true", "inh-000001", 3, 0.7));
        host.World.Run(50);
        var path = Path.Combine(_directory, "writing.sqlite");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var settings = Contracts.Validation.ContractSerializer.Deserialize<Contracts.DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        var loaded = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new ReplayDecisionPort(host.Decisions.Log), "de"), SaveGameStore.Load(path));
        Assert.Equal(host.Writing.Texts, loaded.Writing.Texts);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
    }

    private static (SimulationHost Host, WritingSystem Writing, string Sand, string Stone) WorldWithTexts(IWeather? weather)
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 1, new MockDecisionPort(), "de");
        var type = host.Content.Objects.TypeIds.First();
        var sand = host.Objects.Spawn(type, new Position(1, 1)).Id;
        var stone = host.Objects.Spawn(type, new Position(2, 1)).Id;
        var writing = new WritingSystem(host.Content.Writing, host.Objects, weather, host.World.Calendar.Settings.TicksPerHour, host.World.Calendar.TicksPerDay);
        writing.Place(new WrittenText(sand, "sand_marks", "belief:water|drinkable|true", "inh-000001", 0, 0.6));
        writing.Place(new WrittenText(stone, "carving_stone", "belief:water|drinkable|true", "inh-000001", 0, 0.85));
        return (host, writing, sand, stone);
    }

    private static void Step(SimulationHost host, WritingSystem writing, long ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            host.World.Step();
            writing.Advance(host.World);
        }
    }

    /// <summary>Stand-in for GW1's weather core: the same weather everywhere, all the time.</summary>
    private sealed class FixedWeather(string weather) : IWeather
    {
        public string Current => weather;

        public string WeatherAt(Position at, long tick) => weather;
    }
}
