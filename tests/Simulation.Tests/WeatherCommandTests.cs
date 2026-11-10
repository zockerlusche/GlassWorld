using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Player;
using GlassWorld.Simulation.State;
using GlassWorld.Simulation.Weather;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B2-5 R13: the player's weather as a logged command that costs favour, and the start and end of storm and
/// drought as tracked signals (world events, saved, hashed, replayed).
/// </summary>
public sealed class WeatherCommandTests : IDisposable
{
    private const string Preset = "start_island";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public WeatherCommandTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static ClimateSettings Climate() => Content.Value.WorldGen.Climates["temperate"];

    private static ClimateSettings ClimateCopy() => ContractSerializer.Deserialize<ClimateSettings>(ContractSerializer.Serialize(Climate()));

    /// <summary>The signal of the data that is not the drought (the storm), and the drought signal.</summary>
    private static (ClimateSignal Storm, ClimateSignal Drought) Signals()
    {
        var weather = Climate().Weather;
        return (weather.Signals!.First(s => s.Condition != weather.DroughtWeather), weather.Signals!.First(s => s.Condition == weather.DroughtWeather));
    }

    private static string Calm() => Climate().Weather.States.First().Weather;

    private static string Wet() => Climate().Weather.States.First(s => s.PrecipitationPermille > 0 && s.Weather != Signals().Storm.Condition).Weather;

    private static (World World, WeatherSystem Weather, List<WorldEvent> Events) Bare(ulong seed, ClimateSettings? climate = null)
    {
        var world = new World(seed, Content.Value.Calendar);
        var settings = world.Calendar.Settings;
        var events = new List<WorldEvent>();
        var weather = new WeatherSystem(climate ?? Climate(), 0, settings.TicksPerHour, settings.HoursPerDay, seed) { Report = events.Add };
        world.Add(weather);
        return (world, weather, events);
    }

    /// <summary>A climate that never rains by itself and counts one dry day as a drought.</summary>
    private static ClimateSettings DryClimate()
    {
        var climate = ClimateCopy();
        climate.Weather.DroughtAfterDryDays = 1;
        foreach (var transition in climate.Weather.Seasons.SelectMany(s => s.Transitions))
        {
            transition.To = [new ClimateTransitionTarget { Weather = Calm(), Weight = 1 }];
        }

        return climate;
    }

    private static PlayerCommand SetWeather(string? weather) => new(WeatherIntervention.SetWeather, null) { TypeId = weather };

    private static SimulationHost Island(ulong seed, IDecisionPort? port = null) =>
        SimulationHost.CreateGeneratedWorld(ContentDirectory, seed, port ?? new MockDecisionPort(), "de", Preset);

    [Fact]
    public void TheDataNameTheSignalsAndTheirEventsAreInTheVocabulary()
    {
        var (storm, drought) = Signals();
        var known = Content.Value.EventKinds.ToList();
        Assert.All(new[] { storm.StartEvent, storm.EndEvent, drought.StartEvent, drought.EndEvent }, e => Assert.Contains(e, known));
        Assert.Contains(WeatherIntervention.WeatherSet, known);
    }

    [Fact]
    public void AStormIsReportedOnceWhenItBeginsAndOnceWhenItEnds()
    {
        var (storm, _) = Signals();
        var (world, weather, events) = Bare(3);
        var changes = new List<(string Condition, bool Began, long Tick)>();
        weather.SignalChanged += (c, began, tick) => changes.Add((c, began, tick));
        world.Run(5);

        weather.Force(storm.Condition, world.Tick);
        Assert.Equal([(storm.Condition, true, 5L)], changes);
        var began = Assert.Single(events);
        Assert.Equal((storm.StartEvent, 5L, storm.Condition, string.Empty), (began.Kind, began.Tick, began.Detail, began.InhabitantId));
        Assert.Equal([new KeyValuePair<string, long>(storm.Condition, 5)], weather.Signals);
        Assert.Equal(weather.Signals, weather.Read(world).Signals);

        // Staying in the storm reports nothing more.
        world.Run(3);
        Assert.Single(events);

        weather.Force(Calm(), world.Tick);
        Assert.Equal([storm.StartEvent, storm.EndEvent], events.Select(e => e.Kind));
        Assert.Equal(8, events[^1].Tick);
        Assert.Empty(weather.Signals);
    }

    [Fact]
    public void ADroughtBeginsAfterTheDryDaysAndEndsWithTheRain()
    {
        var (_, drought) = Signals();
        var (world, weather, events) = Bare(4, DryClimate());
        var ticksPerDay = world.Calendar.TicksPerDay;
        world.Run(ticksPerDay - 1);
        Assert.Empty(events);

        // The dry ticks reach the limit of the data in the last tick of the first day.
        world.Run(1);
        var began = Assert.Single(events);
        Assert.Equal((drought.StartEvent, ticksPerDay - 1, drought.Condition), (began.Kind, began.Tick, began.Detail));
        Assert.Contains(drought.Condition, weather.Conditions);
        Assert.Equal(ticksPerDay - 1, Assert.Single(weather.Signals).Value);

        world.Run(ticksPerDay + 1);
        Assert.Single(events);

        // Rain ends it at the next tick (the dry ticks start again); not at a change step, where the table decides anew.
        weather.Force(Wet(), world.Tick);
        world.Run(1);
        Assert.Equal([drought.StartEvent, drought.EndEvent], events.Select(e => e.Kind));
        Assert.Empty(weather.Signals);
    }

