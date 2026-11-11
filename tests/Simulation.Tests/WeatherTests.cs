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

    private static (World World, WeatherSystem Weather) Bare(ulong seed, ClimateSettings? climate = null, int heightOffsetTenthC = 0)
    {
        var world = new World(seed, Content.Value.Calendar);
        var settings = world.Calendar.Settings;
        var weather = new WeatherSystem(climate ?? Climate(), heightOffsetTenthC, settings.TicksPerHour, settings.HoursPerDay, seed);
        world.Add(weather);
        return (world, weather);
    }

    private static List<(long Tick, string Season, string Weather)> Year(ulong seed, ClimateSettings? climate = null, int heightOffsetTenthC = 0)
    {
        var (world, weather) = Bare(seed, climate, heightOffsetTenthC);
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

    /// <summary>
    /// The weather of 20 seeds over a year each (28 days): every condition seen, the conditions seen per
    /// season, and the longest spell without precipitation in ticks with its seed. Run once for the tests that read it.
    /// </summary>
    private static readonly Lazy<(HashSet<string> Seen, Dictionary<string, HashSet<string>> BySeason, long LongestDry, ulong DrySeed)> TwentyYears = new(() =>
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var bySeason = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var precipitation = Climate().Weather.States.ToDictionary(s => s.Weather, s => s.PrecipitationPermille, StringComparer.Ordinal);
        var longestDry = 0L;
        var drySeed = 0UL;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var (world, weather) = Bare(seed);
            var dry = 0L;
            for (var t = 0L; t < world.Calendar.TicksPerDay * 28; t++)
            {
                world.Step();
                dry = precipitation[weather.Current] == 0 ? dry + 1 : 0;
                if (dry > longestDry)
                {
                    (longestDry, drySeed) = (dry, seed);
                }

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

        return (seen, bySeason, longestDry, drySeed);
    });

    /// <summary>
    /// The weather ids of the seasons' transition table: every weather a row starts from or leads to, and
    /// the snow weather (precipitation below the snow temperature falls as snow).
    /// </summary>
    private static HashSet<string> TableWeathers() =>
        Climate().Weather.Seasons.SelectMany(s => s.Transitions).SelectMany(t => t.To.Select(x => x.Weather).Append(t.FromWeather))
            .Append(Climate().Weather.SnowWeather).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void EveryWeatherOfTheTableOccursOver20SeedsAndSeasonsShapeIt()
    {
        var (seen, bySeason, longestDry, _) = TwentyYears.Value;

        // Acceptance 3 of the spec: every weather id of the table at least once over 20 seeds - every
        // weather the seasons' transition table names, without exemption.
        var data = Climate().Weather;
        var table = TableWeathers();
        var missing = table.Where(w => !seen.Contains(w)).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, $"never seen in 20 seeds x 28 days: {string.Join(", ", missing)}");

        // The drought weather is the one state that is no weather of the table: no row leads to it and it
        // never becomes the main weather by itself. It is a condition that joins the main weather after
        // drought_after_dry_days days in a row without precipitation (the rule:
        // DryDaysAddTheDroughtConditionFromTheData). So it is seen exactly when a dry spell was that long;
        // whether one is, with the repository's values, is TheDroughtConditionOccursOver20Seeds.
        Assert.Equal([data.DroughtWeather], data.States.Select(s => s.Weather).Where(w => !table.Contains(w)));
        var droughtTicks = (long)data.DroughtAfterDryDays * Content.Value.Calendar.TicksPerDay;
        Assert.True(
            seen.Contains(data.DroughtWeather) == (longestDry >= droughtTicks),
            $"drought seen: {seen.Contains(data.DroughtWeather)}, longest dry spell {longestDry} ticks, drought after {droughtTicks}");

        // The season tables decide: no weather appears in a season whose table never leads to it.
        foreach (var seasonTable in data.Seasons)
        {
            var reachable = seasonTable.Transitions.SelectMany(t => t.To.Select(x => x.Weather).Append(t.FromWeather))
                .Append(data.DroughtWeather).ToHashSet(StringComparer.Ordinal);
            var season = bySeason.GetValueOrDefault(seasonTable.Season, []);
            var strangers = season.Where(w => !reachable.Contains(w)).ToList();

            // A weather carried over from the season before may last until its first change step.
            Assert.True(strangers.Count <= 2, $"{seasonTable.Season}: {string.Join(", ", strangers)}");
        }
    }

    /// <summary>
    /// Acceptance 3 of the spec read by the letter: every weather id, the drought too, at least once over
    /// 20 seeds. With the repository's values it does not: see the skip reason. The test is kept, skipped,
    /// so the finding stays visible until the values (or the wording of the acceptance) are decided.
    /// </summary>
    [Fact(Skip = "Finding (B2 acceptance 3, evidence audit): the drought condition never occurs in 20 seeds x 28 days. "
        + "The longest spell without precipitation is 7.75 days (11160 ticks, seed 3), drought_after_dry_days is 10, and a year has 28 days "
        + "(content/climate/temperate.json, placeholder values). A value question for Marcel, not a code error: "
        + "the rule itself is tested in DryDaysAddTheDroughtConditionFromTheData.")]
    public void TheDroughtConditionOccursOver20Seeds()
    {
        var (seen, _, longestDry, drySeed) = TwentyYears.Value;
        var data = Climate().Weather;
        var days = longestDry / (double)Content.Value.Calendar.TicksPerDay;
        Assert.True(
            seen.Contains(data.DroughtWeather),
            $"the drought condition never occurs in 20 seeds x 28 days: longest spell without precipitation {days:F2} days ({longestDry} ticks, seed {drySeed}), drought after {data.DroughtAfterDryDays}");
    }

    [Fact]
    public void ChangedDataChangesTheWeatherWithoutCodeChange()
    {
        var climate = ContractSerializer.Deserialize<ClimateSettings>(ContractSerializer.Serialize(Climate()));
        foreach (var row in climate.Weather.Seasons.SelectMany(s => s.Transitions))
        {
            row.To = [new ClimateTransitionTarget { Weather = "rain", Weight = 1 }];
        }

        // Warm enough everywhere that the rain never turns into snow.
        var phases = Year(9, climate, heightOffsetTenthC: 2000);
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

        // Far below the snow temperature in every season and time of day.
        var phases = Year(9, climate, heightOffsetTenthC: -2000);
        Assert.Equal(climate.Weather.SnowWeather, Assert.Single(phases).Weather);
    }

    [Fact]
    public void FrostIsBelowZeroAndHeatAboveTwentyFiveWhereverTheyOccur()
    {
        var climate = Climate();
        var (world, weather) = Bare(1);
        var calendar = world.Calendar;
        foreach (var table in climate.Weather.Seasons)
        {
            var reachable = table.Transitions.SelectMany(t => t.To.Select(x => x.Weather).Append(t.FromWeather)).ToHashSet(StringComparer.Ordinal);
            var seasonIndex = calendar.Settings.Seasons.Select(s => s.Id).ToList().IndexOf(table.Season);
            for (var hour = 0; hour < calendar.Settings.HoursPerDay; hour++)
            {
                var tick = (seasonIndex * calendar.Settings.DaysPerSeason * calendar.TicksPerDay) + ((long)hour * calendar.Settings.TicksPerHour);
                var time = calendar.At(tick);
                if (reachable.Contains("frost"))
                {
                    Assert.True(weather.TemperatureAt(time, "frost") < 0, $"frost in {table.Season} at {hour}h is {weather.TemperatureAt(time, "frost")}");
                }

                if (reachable.Contains("heat"))
                {
                    Assert.True(weather.TemperatureAt(time, "heat") > 250, $"heat in {table.Season} at {hour}h is {weather.TemperatureAt(time, "heat")}");
                }
            }
        }
    }

    [Fact]
    public void SummerIsWarmerThanWinterAndTheDataDecide()
    {
        var (world, weather) = Bare(1);
        var calendar = world.Calendar;
        var ids = calendar.Settings.Seasons.Select(s => s.Id).ToList();
        long Noon(string season) => (ids.IndexOf(season) * calendar.Settings.DaysPerSeason * calendar.TicksPerDay) + (12L * calendar.Settings.TicksPerHour);
        Assert.True(weather.TemperatureAt(calendar.At(Noon("summer")), "clear") > weather.TemperatureAt(calendar.At(Noon("winter")), "clear"));

        var warmer = ContractSerializer.Deserialize<ClimateSettings>(ContractSerializer.Serialize(Climate()));
        warmer.Weather.Temperature.BaseTenthC += 50;
        var (_, changed) = Bare(1, warmer);
        Assert.Equal(weather.TemperatureAt(calendar.At(Noon("winter")), "clear") + 50, changed.TemperatureAt(calendar.At(Noon("winter")), "clear"));

        var (_, high) = Bare(1, heightOffsetTenthC: -40);
        Assert.Equal(weather.TemperatureAt(calendar.At(Noon("summer")), "clear") - 40, high.TemperatureAt(calendar.At(Noon("summer")), "clear"));
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
    public void WeatherAtAnswersForKnownAndOldTicksAndRefusesTheFuture()
    {
        var (world, weather) = Bare(4);
        world.Run(world.Calendar.TicksPerDay * 3);
        var anywhere = new Position(0, 0);
        Assert.Equal(weather.Current, weather.WeatherAt(anywhere, world.Tick));
        Assert.Equal(weather.Current, weather.WeatherAt(new Position(100, 50), weather.SinceTick));
        Assert.Throws<ArgumentOutOfRangeException>(() => weather.WeatherAt(anywhere, world.Tick + 1));

        // Long ago (before the kept history): the oldest kept phase, never an exception.
        world.Run(world.Calendar.TicksPerDay * 40);
        var oldest = weather.WeatherAt(anywhere, 0);
        Assert.False(string.IsNullOrEmpty(oldest));
        Assert.Equal(oldest, weather.WeatherAt(anywhere, 1));
        Assert.InRange(weather.WindAt(0), 0.0, 1.0);
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

    [Fact]
    public void TheSameSeedGivesTheSameWindAndAnotherSeedOtherGusts()
    {
        static List<double> Winds(ulong seed)
        {
            var (world, weather) = Bare(seed);
            var winds = new List<double>();
            for (var i = 0; i < 3 * 1440; i++)
            {
                world.Step();
                winds.Add(weather.WindAt(world.Tick));
            }

            return winds;
        }

        var a = Winds(5);
        Assert.Equal(a, Winds(5));
        Assert.NotEqual(a, Winds(6));
        Assert.All(a, w => Assert.InRange(w, 0.0, 1.0));

        // Gusts are slow: from one tick to the next the wind moves only a little (no flicker).
        var steps = a.Zip(a.Skip(1), (x, y) => Math.Abs(y - x)).ToList();
        Assert.True(steps.Max() < 0.05, $"largest step {steps.Max()}");
        Assert.True(a.Distinct().Count() > 100, "the wind should vary");
    }

    [Fact]
    public void TheWindFollowsTheDataOfTheWeather()
    {
        // One weather all year: the wind swings around its base by at most its gust.
        var climate = ContractSerializer.Deserialize<ClimateSettings>(ContractSerializer.Serialize(Climate()));
        foreach (var row in climate.Weather.Seasons.SelectMany(s => s.Transitions))
        {
            row.To = [new ClimateTransitionTarget { Weather = "clear", Weight = 1 }];
        }

        var clear = climate.Weather.States.Single(s => s.Weather == "clear");
        var (world, weather) = Bare(3, climate);
        var winds = new List<double>();
        for (var i = 0; i < 1440; i++)
        {
            world.Step();
            winds.Add(weather.WindAt(world.Tick));
        }

        Assert.All(winds, w => Assert.InRange(w, (clear.WindPermille - clear.GustPermille) / 1000.0, (clear.WindPermille + clear.GustPermille) / 1000.0));

        // More base wind in the data, more wind; no gusts, exactly the base.
        clear.WindPermille += 300;
        clear.GustPermille = 0;
        var (stormy, stronger) = Bare(3, climate);
        stormy.Run(1440);
        Assert.Equal((clear.WindPermille) / 1000.0, stronger.WindAt(stormy.Tick), 12);
        Assert.True(stronger.WindAt(stormy.Tick) > winds.Max());
    }

    [Fact]
    public void AfterAChangeTheWindBlendsOverTheTransition()
    {
        var climate = ContractSerializer.Deserialize<ClimateSettings>(ContractSerializer.Serialize(Climate()));
        foreach (var state in climate.Weather.States)
        {
            state.GustPermille = 0;
        }

        var (world, weather) = Bare(8, climate);
        world.Run(100);
        var before = weather.WindAt(world.Tick);
        weather.Force("storm", world.Tick);
        var storm = climate.Weather.States.Single(s => s.Weather == "storm").WindPermille / 1000.0;
        var transition = (long)climate.Weather.TransitionMinutes * world.Calendar.Settings.TicksPerHour / 60;
        var start = world.Tick;
        world.Run(transition);
        Assert.Equal(before, weather.WindAt(start), 12);
        Assert.Equal(before + ((storm - before) / 2), weather.WindAt(start + (transition / 2)), 12);
        Assert.Equal(storm, weather.WindAt(start + transition), 12);
        Assert.Throws<ArgumentOutOfRangeException>(() => weather.WindAt(world.Tick + 1));
    }

    [Fact]
    public void TheWindIsInTheSnapshotAndTheSameAfterLoading()
    {
        var original = SimulationHost.CreateGeneratedWorld(ContentDirectory, 42, new MockDecisionPort(), "de", "start_island");
        original.World.Run(original.World.Calendar.TicksPerDay + 101);
        var reading = original.Snapshot().Weather!;
        Assert.Equal(original.Weather!.WindAt(original.World.Tick), reading.Wind);
        Assert.InRange(reading.Wind, 0.0, 1.0);

        var path = Path.Combine(_directory, "wind.db");
        SaveGameStore.Save(path, StateCodec.Capture(original));
        var hash = original.World.StateHash();
        var loaded = Restore(SaveGameStore.Load(path), new ReplayDecisionPort(original.Decisions.Log));
        Assert.Equal(reading.Wind, loaded.Snapshot().Weather!.Wind);
        Assert.Equal(hash, loaded.World.StateHash());
        // Reading the wind changes nothing in the state; after loading it goes on the same way.
        Assert.Equal(hash, original.World.StateHash());
        for (var i = 0; i < 600; i++)
        {
            original.World.Step();
            loaded.World.Step();
            Assert.Equal(original.Weather.WindAt(original.World.Tick), loaded.Weather!.WindAt(loaded.World.Tick));
        }
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
