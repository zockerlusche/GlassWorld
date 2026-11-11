using System.Text.Json.Nodes;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B2582, W1745: the comfort zone of the inhabitant model. The air at the inhabitant's place moves a need
/// (data: comfort_zone): it sinks outside the zone and recovers inside. Only a need effect; every number
/// is read from the data, so the tests follow a change of the proposal values.
/// </summary>
public sealed class ComfortZoneTests
{
    private const string Preset = "start_island";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);
    private static readonly ComfortZone Zone = Content.InhabitantModel.ComfortZone!;
    private static readonly string Need = Zone.Need;

    [Fact]
    public void TheNeedSinksBelowTheZoneByTheDegreesAndRecoversInside()
    {
        var air = new Air(0, false);
        var (world, inhabitants) = NewWorld(Content.InhabitantModel, () => air);
        var inhabitant = inhabitants.Get("inh-000001");
        var shift = inhabitants.ComfortShiftOf(world.Seed, inhabitant.Id);
        Assert.True(Zone.ColdLossPerHourPerDegree > 0, "content: cold costs something");
        Assert.True(Zone.RecoverPerHour > 0, "content: the zone gives something back");

        // Five degrees below its own zone.
        inhabitant.Needs[Need] = 0.8;
        air = new Air(Zone.MinTenthC + shift - 50, false);
        world.Step();
        var cold = 0.8 - PerTick(world, Decay) - PerTick(world, Zone.ColdLossPerHourPerDegree * 5);
        Assert.Equal(cold, inhabitant.Needs[Need], 12);

        // Twice as far below costs twice as much.
        inhabitant.Needs[Need] = 0.8;
        air = new Air(Zone.MinTenthC + shift - 100, false);
        world.Step();
        Assert.Equal(0.8 - PerTick(world, Decay) - PerTick(world, Zone.ColdLossPerHourPerDegree * 10), inhabitant.Needs[Need], 12);

        // The edges belong to the zone: there it recovers, up to full and no further.
        foreach (var inside in new[] { Zone.MinTenthC + shift, Zone.MaxTenthC + shift })
        {
            inhabitant.Needs[Need] = 0.5;
            air = new Air(inside, false);
            world.Step();
            Assert.Equal(0.5 - PerTick(world, Decay) + PerTick(world, Zone.RecoverPerHour), inhabitant.Needs[Need], 12);
        }

        inhabitant.Needs[Need] = 1.0;
        world.Step();
        Assert.True(inhabitant.Needs[Need] <= 1.0);
        Assert.Equal(1.0, inhabitant.Needs[Need], 3);
    }

    [Fact]
    public void HeatAboveTheZoneCostsByItsOwnRate()
    {
        var air = new Air(0, false);
        var (world, inhabitants) = NewWorld(Content.InhabitantModel, () => air);
        var inhabitant = inhabitants.Get("inh-000001");
        var shift = inhabitants.ComfortShiftOf(world.Seed, inhabitant.Id);
        Assert.True(Zone.HeatLossPerHourPerDegree > 0, "content: heat costs something");

        inhabitant.Needs[Need] = 0.8;
        air = new Air(Zone.MaxTenthC + shift + 70, true); // precipitation is a factor of the cold only
        world.Step();
        Assert.Equal(0.8 - PerTick(world, Decay) - PerTick(world, Zone.HeatLossPerHourPerDegree * 7), inhabitant.Needs[Need], 12);
    }

    [Fact]
    public void ARunningActionAndPrecipitationMultiplyTheColdLoss()
    {
        var factor = Assert.Single(Zone.ActionFactors ?? [], f => f.ColdFactor is > 1);
        Assert.True(Zone.PrecipitationColdFactor is > 1, "content: being wet makes the cold worse");

        var air = new Air(0, false);
        var (world, inhabitants) = NewWorld(Content.InhabitantModel, () => air);
        var inhabitant = inhabitants.Get("inh-000001");
        air = new Air(Zone.MinTenthC + inhabitants.ComfortShiftOf(world.Seed, inhabitant.Id) - 100, true);
        var loss = PerTick(world, Zone.ColdLossPerHourPerDegree * 10);

        // Wet, but the action does not count: it has no factor in the data.
        inhabitant.Needs[Need] = 0.8;
        world.Step();
        Assert.Equal(0.8 - PerTick(world, Decay) - (loss * Zone.PrecipitationColdFactor!.Value), inhabitant.Needs[Need], 12);

        // Wet and in the action with a factor, once it runs.
        inhabitant.Activity = new Activity(factor.Action, null, inhabitant.Position, 1_000);
        inhabitant.Needs[Need] = 0.8;
        world.Step();
        Assert.Equal(0.8 - PerTick(world, Decay) - (loss * Zone.PrecipitationColdFactor.Value * factor.ColdFactor!.Value), inhabitant.Needs[Need], 12);
    }

    [Fact]
    public void TheAirAloneNeverPushesTheNeedBelowTheFloor()
    {
        var floor = Zone.Floor ?? 0.0;
        var air = new Air(Zone.MinTenthC - 400, true);
        var (world, inhabitants) = NewWorld(Content.InhabitantModel, () => air);
        var inhabitant = inhabitants.Get("inh-000001");
        inhabitant.Needs[Need] = floor + 0.0001;
        for (var i = 0; i < 200; i++)
        {
            world.Step();
        }

        Assert.Equal(floor, inhabitant.Needs[Need], 12);

        // The proposal keeps the floor above the neglect threshold while nothing in the world warms anyone.
        Assert.True(floor >= Content.InhabitantModel.Needs.Single(n => n.Id == Need).NeglectBelow, "content: the cold alone must not neglect");

        // A need that is already lower (by something else) is left alone, not lifted.
        if (floor > 0)
        {
            inhabitant.Needs[Need] = floor / 2;
            world.Step();
            Assert.Equal((floor / 2) - PerTick(world, Decay), inhabitant.Needs[Need], 12);
        }
    }

    [Fact]
    public void WithoutTheSectionOrWithoutAirNothingMoves()
    {
        // Old behaviour: a model without comfort_zone in freezing air, and the real model in a world without air.
        var model = ContractSerializer.Deserialize<InhabitantModel>(File.ReadAllText(Path.Combine(ContentDirectory, "inhabitants", "model.json")));
        model.ComfortZone = null;
        var noSection = NewWorld(model, () => new Air(-300, true));
        var noAir = NewWorld(Content.InhabitantModel, null);
        var nullAir = NewWorld(Content.InhabitantModel, () => null);
        var start = noSection.Inhabitants.All.ToDictionary(i => i.Id, i => i.Needs[Need], StringComparer.Ordinal);
        foreach (var (world, _) in new[] { noSection, noAir, nullAir })
        {
            world.Run(500);
        }

        foreach (var (world, inhabitants) in new[] { noSection, noAir, nullAir })
        {
            Assert.All(inhabitants.All, i => Assert.Equal(Math.Max(0.0, start[i.Id] - (500 * PerTick(world, Decay))), i.Needs[Need], 9));
        }

        Assert.Equal(noSection.World.StateHash(), noAir.World.StateHash());
        Assert.Equal(noSection.World.StateHash(), nullAir.World.StateHash());
        Assert.Equal(0, noSection.Inhabitants.ComfortShiftOf(noSection.World.Seed, "inh-000001"));
    }

    [Fact]
    public void EveryInhabitantHasItsOwnZoneFromTheSeed()
    {
        var spread = Zone.IndividualShiftTenthC ?? 0;
        Assert.True(spread > 0, "content: the zones differ (B2582)");
        var shifts = new List<int>();
        for (ulong seed = 1; seed <= 8; seed++)
        {
            var a = NewWorld(Content.InhabitantModel, null).Inhabitants;
            var b = NewWorld(Content.InhabitantModel, null).Inhabitants;
            foreach (var inhabitant in a.All)
            {
                var shift = a.ComfortShiftOf(seed, inhabitant.Id);
                Assert.InRange(shift, -spread, spread);
                Assert.Equal(shift, b.ComfortShiftOf(seed, inhabitant.Id)); // same seed and id, same zone
                Assert.Equal(shift, a.ComfortShiftOf(seed, inhabitant.Id));
                shifts.Add(shift);
            }
        }

        Assert.True(shifts.Distinct().Count() > 4, "the zones of the inhabitants differ");
    }

    [Fact]
    public void AZoneTheWrongWayRoundOrWithUnknownIdsIsRefusedAtLoad()
    {
        var problems = new Action<JsonNode>[]
        {
            zone => zone["need"] = "nonsense",
            zone => zone["min_tenth_c"] = Zone.MaxTenthC + 1,
            zone => zone["action_factors"] = new JsonArray(new JsonObject { ["action"] = "nonsense" }),
            zone => zone["action_factors"] = new JsonArray(new JsonObject { ["action"] = "idle" }, new JsonObject { ["action"] = "idle" }),
        };
        var directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());
        try
        {
            foreach (var entry in Directory.GetFiles(ContentDirectory, "*.json", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(directory, Path.GetRelativePath(ContentDirectory, entry));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(entry, copy);
            }

            var path = Path.Combine(directory, "inhabitants", "model.json");
            var original = File.ReadAllText(path);
            Assert.NotNull(ContentSet.Load(directory).InhabitantModel.ComfortZone);
            foreach (var problem in problems)
            {
                var model = JsonNode.Parse(original)!;
                problem(model["comfort_zone"]!);
                File.WriteAllText(path, model.ToJsonString());
                var error = Assert.Throws<InvalidDataException>(() => ContentSet.Load(directory));
                Assert.Contains("comfort_zone", error.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void TheWorldWithoutAClimateHasNoAir()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 7, new MockDecisionPort(), "de");
        Assert.All(host.Inhabitants.All, i => Assert.Null(host.Inhabitants.AirAt!(host.World, i.Position)));
        var start = host.Inhabitants.All.ToDictionary(i => i.Id, i => i.Needs[Need], StringComparer.Ordinal);
        host.World.Run(300);
        Assert.All(host.Inhabitants.All, i => Assert.Equal(Math.Max(0.0, start[i.Id] - (300 * PerTick(host.World, Decay))), i.Needs[Need], 9));
    }

    [Fact]
    public void OnTheIslandTheAirIsTheWeatherAtTheHeightOfTheCell()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        var preset = host.Content.WorldGen.Presets[Preset];
        var (map, weather) = (host.Map!, host.WeatherSystem!);
        var starts = map.Island.Starts;
        var startHeight = starts.Sum(s => Math.Max(0, map.Map.Cell(s.X, s.Z).Height - preset.Terrain.SeaLevel)) / starts.Count;
        int Drop(int height) => -(preset.Climate.TemperatureDropPer100Height * Math.Max(0, height - preset.Terrain.SeaLevel) / 100);

        // The weather's own temperature stands for the mean height of the start cells; every cell has its own.
        var seen = new HashSet<int>();
        for (var z = 0; z < map.Map.DepthCells; z += 7)
        {
            for (var x = 0; x < map.Map.WidthCells; x += 7)
            {
                var cell = map.Map.Cell(x, z);
                var air = host.Inhabitants.AirAt!(host.World, map.CentreOf(x, z))!.Value;
                Assert.Equal(weather.Read(host.World).TemperatureTenthC - Drop(startHeight + preset.Terrain.SeaLevel) + Drop(cell.Height), air.TemperatureTenthC);
                seen.Add(air.TemperatureTenthC);
            }
        }

        Assert.True(seen.Count > 1, "higher ground is colder");

        // Wet or dry follows the weather.
        var at = host.Inhabitants.All.First().Position;
        var wet = host.Content.WorldGen.Climates[preset.Climate.ClimateId].Weather.States.First(s => s.PrecipitationPermille > 0).Weather;
        var dry = host.Content.WorldGen.Climates[preset.Climate.ClimateId].Weather.States.First(s => s.PrecipitationPermille == 0).Weather;
        weather.Force(wet, host.World.Tick);
        Assert.True(host.Inhabitants.AirAt!(host.World, at)!.Value.Precipitation);
        weather.Force(dry, host.World.Tick);
        Assert.False(host.Inhabitants.AirAt!(host.World, at)!.Value.Precipitation);
    }

    [Fact]
    public void ColdWeatherOnTheIslandLowersTheNeedAndSaveAndLoadGoOnIdentically()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        var twin = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", Preset);
        var coldest = host.Content.WorldGen.Climates[host.Content.WorldGen.Presets[Preset].Climate.ClimateId].Weather.Temperature.Weathers.MinBy(w => w.OffsetTenthC)!.Weather;
        var start = host.Inhabitants.All.ToDictionary(i => i.Id, i => i.Needs[Need], StringComparer.Ordinal);
        foreach (var world in new[] { host, twin })
        {
            world.WeatherSystem!.Force(coldest, world.World.Tick);
            world.World.Run(120);
        }

        Assert.All(host.Inhabitants.All, i =>
        {
            var air = host.Inhabitants.AirAt!(host.World, i.Position)!.Value;
            Assert.True(air.TemperatureTenthC < Zone.MinTenthC + host.Inhabitants.ComfortShiftOf(host.World.Seed, i.Id), "the coldest weather is below everybody's zone");
            Assert.True(i.Needs[Need] < start[i.Id], $"{i.Id} got colder");
        });

        // Same seed, same run: same state (the zone's own shift is drawn from the seed, not from a world stream).
        Assert.Equal(host.World.StateHash(), twin.World.StateHash());

        // Nothing of the comfort zone is saved: a loaded world goes on exactly like the one that kept running.
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        var loaded = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new MockDecisionPort(), "de"), StateCodec.Capture(host));
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        host.World.Run(240);
        loaded.World.Run(240);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        Assert.All(host.Inhabitants.All, i => Assert.Equal(i.Needs[Need], loaded.Inhabitants.Get(i.Id).Needs[Need]));
    }

    private static double Decay => Content.InhabitantModel.Needs.Single(n => n.Id == Need).DecayPerHour;

    private static double PerTick(World world, double perHour) => perHour / world.Calendar.Settings.TicksPerHour;

    /// <summary>Four inhabitants far apart, everyone idling (an action without a comfort factor), in the air given.</summary>
    private static (World World, InhabitantsSystem Inhabitants) NewWorld(InhabitantModel model, Func<Air?>? air)
    {
        var world = new World(3, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(model, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Content.Knowledge,
            AirAt = air is null ? null : (_, _) => air(),
        };
        world.Add(objects).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [new(0, 0), new(100, 0), new(0, 100), new(100, 100)]);
        foreach (var inhabitant in inhabitants.All)
        {
            inhabitant.Activity = new Activity("idle", null, inhabitant.Position, 100_000);
        }

        return (world, inhabitants);
    }
}
