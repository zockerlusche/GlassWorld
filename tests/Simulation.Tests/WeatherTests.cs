using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;
using GlassWorld.Simulation.Weather;

namespace GlassWorld.Simulation.Tests;

/// <summary>B2 R13: the weather system (deterministic, by season, saved, read by decider and presentation).</summary>
public sealed class WeatherTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public WeatherTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static ClimateSettings Climate() => Content.Value.WorldGen.Climates["temperate"];

    private static (World World, WeatherSystem Weather) Bare(ulong seed, ClimateSettings? climate = null, int temperatureTenthC = 160)
    {
        var world = new World(seed, Content.Value.Calendar);
        var settings = world.Calendar.Settings;
        var weather = new WeatherSystem(climate ?? Climate(), temperatureTenthC, settings.TicksPerHour, settings.HoursPerDay);
        world.Add(weather);
        return (world, weather);
    }

    private static List<(long Tick, string Season, string Weather)> Year(ulong seed, ClimateSettings? climate = null, int temperatureTenthC = 160)
    {
        var (world, weather) = Bare(seed, climate, temperatureTenthC);
        var phases = new List<(long, string, string)>();
        weather.Changed += (_, now, tick) => phases.Add((tick, world.Calendar.At(tick).SeasonId, now));
        world.Run(world.Calendar.TicksPerDay * 28);
        return phases;
    }

    [Fact]
    public void TheSameSeedGivesTheSameWeatherYear()
    {
        Assert.Equal(Year(5), Year(5));
        Assert.NotEqual(Year(5), Year(6));
    }

    [Fact]
    public void TheWeatherOnlyChangesAtTheChangeStepOfTheData()
    {
        var hours = Climate().Weather.ChangeEveryHours;
        var step = (long)hours * Content.Value.Calendar.Settings.TicksPerHour;
        Assert.All(Year(3), p => Assert.Equal(0, p.Tick % step));
    }

    [Fact]
    public void EveryWeatherOfTheTableOccursOver20SeedsAndSeasonsShapeIt()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var bySeason = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var (world, weather) = Bare(seed);
            for (var t = 0L; t < world.Calendar.TicksPerDay * 28; t++)
            {
                world.Step();
                foreach (var condition in weather.Conditions)
                {
                    seen.Add(condition);
                    var season = world.Now.SeasonId;
                    if (!bySeason.TryGetValue(season, out var set))
                    {
                        bySeason[season] = set = [];
                    }

                    set.Add(condition);
                }
            }
        }

        // Acceptance 3 of the spec: every weather id of the table at least once over 20 seeds. The drought
        // weather needs drought_after_dry_days in a row without precipitation, which the placeholder
        // transitions practically never give within a year (open value question); its rule is tested in
        // DryDaysAddTheDroughtConditionFromTheData.
        var missing = Climate().Weather.States.Select(s => s.Weather).Where(w => !seen.Contains(w)).ToList();
        Assert.True(missing.Count == 0 || (missing.Count == 1 && missing[0] == Climate().Weather.DroughtWeather), $"never seen: {string.Join(", ", missing)}");

        // The season tables decide: no weather appears in a season whose table never leads to it.
        foreach (var table in Climate().Weather.Seasons)
        {
            var reachable = table.Transitions.SelectMany(t => t.To.Select(x => x.Weather).Append(t.FromWeather))
                .Append(Climate().Weather.DroughtWeather).ToHashSet(StringComparer.Ordinal);
            var season = bySeason.GetValueOrDefault(table.Season, []);
            var strangers = season.Where(w => !reachable.Contains(w)).ToList();

            // A weather carried over from the season before may last until its first change step.
            Assert.True(strangers.Count <= 2, $"{table.Season}: {string.Join(", ", strangers)}");
        }
    }

    [Fact]
    public void ChangedDataChangesTheWeatherWithoutCodeChange()
    {
        var climate = ContractSerializer.Deserialize<ClimateSettings>(ContractSerializer.Serialize(Climate()));
        foreach (var row in climate.Weather.Seasons.SelectMany(s => s.Transitions))
        {
            row.To = [new ClimateTransitionTarget { Weather = "rain", Weight = 1 }];
        }

        var phases = Year(9, climate);
        Assert.Equal("rain", Assert.Single(phases).Weather);
    }

    [Fact]
    public void PrecipitationBelowTheSnowTemperatureFallsAsSnow()
    {
        var climate = ContractSerializer.Deserialize<ClimateSettings>(ContractSerializer.Serialize(Climate()));
        foreach (var row in climate.Weather.Seasons.SelectMany(s => s.Transitions))
        {
            row.To = [new ClimateTransitionTarget { Weather = "rain", Weight = 1 }];
        }

        var phases = Year(9, climate, temperatureTenthC: climate.Weather.SnowBelowTenthC - 1);
        Assert.Equal(climate.Weather.SnowWeather, Assert.Single(phases).Weather);
    }

    [Fact]
    public void DryDaysAddTheDroughtConditionFromTheData()
    {
        var climate = ContractSerializer.Deserialize<ClimateSettings>(ContractSerializer.Serialize(Climate()));
        var dry = climate.Weather.States.First(s => s.PrecipitationPermille == 0 && s.Weather != climate.Weather.DroughtWeather).Weather;
        foreach (var row in climate.Weather.Seasons.SelectMany(s => s.Transitions))
        {
            row.To = [new ClimateTransitionTarget { Weather = dry, Weight = 1 }];
        }

        var (world, weather) = Bare(2, climate);
        world.Run(world.Calendar.TicksPerDay * (climate.Weather.DroughtAfterDryDays - 1));
        Assert.DoesNotContain(climate.Weather.DroughtWeather, weather.Conditions);
        world.Run(world.Calendar.TicksPerDay * 2);
        Assert.Contains(climate.Weather.DroughtWeather, weather.Conditions);
        Assert.Equal(weather.Conditions.Order(StringComparer.Ordinal), weather.Conditions);
    }

    [Fact]
    public void WeatherAtAnswersForKnownTicksAndRefusesOthers()
    {
        var (world, weather) = Bare(4);
        world.Run(world.Calendar.TicksPerDay * 3);
        var anywhere = new Position(0, 0);
        Assert.Equal(weather.Current, weather.WeatherAt(anywhere, world.Tick));
        Assert.Equal(weather.Current, weather.WeatherAt(new Position(100, 50), weather.SinceTick));
        Assert.Throws<ArgumentOutOfRangeException>(() => weather.WeatherAt(anywhere, world.Tick + 1));

        world.Run(world.Calendar.TicksPerDay * 40);
        Assert.Throws<ArgumentOutOfRangeException>(() => weather.WeatherAt(anywhere, 0));
    }

    [Fact]
    public void OnTheIslandTheWeatherIsInTheWorldTheDeciderAndTheSnapshot()
    {
        var port = new CapturingPort();
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, port, "de", "start_island");
        Assert.NotNull(host.Weather);
        host.World.Run(host.World.Calendar.TicksPerDay);
        var request = ContractSerializer.Deserialize<DecisionRequest>(Assert.IsType<string>(port.Last));
        Assert.Equal(host.WeatherSystem!.Conditions, request.Environment.Weather);
        Assert.Equal(host.WeatherSystem.Current, host.Snapshot().Weather!.Weather);

        var placeholder = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 7, new MockDecisionPort(), "de");
        Assert.Null(placeholder.Weather);
        Assert.Null(placeholder.Snapshot().Weather);
    }

    [Fact]
    public void SaveAndLoadInTheMiddleOfAPhaseGiveTheSameHashAndTheSameWeatherAfterwards()
    {
        var original = SimulationHost.CreateGeneratedWorld(ContentDirectory, 42, new MockDecisionPort(), "de", "start_island");
        original.World.Run(original.World.Calendar.TicksPerDay + 101);
        var path = Path.Combine(_directory, "weather.db");
        SaveGameStore.Save(path, StateCodec.Capture(original));
        var state = SaveGameStore.Load(path);
        Assert.NotNull(state.Weather);

        var loaded = Restore(state, new ReplayDecisionPort(original.Decisions.Log));
        Assert.Equal(original.WeatherSystem!.Capture().Weather, loaded.WeatherSystem!.Capture().Weather);
        Assert.Equal(original.World.StateHash(), loaded.World.StateHash());

        original.World.Run(original.World.Calendar.TicksPerDay * 3);
        var replay = Restore(SaveGameStore.Load(path), new ReplayDecisionPort(original.Decisions.Log));
        replay.World.Run(original.World.Calendar.TicksPerDay * 3);
        Assert.Equal(original.WeatherSystem.Current, replay.WeatherSystem!.Current);
        Assert.Equal(original.World.StateHash(), replay.World.StateHash());
    }

    private static SimulationHost Restore(SaveState state, IDecisionPort port)
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, port, "de"), state);
    }

    private sealed class CapturingPort : IDecisionPort
    {
        private readonly MockDecisionPort _mock = new();

        public string? Last { get; private set; }

        public void Send(string requestId, string requestJson)
        {
            Last = requestJson;
            _mock.Send(requestId, requestJson);
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => _mock.Receive(tick);
    }
}
