using System.Text.Json.Nodes;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
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

    /// <summary>A copy of the content in which the comfort zone lies far above any air (always too cold), changed further by <paramref name="change"/>.</summary>
    private string Copy(Action<JsonObject> change)
    {
        var target = Path.Combine(Path.GetTempPath(), "gw-cold-" + Guid.NewGuid().ToString("N"));
        _copies.Add(target);
        foreach (var file in Directory.EnumerateFiles(ContentDirectory, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(ContentDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        var modelPath = Path.Combine(target, "inhabitants", "model.json");
        var model = JsonNode.Parse(File.ReadAllText(modelPath))!.AsObject();
        var zone = model["comfort_zone"]!.AsObject();
        zone["min_tenth_c"] = 590;
        zone["max_tenth_c"] = 600;
        change(zone);
        File.WriteAllText(modelPath, model.ToJsonString());
        return target;
    }

    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