    [Fact]
    public void OverAYearEveryStartIsFollowedByItsEnd()
    {
        var (world, weather, events) = Bare(11);
        var on = new HashSet<string>(StringComparer.Ordinal);
        var rules = Climate().Weather.Signals!.ToList();
        for (var tick = 0L; tick < world.Calendar.TicksPerDay * 28 * 4; tick++)
        {
            var before = events.Count;
            world.Step();
            foreach (var e in events.Skip(before))
            {
                var rule = rules.Single(r => r.Condition == e.Detail);
                Assert.True(e.Kind == rule.StartEvent ? on.Add(rule.Condition) : e.Kind == rule.EndEvent && on.Remove(rule.Condition), $"unexpected {e.Kind} at {e.Tick}");
            }

            Assert.Equal(rules.Select(r => r.Condition).Where(c => weather.Conditions.Contains(c)).Order(StringComparer.Ordinal), weather.Signals.Select(s => s.Key));
        }

        Assert.Contains(events, e => e.Kind == Signals().Storm.StartEvent);
    }

    [Fact]
    public void WithoutASignalOnTheHashIsTheOneOfAClimateWithoutSignals()
    {
        var (storm, _) = Signals();
        var plain = ClimateCopy();
        plain.Weather.Signals = null;
        var (world, weather, _) = Bare(9);
        var (plainWorld, plainWeather, plainEvents) = Bare(9, plain);
        world.Run(50);
        plainWorld.Run(50);
        Assert.Equal(plainWorld.StateHash(), world.StateHash());

        // While the storm is on, it is part of the state; afterwards the hashes meet again.
        weather.Force(storm.Condition, world.Tick);
        plainWeather.Force(storm.Condition, plainWorld.Tick);
        Assert.NotEqual(plainWorld.StateHash(), world.StateHash());
        Assert.Empty(plainEvents);
        weather.Force(Calm(), world.Tick);
        plainWeather.Force(Calm(), plainWorld.Tick);
        Assert.Equal(plainWorld.StateHash(), world.StateHash());
    }

    [Fact]
    public void TheWeatherCommandSetsTheWeatherCostsFavourAndIsLogged()
    {
        var host = Island(7);
        host.World.Run(10);
        var cost = host.Player.CostOf("weather");
        Assert.True(cost > 0);
        var before = host.Player.Favor;
        var wanted = Wet();
        Assert.NotEqual(wanted, host.WeatherSystem!.Current);

        Assert.True(host.Decisions.Submit(SetWeather(wanted)).Accepted);
        Assert.NotEqual(wanted, host.WeatherSystem.Current);
        host.World.Step();

        Assert.Equal(wanted, host.WeatherSystem.Current);
        Assert.Equal(10, host.WeatherSystem.SinceTick);
        Assert.Equal(wanted, host.Snapshot().Weather!.Weather);
        Assert.InRange(host.Player.Favor, before - cost, before - cost + 1);
        var logged = Assert.Single(host.Decisions.Log.Entries, e => e.Kind == DecisionLogKind.Command);
        Assert.Equal(10, logged.Tick);
        Assert.Contains(WeatherIntervention.SetWeather, logged.Json, StringComparison.Ordinal);
        var reported = Assert.Single(host.Inhabitants.Events, e => e.Kind == WeatherIntervention.WeatherSet);
        Assert.Equal(wanted, reported.Detail);
    }

    [Fact]
    public void AStormByThePlayerIsSignalledThroughTheWorldEvents()
    {
        var (storm, _) = Signals();
        var host = Island(7);
        host.World.Run(4);
        Assert.True(host.Decisions.Submit(SetWeather(storm.Condition)).Accepted);
        host.World.Step();

        Assert.Equal([WeatherIntervention.WeatherSet, storm.StartEvent], host.Inhabitants.Events.Where(e => e.InhabitantId.Length == 0).Select(e => e.Kind));
        Assert.Equal(storm.Condition, Assert.Single(host.Snapshot().Weather!.Signals).Key);
    }

