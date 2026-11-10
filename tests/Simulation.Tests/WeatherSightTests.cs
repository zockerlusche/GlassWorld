using System.Text.Json.Nodes;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Exploration;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;
using GlassWorld.Simulation.Weather;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B2594: the weather lowers how far the inhabitants see, in the core and not only in the look. One factor
/// from the climate data (sight_permille per weather state) for both ranges: the exploration range in
/// cells and the perception radius in metres. The tests read the weathers and values from the data.
/// </summary>
public sealed class WeatherSightTests : IDisposable
{
    private const string Preset = "start_island";
    private const string Water = "spring_water";
    private const string Drinkable = "drinkable";
    private const string Thirst = "thirst";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public WeatherSightTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static ClimateSettings Climate => Content.Value.WorldGen.Climates[Content.Value.WorldGen.Presets[Preset].Climate.ClimateId];

    /// <summary>The weather of the data in which one sees least (fog in the temperate climate).</summary>
    private static ClimateWeatherState Thick => Climate.Weather.States.MinBy(s => s.SightPermille ?? Sight.Full)!;

    /// <summary>The weather a world begins with; the data give it full sight.</summary>
    private static ClimateWeatherState Open => Climate.Weather.States.First();

    private static double PerceptionRadiusM => Content.Value.Brain.Weights.PerceptionRadiusM;

    [Fact]
    public void TheSightInEveryWeatherComesFromTheClimateData()
    {
        var host = Island(7);
        var weather = host.WeatherSystem!;
        var exploration = host.Exploration!;
        var at = host.Map!.CellOf(host.Inhabitants.All.First().Position)!.Value;
        Assert.Equal(Sight.Full, Open.SightPermille ?? Sight.Full);
        Assert.InRange(Thick.SightPermille ?? Sight.Full, 1, Sight.Full - 1);
        weather.Force(Open.Weather, host.World.Tick);
        var fullCells = exploration.SightRangeCells(at.X, at.Z);
        Assert.Equal(PerceptionRadiusM, host.Inhabitants.SightRadiusM);

        foreach (var state in Climate.Weather.States)
        {
            weather.Force(state.Weather, host.World.Tick);
            var permille = state.SightPermille ?? Sight.Full;

            Assert.Equal(permille, weather.SightPermille);
            Assert.Equal(permille, host.Inhabitants.Sight.Permille);
            Assert.Equal(Math.Max(1, fullCells * permille / Sight.Full), exploration.SightRangeCells(at.X, at.Z));
            Assert.Equal(PerceptionRadiusM * permille / Sight.Full, host.Inhabitants.SightRadiusM, 9);
        }

        weather.Force(Thick.Weather, host.World.Tick);
        Assert.True(exploration.SightRangeCells(at.X, at.Z) < fullCells);
        Assert.True(host.Inhabitants.SightRadiusM < PerceptionRadiusM);
    }

