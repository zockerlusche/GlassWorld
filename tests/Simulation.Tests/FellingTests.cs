using System.Text.Json.Nodes;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1830 (D1238): felling is a processing rule of the data (fell: a rooted tree and a hard, sharp tool) - the tree
/// gives a trunk and leaves its stump; a standing tree is never split or charred (rooted); it grows back at its
/// place after the data's hours, and then its stump goes.
/// </summary>
public sealed class FellingTests : IDisposable
{
    private const string Actor = "inh-000001";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private readonly string _copy = Path.Combine(Path.GetTempPath(), "glassworld-felling-" + Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_copy))
        {
            Directory.Delete(_copy, recursive: true);
        }
    }

    [Fact]
    public void AHardSharpStoneFellsABirchIntoATrunkAndItsStump()
    {
        var (host, events) = World();
        var birch = host.Objects.Spawn("birch_tree", new Position(1, 0));
        var flint = host.Objects.Spawn("flint", new Position(1.5, 0));

        RunCombine(host, birch, flint);

        Assert.False(host.Objects.TryGet(birch.Id, out _));
        Assert.Single(host.Objects.All, o => o.TypeId == "felled_trunk" && o.Position == birch.Position);
        Assert.Single(host.Objects.All, o => o.TypeId == "tree_stump" && o.Position == birch.Position);
        Assert.True(host.Objects.TryGet(flint.Id, out _), "the tool stays");

        // Felled, not split: a standing tree is never a variant that loses its obstacle and shade (rooted).
        Assert.Contains(events, e => e.Kind == InhabitantsSystem.Combined && e.Detail == "fell_tree");
        Assert.DoesNotContain(events, e => e.Kind == InhabitantsSystem.Combined && e.Detail == "split_wood");
    }

    [Fact]
    public void NoProcessButFellingTakesAStandingTreeAndTheFelledTrunkCanBeSplit()
    {
        var rules = ContractSerializer.Deserialize<ProcessRules>(File.ReadAllText(Path.Combine(ContentDirectory, "processes", "rules.json")));
        var processes = ContractSerializer.Deserialize<Vocabulary>(File.ReadAllText(Path.Combine(ContentDirectory, "vocabulary", "processes.json")));
        foreach (var process in processes.Entries.Select(e => e.Id).Where(p => p != "fell"))
        {
            Assert.True(rules.Physics!.NeverIf.TryGetValue(process, out var never) && never.Contains("rooted"), $"{process} may take a standing tree");
        }

        // The trunk has no root: the data's split rule fits it.
        var trunk = ContentSet.Load(ContentDirectory).Objects.TrueProperties("felled_trunk");
        var split = rules.Rules.Single(r => r.Id == "split_wood").Inputs.First();
        Assert.True(split.PropertiesAll.All(trunk.Contains) && !split.PropertiesNone.Any(trunk.Contains));
        Assert.DoesNotContain("rooted", (IReadOnlySet<string>)trunk);
    }

    [Fact]
    public void AFelledBirchGrowsBackAtItsPlaceAndItsStumpGoes()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentWithQuickBirches(), 7, new MockDecisionPort(), "de", "start_island");
        var map = host.Map!;
        var index = map.Occurrences.ToList().FindIndex(o => o.TypeId == "birch_tree" && o.GoneSinceTick is null);
        Assert.True(index >= 0, "the island has a birch that grows back");
        var place = map.Occurrences[index];
        Assert.True(host.Objects.TryGet(place.ObjectId, out var birch));

        // Felled: the birch goes, its stump stays (as the process leaves it).
        Assert.True(host.Objects.Remove(birch.Id));
        var stump = host.Objects.Spawn("tree_stump", birch.Position);
        host.World.Step();
        Assert.NotNull(map.Occurrences[index].GoneSinceTick);

        for (var i = 0; i < 5000 && map.Occurrences[index].GoneSinceTick is not null; i++)
        {
            host.World.Step();
        }

        var grown = map.Occurrences[index];
        Assert.Null(grown.GoneSinceTick);
        Assert.True(host.Objects.TryGet(grown.ObjectId, out var again));
        Assert.Equal("birch_tree", again.TypeId);
        Assert.False(host.Objects.TryGet(stump.Id, out _), "the stump goes when the tree grows back");
    }

    /// <summary>A copy of the content in which birches grow back after two hours (the data's two years would take long).</summary>
    private string ContentWithQuickBirches()
    {
        foreach (var file in Directory.EnumerateFiles(ContentDirectory, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(_copy, Path.GetRelativePath(ContentDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        var path = Path.Combine(_copy, "world", "spawns.json");
        var spawns = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var rule in spawns["spawns"]!.AsArray().Where(r => (string?)r!["object_type"] == "birch_tree" && r!["regrow_hours"] is not null))
        {
            rule!["regrow_hours"] = 2;
        }

        File.WriteAllText(path, spawns.ToJsonString());
        return _copy;
    }

    private static (SimulationHost Host, List<WorldEvent> Events) World()
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 13, new MockDecisionPort(), "de");
        var model = host.Content.InhabitantModel;
        var needs = model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal);
        var moods = model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal);
        var traits = model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal);
        host.Inhabitants.Add(new Inhabitant(Actor, "female", 1, new Position(0, 0), needs, moods, traits, model.WillToLive.Start));
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        return (host, events);
    }

    private static void RunCombine(SimulationHost host, WorldObject target, WorldObject partner)
    {
        var inhabitant = host.Inhabitants.Get(Actor);
        inhabitant.Activity = new Activity("combine", target.Id, target.Position, null, Partners: [partner.Id]);
        for (var i = 0; i < 2000 && inhabitant.Activity is { Action: "combine" }; i++)
        {
            host.World.Run(1);
        }

        Assert.False(inhabitant.Activity is { Action: "combine" }, "the try did not end");
    }
}
