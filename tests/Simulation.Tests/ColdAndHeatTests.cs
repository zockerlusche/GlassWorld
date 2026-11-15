using System.Text.Json.Nodes;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1379 rest (spec Bogen 30 R4-R6, TL #25685): wind cools and wind shelter dims it, others near warm, and cold
/// changes how fast other needs fall - all by the data, no counter-measure in the code.
/// </summary>
public sealed class ColdAndHeatTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private readonly List<string> _copies = [];

    [Fact]
    public void WindCoolsAndAWindShelterDimsIt()
    {
        var host = Island(ContentDirectory);
        host.WeatherSystem!.Force("storm", host.World.Tick);
        host.World.Run(30);
        var at = new Position(host.Inhabitants.All.First().Position.X + 40, host.Inhabitants.All.First().Position.Z);
        var near = new List<WorldObject>();
        host.Objects.Within(at, 5, near);
        foreach (var thing in near)
        {
            host.Objects.Remove(thing.Id);
        }

        var open = host.Body!.WindTenthC(host.World, at);
        Assert.True(open > 0, "a storm's wind is felt");
        host.Objects.Spawn("rock_niche", new Position(at.X + 1, at.Z));
        var sheltered = host.Body.WindTenthC(host.World, at);
        Assert.True(sheltered < open, $"{sheltered} >= {open}");
    }

    [Fact]
    public void OthersNearWarmByTheirDistance()
    {
        var host = Island(ContentDirectory);
        var others = Content.Value.Body!.OthersWarmth!;
        var radius = others.RadiusCm / 100.0;
        foreach (var inhabitant in host.Inhabitants.All)
        {
            var expected = host.Inhabitants.All.Where(o => o.Id != inhabitant.Id)
                .Select(o => Math.Sqrt(Math.Pow(o.Position.X - inhabitant.Position.X, 2) + Math.Pow(o.Position.Z - inhabitant.Position.Z, 2)))
                .Where(d => d < radius).Sum(d => others.TenthC * (1.0 - (d / radius)));
            Assert.Equal((int)Math.Round(expected, MidpointRounding.AwayFromZero), host.Body!.OthersWarmthTenthC(inhabitant));
        }
    }

    [Fact]
    public void InTheColdHungerFallsFasterThanWithoutTheModifier()
    {
        // Always too cold (the zone far above any air), once with the data's need modifiers and once without.
        var withModifiers = Island(Copy(zone => { }));
        var without = Island(Copy(zone => zone.Remove("need_modifiers")));
        Assert.Equal("cold", withModifiers.Inhabitants.ComfortLevelOf(withModifiers.World, withModifiers.Inhabitants.All.First()));

        var hours = withModifiers.World.Calendar.Settings.TicksPerHour * 2;
        withModifiers.World.Run(hours);
        without.World.Run(hours);
        var id = withModifiers.Inhabitants.All.First().Id;
        Assert.True(withModifiers.Inhabitants.Get(id).Needs["hunger"] < without.Inhabitants.Get(id).Needs["hunger"]);
    }

    [Fact]
    public void OnAWinterNightWithoutFireTheWarmthNeedFallsAndAFireOrAShelterSlowsIt()
    {
        // TL #25829/#25856, B2771 (D1112 #5): a winter night without fire costs at least 0.3 of the warmth need; with
        // a fire kept burning next to the inhabitant much less, with a rock niche keeping the wind off no more.
        var content = WinterFirstContent();
        var need = Content.Value.InhabitantModel.ComfortZone!.Need;

        var (open, openInhabitant) = WinterNight(content);
        var level = open.Inhabitants.ComfortLevelOf(open.World, openInhabitant);
        Assert.True(level is "cold" or "cool", $"comfort level {level} on a winter night");
        var openDrop = Drop(open, openInhabitant, need, _ => { });

        var (fire, fireInhabitant) = WinterNight(content);
        var fireDrop = Drop(fire, fireInhabitant, need, h =>
        {
            // A fire kept burning next to the inhabitant (as someone tending it would): embers get a fresh fire.
            if (!h.Objects.All.Any(o => o.TypeId == "fire" && !h.Fire.IsEmbers(o.Id)))
            {
                h.Objects.Spawn("fire", new Position(fireInhabitant.Position.X + 0.5, fireInhabitant.Position.Z));
            }
        });

        var (niche, nicheInhabitant) = WinterNight(content);
        var nicheDrop = Drop(niche, nicheInhabitant, need, h => h.Objects.Spawn("rock_niche", new Position(nicheInhabitant.Position.X + 1, nicheInhabitant.Position.Z)));

        Assert.True(openDrop >= 0.3, $"{need} fell only {openDrop} on a winter night (B2771: at least 0.3)");
        Assert.True(fireDrop <= openDrop / 2, $"with a fire {fireDrop} > half of {openDrop}");
        Assert.True(nicheDrop <= openDrop, $"in a niche {nicheDrop} > {openDrop}");
    }

    /// <summary>How much a full need falls over eight hours of the night, <paramref name="everyHour"/> done each hour.</summary>
    private static double Drop(SimulationHost host, Inhabitant inhabitant, string need, Action<SimulationHost> everyHour)
    {
        // Full at nightfall: the cold day before has already lowered it (the measure is the night).
        inhabitant.Needs[need] = 1.0;
        for (var hour = 0; hour < 8; hour++)
        {
            everyHour(host);
            host.World.Run(host.World.Calendar.Settings.TicksPerHour);
        }

        return 1.0 - inhabitant.Needs[need];
    }

    /// <summary>The second winter night at 22 o'clock (past the climate's one-day season transition), nobody lighting a fire.</summary>
    private static (SimulationHost Host, Inhabitant Inhabitant) WinterNight(string content)
    {
        var host = Island(content);
        Assert.Equal("winter", host.World.Now.SeasonId);
        host.World.Run(host.World.Calendar.TicksPerDay);
        while (host.World.Now.Hour != 22)
        {
            host.World.Run(1);
        }

        Assert.DoesNotContain(host.Objects.All, o => o.TypeId == "fire");

        // A dry night (rain would put the fire out and wet the inhabitant: another proof).
        host.WeatherSystem!.Force("clear", host.World.Tick);
        return (host, host.Inhabitants.All.First());
    }

    /// <summary>A copy of the content whose year begins in winter (calendar and climate lists).</summary>
    private string WinterFirstContent() => CopyContent(target =>
    {
        var timePath = Path.Combine(target, "world", "time.json");
        var time = JsonNode.Parse(File.ReadAllText(timePath))!.AsObject();
        var seasons = time["seasons"]!.AsArray();
        var winter = seasons.First(s => (string?)s!["id"] == "winter")!;
        seasons.Remove(winter);
        seasons.Insert(0, winter);
        File.WriteAllText(timePath, time.ToJsonString());

        // The climate lists its seasons in calendar order too: winter first there as well.
        foreach (var climatePath in Directory.EnumerateFiles(Path.Combine(target, "climate"), "*.json"))
        {
            var climate = JsonNode.Parse(File.ReadAllText(climatePath))!;
            WinterFirst(climate);
            File.WriteAllText(climatePath, climate.ToJsonString());
        }
    });

    /// <summary>Every list of the four seasons (items with a 'season') starts with winter, the rest in their order.</summary>
    private static void WinterFirst(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            var winter = array.FirstOrDefault(i => i is JsonObject o && (string?)o["season"] == "winter");
            if (winter is not null && array.Count == 4 && array.All(i => i is JsonObject o && o["season"] is not null))
            {
                array.Remove(winter);
                array.Insert(0, winter);
            }

            foreach (var item in array.ToList())
            {
                WinterFirst(item);
            }
        }
        else if (node is JsonObject obj)
        {
            foreach (var (_, value) in obj.ToList())
            {
                WinterFirst(value);
            }
        }
    }

    /// <summary>A copy of the content, changed by <paramref name="change"/> (the copy's directory).</summary>
    private string CopyContent(Action<string> change)
    {
        var target = Path.Combine(Path.GetTempPath(), "gw-cold-" + Guid.NewGuid().ToString("N"));
        _copies.Add(target);
        foreach (var file in Directory.EnumerateFiles(ContentDirectory, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(ContentDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        change(target);
        return target;
    }

    /// <summary>A copy of the content in which the comfort zone lies far above any air (always too cold), changed further by <paramref name="change"/>.</summary>
    private string Copy(Action<JsonObject> change) => CopyContent(target =>
    {
        var modelPath = Path.Combine(target, "inhabitants", "model.json");
        var model = JsonNode.Parse(File.ReadAllText(modelPath))!.AsObject();
        var zone = model["comfort_zone"]!.AsObject();
        zone["min_tenth_c"] = 590;
        zone["max_tenth_c"] = 600;
        change(zone);
        File.WriteAllText(modelPath, model.ToJsonString());
    });

    public void Dispose()
    {
        foreach (var copy in _copies)
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    private static SimulationHost Island(string content)
    {
        var host = SimulationHost.CreateGeneratedWorld(content, 23, new SilentPort(), "de", "start_island");
        host.World.Run(1);
        return host;
    }

    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