    [Fact]
    public void TheWeatherCommandIsRefusedWithAReason()
    {
        var host = Island(7);
        host.World.Run(3);
        Assert.Equal(WeatherIntervention.ReasonUnknownWeather, host.Decisions.Submit(SetWeather("hail_of_frogs")).ReasonKey);
        Assert.Equal(WeatherIntervention.ReasonUnknownWeather, host.Decisions.Submit(SetWeather(null)).ReasonKey);
        Assert.Equal(WeatherIntervention.ReasonUnchanged, host.Decisions.Submit(SetWeather(host.WeatherSystem!.Current)).ReasonKey);

        // Not enough favour: the smallest start option does not pay for the weather.
        var poor = Island(7);
        var smallest = poor.Content.Favor.StartOptions.Min();
        Assert.True(smallest < poor.Player.CostOf("weather"));
        poor.Player.SetStartFavor(smallest);
        Assert.Equal(PlayerSystem.ReasonNoFavor, poor.Decisions.Submit(SetWeather(Wet())).ReasonKey);

        // A world without weather (placeholder) refuses every weather.
        var placeholder = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 7, new MockDecisionPort(), "de");
        Assert.Equal(WeatherIntervention.ReasonNoWeather, placeholder.Decisions.Submit(SetWeather(Wet())).ReasonKey);

        host.World.Step();
        Assert.DoesNotContain(host.Decisions.Log.Entries, e => e.Kind == DecisionLogKind.Command);
    }

    [Fact]
    public void ASecondCommandInTheSameTickThatCanNoLongerBePaidDoesNothing()
    {
        var host = Island(7);
        host.World.Run(3);
        var (storm, _) = Signals();

        // Both pass the check at submit; the favour of the middle start option pays for one only.
        Assert.True(host.Decisions.Submit(SetWeather(Wet())).Accepted);
        Assert.True(host.Decisions.Submit(SetWeather(storm.Condition)).Accepted);
        Assert.True(host.Player.Favor < 2 * host.Player.CostOf("weather"));
        host.World.Step();

        Assert.Equal(Wet(), host.WeatherSystem!.Current);
        Assert.Single(host.Inhabitants.Events, e => e.Kind == WeatherIntervention.WeatherSet);
    }

    [Fact]
    public void AReplayFromSeedAndLogGivesTheSameWeatherAndWorld()
    {
        var (storm, _) = Signals();
        var original = Island(42);
        var ticksPerHour = original.World.Calendar.Settings.TicksPerHour;
        original.World.Run(ticksPerHour * 5);
        Assert.True(original.Decisions.Submit(SetWeather(storm.Condition)).Accepted);
        original.World.Run(ticksPerHour * 30);

        var replay = Island(42, new ReplayDecisionPort(original.Decisions.Log));
        replay.Decisions.LoadReplay(original.Decisions.Log);
        for (var tick = 0L; tick < original.World.Tick; tick++)
        {
            replay.World.Step();
            if (tick == (ticksPerHour * 5) + 1)
            {
                Assert.Equal(storm.Condition, replay.WeatherSystem!.Current);
                Assert.NotEmpty(replay.WeatherSystem.Signals);
            }
        }

        Assert.Equal(original.Decisions.Log.Entries, replay.Decisions.Log.Entries);
        Assert.Equal(original.Player.Favor, replay.Player.Favor);
        Assert.Equal(original.World.StateHash(), replay.World.StateHash());
    }

    [Fact]
    public void ASaveDuringAStormKeepsTheSignalAndReportsItsEndOnce()
    {
        var (storm, _) = Signals();
        var original = Island(42);
        original.World.Run(6);
        Assert.True(original.Decisions.Submit(SetWeather(storm.Condition)).Accepted);
        original.World.Run(4);
        Assert.NotEmpty(original.WeatherSystem!.Signals);

        var path = Path.Combine(_directory, "storm.db");
        SaveGameStore.Save(path, StateCodec.Capture(original));
        var state = SaveGameStore.Load(path);
        Assert.Equal(original.WeatherSystem.Signals, state.Weather!.Signals);
        var loaded = Restore(state);
        Assert.Equal(original.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(original.WeatherSystem.Signals, loaded.WeatherSystem!.Signals);

        // Loading reports nothing; the end comes once, when the storm is over.
        Assert.DoesNotContain(loaded.Inhabitants.Events, e => e.Kind == storm.StartEvent);
        loaded.WeatherSystem.Force(Calm(), loaded.World.Tick);
        Assert.Single(loaded.Inhabitants.Events, e => e.Kind == storm.EndEvent);
    }

    [Fact]
    public void ASaveFromBeforeTheSignalsGetsThemFromItsWeather()
    {
        var (storm, _) = Signals();
        var original = Island(42);
        original.World.Run(6);
        original.Decisions.Submit(SetWeather(storm.Condition));
        original.World.Run(4);
        var state = StateCodec.Capture(original);

        // An older save has the weather but no signals: the storm that is on counts from its own start.
        var old = state with { Weather = state.Weather! with { Signals = null } };
        var loaded = Restore(old);
        Assert.Equal(original.WeatherSystem!.Signals, loaded.WeatherSystem!.Signals);
        Assert.Equal(original.World.StateHash(), loaded.World.StateHash());
    }

    private static SimulationHost Restore(SaveState state)
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return StateCodec.Restore(new SimulationHostFactory(Content.Value, settings, new MockDecisionPort(), "de"), state);
    }
}