    [Fact]
    public void ARangeNeverFallsBelowOneCellAndAWorldWithoutWeatherSeesFully()
    {
        var weather = new WeatherSystem(Climate, 0, 60, 24, 1);
        var sight = new Sight(weather);
        weather.Force(Thick.Weather, 0);

        Assert.Equal(Thick.SightPermille ?? Sight.Full, sight.Permille);
        Assert.Equal(1, sight.Cells(1));
        Assert.Equal(1, sight.Cells(2));
        Assert.Equal(16 * sight.Permille / Sight.Full, sight.Cells(16));

        Assert.Equal(Sight.Full, Sight.Clear.Permille);
        Assert.Equal(16, Sight.Clear.Cells(16));
        Assert.Equal(PerceptionRadiusM, Sight.Clear.Metres(PerceptionRadiusM));
        var placeholder = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), "de");
        Assert.Null(placeholder.WeatherSystem);
        Assert.Equal(PerceptionRadiusM, placeholder.Inhabitants.SightRadiusM);
    }

    [Fact]
    public void ExploringRevealsLessInFogAndCatchesUpWhenItClears()
    {
        var host = Island(7);
        var weather = host.WeatherSystem!;
        var preset = host.Content.WorldGen.Presets[Preset];

        // Two fresh cloud covers over the same island and the same inhabitants: one looks in the open
        // weather, the other in the thick one.
        var open = new ExplorationSystem(preset.Exploration, host.Map!, host.Inhabitants, preset.Terrain.SeaLevel);
        var thick = new ExplorationSystem(preset.Exploration, host.Map!, host.Inhabitants, preset.Terrain.SeaLevel);
        weather.Force(Open.Weather, host.World.Tick);
        open.Look();
        weather.Force(Thick.Weather, host.World.Tick);
        thick.Look();
        var inFog = thick.Version;
        Assert.InRange(inFog, 1, open.Version - 1);
        Assert.All(thick.RevealedSince(0), c => Assert.True(open.IsRevealed(c % open.Width, c / open.Width)));

        // Looking again in the same weather from the same cell adds nothing.
        thick.Look();
        Assert.Equal(inFog, thick.Version);

        // It clears: everyone looks again from where it stands, without taking a step.
        weather.Force(Open.Weather, host.World.Tick);
        thick.Look();
        Assert.Equal(open.RevealedSince(0).Order(), thick.RevealedSince(0).Order());

        // What was seen stays seen when the fog comes back.
        weather.Force(Thick.Weather, host.World.Tick);
        thick.Look();
        Assert.Equal(open.Version, thick.Version);
    }

    [Fact]
    public void InTheRunningWorldSomeoneNewSeesLittleInFogAndMoreWhenItClears()
    {
        var host = Island(7);
        var weather = host.WeatherSystem!;
        var exploration = host.Exploration!;
        var parent = host.Inhabitants.All.First();
        var child = new Inhabitant(
            "inh-000099",
            parent.Sex,
            parent.Group,
            parent.Position,
            new Dictionary<string, double>(parent.Needs, StringComparer.Ordinal),
            new Dictionary<string, double>(parent.Moods, StringComparer.Ordinal),
            new Dictionary<string, double>(parent.Traits, StringComparer.Ordinal),
            parent.WillToLive);
        host.Inhabitants.Add(child);
        weather.Force(Thick.Weather, host.World.Tick);

        host.World.Step();
        var cell = host.Map!.CellOf(child.Position)!.Value;
        var range = exploration.SightRangeCells(cell.X, cell.Z);
        var inFog = exploration.SeenCount(child.Id);
        Assert.InRange(inFog, 1, ((2 * range) + 1) * ((2 * range) + 1));
        var revealed = exploration.Version;

        weather.Force(Open.Weather, host.World.Tick);
        host.World.Step();

        // The child sees everything around the cell it stands in now, as far as one sees in the open.
        cell = host.Map.CellOf(child.Position)!.Value;
        var wide = exploration.SightRangeCells(cell.X, cell.Z);
        Assert.True(wide > range);
        Assert.True(exploration.SeenCount(child.Id) > inFog);
        for (var dz = -wide; dz <= wide; dz++)
        {
            for (var dx = -wide; dx <= wide; dx++)
            {
                var (x, z) = (cell.X + dx, cell.Z + dz);
                if ((dx * dx) + (dz * dz) <= wide * wide && x >= 0 && z >= 0 && x < exploration.Width && z < exploration.Depth)
                {
                    Assert.True(exploration.HasSeen(child.Id, x, z));
                }
            }
        }

        Assert.True(exploration.Version >= revealed);
    }

    [Fact]
    public void TheDeciderIsOfferedFewerThingsAndInhabitantsInFogAndIsToldHowFarItSees()
    {
        var open = Asked(Open.Weather);
        var thick = Asked(Thick.Weather);
        var sight = PerceptionRadiusM * (Thick.SightPermille ?? Sight.Full) / Sight.Full;

        Assert.Equal(PerceptionRadiusM, open.Request.Environment.VisibilityM);
        Assert.Equal(Math.Round(sight, 1), thick.Request.Environment.VisibilityM);
        Assert.Contains(Thick.Weather, thick.Request.Environment.Weather);

        // The near thing is offered in both, the far thing and the far inhabitant only in the open.
        Assert.Contains(open.Request.Perception.Objects, o => o.Id == open.Near);
        Assert.Contains(open.Request.Perception.Objects, o => o.Id == open.Far);
        Assert.Contains(open.Request.Perception.Inhabitants!, i => i.Id == open.Other);
        Assert.Contains(thick.Request.Perception.Objects, o => o.Id == thick.Near);
        Assert.DoesNotContain(thick.Request.Perception.Objects, o => o.Id == thick.Far);
        Assert.DoesNotContain(thick.Request.Perception.Inhabitants!, i => i.Id == thick.Other);
        Assert.True(thick.Request.Perception.Objects.Count < open.Request.Perception.Objects.Count);
        Assert.All(thick.Request.Perception.Objects, o => Assert.True(o.DistanceM <= sight));
        Assert.All(thick.Request.Perception.Inhabitants!, i => Assert.True(i.DistanceM <= sight));
    }

    [Fact]
    public void InFogAKnownPlaceStaysAGoalWithinTheUsualRadiusAndAnUnknownThingOutOfSightIsNone()
    {
        var host = Island(7);
        var weather = host.WeatherSystem!;
        var inhabitant = Parched(host);
        var home = inhabitant.Position;
        var sight = PerceptionRadiusM * (Thick.SightPermille ?? Sight.Full) / Sight.Full;
        foreach (var obj in host.Objects.All.ToList())
        {
            host.Objects.Remove(obj.Id);
        }

        // Water it knows to be drinkable, out of sight in the fog but within the usual radius.
        var water = host.Objects.Spawn(Water, LandAt(host, inhabitant.Position, sight + ((PerceptionRadiusM - sight) * 0.3), sight + ((PerceptionRadiusM - sight) * 0.7)));
        inhabitant.Beliefs.Set(Water, Drinkable, true, 1.0, KnowledgeSources.Experienced, null, 1);

        // In the open it sees the water and goes there.
        weather.Force(Open.Weather, host.World.Tick);
        Assert.Equal(water.Id, Chosen(host, inhabitant, home)?.TargetId);

        // In the fog it does not see it: no goal, it looks further away instead.
        weather.Force(Thick.Weather, host.World.Tick);
        Assert.Null(Chosen(host, inhabitant, home)?.TargetId);

        // Once it remembers the place the water is a goal again, fog or not.
        inhabitant.RememberPlace(water.Id, new PlaceMemory(Water, water.Position, host.World.Tick), host.Content.Knowledge.Looking!.PlaceMemoryMax);
        Assert.Equal(water.Id, Chosen(host, inhabitant, home)?.TargetId);

        // A remembered thing that is gone is none, and neither is one beyond the usual radius.
        host.Objects.Remove(water.Id);
        Assert.Null(Chosen(host, inhabitant, home)?.TargetId);
        var distant = host.Objects.Spawn(Water, LandAt(host, inhabitant.Position, PerceptionRadiusM * 1.2, PerceptionRadiusM * 1.8));
        inhabitant.RememberPlace(distant.Id, new PlaceMemory(Water, distant.Position, host.World.Tick), host.Content.Knowledge.Looking!.PlaceMemoryMax);
        Assert.Null(Chosen(host, inhabitant, home)?.TargetId);
    }

    [Fact]
    public void AThingThatWasUsedAndIsStillThereIsARememberedPlace()
    {
        var host = Island(7);
        var inhabitant = Parched(host);
        foreach (var obj in host.Objects.All.ToList())
        {
            host.Objects.Remove(obj.Id);
        }

        var water = host.Objects.Spawn(Water, LandAt(host, inhabitant.Position, 2, 8));
        inhabitant.Beliefs.Set(Water, Drinkable, true, 1.0, KnowledgeSources.Experienced, null, 1);
        Assert.Empty(inhabitant.Places);

        for (var tick = 0; tick < 600 && inhabitant.Needs[Thirst] < 0.5; tick++)
        {
            host.World.Step();
        }

        Assert.True(inhabitant.Needs[Thirst] >= 0.5);
        var place = Assert.Single(inhabitant.Places);
        Assert.Equal(water.Id, place.Key);
        Assert.Equal(Water, place.Value.TypeId);
        Assert.Equal(water.Position, place.Value.Position);
    }

    [Fact]
    public void ASavedWorldGoesOnTheSameInFogInTheOpenAndRightAtTheChange()
    {
        var host = Island(42);
        var weather = host.WeatherSystem!;
        var changeTicks = (long)Climate.Weather.ChangeEveryHours * host.World.Calendar.Settings.TicksPerHour;

        // Just after a change step, so the forced weather holds for the ticks that follow.
        host.World.Run((2 * changeTicks) + 5);
        weather.Force(Thick.Weather, host.World.Tick);

        // Right at the change: the fog is there, nobody has looked in it yet.
        var loaded = SavedAndLoaded(host, "at-fog.db");
        AssertGoOnTheSame(host, loaded, 40);

        // In the middle of the fog.
        Assert.Equal(Thick.Weather, host.WeatherSystem!.Current);
        loaded = SavedAndLoaded(host, "mid-fog.db");
        Assert.Equal(Thick.Weather, loaded.WeatherSystem!.Current);
        AssertGoOnTheSame(host, loaded, 60);

        // Right when it clears: everyone is about to look again from where it stands. A world that forgot
        // how far each one saw at its last look would not look again here and keep less revealed.
        var exploration = host.Exploration!;
        var before = exploration.Version;
        host.WeatherSystem.Force(Open.Weather, host.World.Tick);
        var pending = StateCodec.Capture(host).Exploration!.Looks!;
        Assert.Equal(host.Inhabitants.All.Count(), pending.Count);
        Assert.All(pending, l => Assert.True(l.Value.RangeCells < exploration.SightRangeCells(l.Value.Cell % exploration.Width, l.Value.Cell / exploration.Width)));
        loaded = SavedAndLoaded(host, "at-clear.db");
        host.World.Step();
        loaded.World.Step();
        Assert.True(exploration.Version >= before);
        Assert.All(StateCodec.Capture(loaded).Exploration!.Looks!, l => Assert.Equal(exploration.SightRangeCells(l.Value.Cell % exploration.Width, l.Value.Cell / exploration.Width), l.Value.RangeCells));
        AssertGoOnTheSame(host, loaded, 40);

        // In the open again.
        loaded = SavedAndLoaded(host, "mid-clear.db");
        AssertGoOnTheSame(host, loaded, 300);
    }

    [Fact]
    public void ASaveFromBeforeTheLastLooksLetsEveryoneLookAgainAndIsSavedInTheNewFormat()
    {
        var host = Island(7);
        host.World.Run(400);
        var path = Path.Combine(_directory, "v7.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        Assert.Equal(host.Inhabitants.All.Count(), StateCodec.Capture(host).Exploration!.Looks!.Count);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE exploration DROP COLUMN looked_cell; ALTER TABLE exploration DROP COLUMN looked_range; PRAGMA user_version = 7;";
            command.ExecuteNonQuery();
        }

        Assert.Equal(7, SaveGameStore.VersionOf(path));
        var older = SaveGameStore.Load(path);
        Assert.Empty(older.Exploration!.Looks!);
        var loaded = Restore(older);

        // The seen cells are all there; at the next tick everyone looks again in the same weather, which
        // adds nothing, and from then on the two worlds are the same again.
        Assert.Equal(host.Exploration!.RevealedSince(0).Order(), loaded.Exploration!.RevealedSince(0).Order());
        host.World.Step();
        loaded.World.Step();
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());

        SaveGameStore.Save(path, StateCodec.Capture(loaded));
        Assert.Equal(SaveGameStore.SchemaVersion, SaveGameStore.VersionOf(path));
        Assert.Equal(loaded.World.StateHash(), Restore(SaveGameStore.Load(path)).World.StateHash());
    }

    [Fact]
    public void ChangingTheDataChangesHowFarOneSeesWithoutAnyCode()
    {
        // The thick weather with full sight, and the open weather with half of it: only the climate file differs.
        var changed = CopyContent();
        var file = Path.Combine(changed, "climate", Path.GetFileName(ClimateFile()));
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        foreach (var state in json["weather"]!["states"]!.AsArray())
        {
            var id = state!["weather"]!.GetValue<string>();
            if (id == Thick.Weather)
            {
                state["sight_permille"] = Sight.Full;
            }
            else if (id == Open.Weather)
            {
                state["sight_permille"] = Sight.Full / 2;
            }
        }

        File.WriteAllText(file, json.ToJsonString());
        var usual = Island(7);
        var other = SimulationHost.CreateGeneratedWorld(changed, 7, new MockDecisionPort(), "de", Preset);
        var at = usual.Map!.CellOf(usual.Inhabitants.All.First().Position)!.Value;

        usual.WeatherSystem!.Force(Thick.Weather, 0);
        other.WeatherSystem!.Force(Thick.Weather, 0);
        Assert.True(usual.Exploration!.SightRangeCells(at.X, at.Z) < other.Exploration!.SightRangeCells(at.X, at.Z));
        Assert.True(usual.Inhabitants.SightRadiusM < other.Inhabitants.SightRadiusM);
        Assert.Equal(PerceptionRadiusM, other.Inhabitants.SightRadiusM);

        usual.WeatherSystem.Force(Open.Weather, 0);
        other.WeatherSystem.Force(Open.Weather, 0);
        Assert.Equal(PerceptionRadiusM / 2, other.Inhabitants.SightRadiusM, 9);
        Assert.Equal(usual.Exploration.SightRangeCells(at.X, at.Z) / 2, other.Exploration.SightRangeCells(at.X, at.Z));

        // And the world behaves differently: at its start the changed one has revealed less.
        Assert.True(other.Exploration.Version < usual.Exploration.Version);
    }

    private static SimulationHost Island(ulong seed) =>
        SimulationHost.CreateGeneratedWorld(ContentDirectory, seed, new MockDecisionPort(), "de", Preset);

    private static SimulationHost Restore(SaveState state) =>
        StateCodec.Restore(
            new SimulationHostFactory(
                Content.Value,
                ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json"))),
                new MockDecisionPort(),
                "de"),
            state);

    private SimulationHost SavedAndLoaded(SimulationHost host, string file)
    {
        var path = Path.Combine(_directory, file);
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loaded = Restore(SaveGameStore.Load(path));
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        return loaded;
    }

    /// <summary>Both worlds run on and stay the same: hash, weather and cloud cover.</summary>
    private static void AssertGoOnTheSame(SimulationHost a, SimulationHost b, int ticks)
    {
        a.World.Run(ticks);
        b.World.Run(ticks);
        Assert.Equal(a.World.StateHash(), b.World.StateHash());
        Assert.Equal(a.WeatherSystem!.Current, b.WeatherSystem!.Current);
        Assert.Equal(a.Exploration!.RevealedSince(0).Order(), b.Exploration!.RevealedSince(0).Order());
        foreach (var inhabitant in a.Inhabitants.All)
        {
            Assert.Equal(a.Exploration.SeenCount(inhabitant.Id), b.Exploration.SeenCount(inhabitant.Id));
        }
    }

    /// <summary>The first inhabitant, sated except for a pressing thirst and without anything in mind.</summary>
    private static Inhabitant Parched(SimulationHost host)
    {
        var inhabitant = host.Inhabitants.All.First();
        foreach (var need in inhabitant.Needs.Keys.ToList())
        {
            inhabitant.Needs[need] = 1.0;
        }

        inhabitant.Needs[Thirst] = 0.1;
        return inhabitant;
    }

    /// <summary>What the routine chooses for the inhabitant at the next tick, from where it stood at first and with its thirst still pressing.</summary>
    private static Activity? Chosen(SimulationHost host, Inhabitant inhabitant, Position home)
    {
        inhabitant.Position = home;
        inhabitant.Activity = null;
        inhabitant.Planned = null;
        inhabitant.Needs[Thirst] = 0.1;
        host.World.Step();
        return inhabitant.Activity;
    }

    /// <summary>The centre of a dry cell the inhabitant can walk to, between the two distances in metres.</summary>
    private static Position LandAt(SimulationHost host, Position from, double least, double most)
    {
        var map = host.Map!;
        for (var z = 0; z < map.Map.DepthCells; z++)
        {
            for (var x = 0; x < map.Map.WidthCells; x++)
            {
                var centre = map.CentreOf(x, z);
                var distance = Math.Sqrt(((centre.X - from.X) * (centre.X - from.X)) + ((centre.Z - from.Z) * (centre.Z - from.Z)));
                if (distance >= least && distance <= most && map.Map.Cell(x, z).Water == WaterKind.None && map.Connected(from, centre))
                {
                    return centre;
                }
            }
        }

        throw new InvalidOperationException($"No dry cell between {least} m and {most} m.");
    }

    /// <summary>
    /// A world in the given weather where the first inhabitant is asked (it was clicked): one thing near, one
    /// thing and another inhabitant out of sight in the thick weather but within the usual radius.
    /// </summary>
    private static (DecisionRequest Request, string Near, string Far, string Other) Asked(string weatherId)
    {
        var port = new RecordingPort();
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, port, "de", Preset);
        var sight = PerceptionRadiusM * (Thick.SightPermille ?? Sight.Full) / Sight.Full;
        var asked = host.Inhabitants.All.First();
        var other = host.Inhabitants.All.Last();
        var far = sight + ((PerceptionRadiusM - sight) / 2);
        foreach (var obj in host.Objects.All.ToList())
        {
            host.Objects.Remove(obj.Id);
        }

        var near = host.Objects.Spawn(Water, new Position(asked.Position.X + (sight / 2), asked.Position.Z));
        var distant = host.Objects.Spawn(Water, new Position(asked.Position.X - far, asked.Position.Z));
        other.Position = new Position(asked.Position.X, asked.Position.Z + far);
        host.WeatherSystem!.Force(weatherId, host.World.Tick);
        Assert.True(host.Decisions.Submit(new PlayerCommand(DecisionsSystem.InhabitantClicked, asked.Id)).Accepted);
        for (var tick = 0; tick < 5 && port.Requests.All(r => r.Inhabitant.Id != asked.Id); tick++)
        {
            host.World.Step();
        }

        return (port.Requests.First(r => r.Inhabitant.Id == asked.Id), near.Id, distant.Id, other.Id);
    }

    private static string ClimateFile() =>
        Directory.GetFiles(Path.Combine(ContentDirectory, "climate"), "*.json").Single(f => File.ReadAllText(f).Contains($"\"id\": \"{Climate.Id}\"", StringComparison.Ordinal));

    private string CopyContent()
    {
        var target = Path.Combine(_directory, "content");
        foreach (var file in Directory.GetFiles(ContentDirectory, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(ContentDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        return target;
    }

    /// <summary>A decider that keeps what it was asked and never answers.</summary>
    private sealed class RecordingPort : IDecisionPort
    {
        public List<DecisionRequest> Requests { get; } = [];

        public void Send(string requestId, string requestJson) => Requests.Add(ContractSerializer.Deserialize<DecisionRequest>(requestJson));

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
