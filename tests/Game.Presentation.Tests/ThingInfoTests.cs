using System.Text.Json;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// A1791 (Marcel W2242, D1192 #5/#24): a click on a thing says what it is, who made it, what the inhabitants believe
/// about it and where from, and the ground it stands on (TL #27543) - read from the core, never written.
/// </summary>
public sealed class ThingInfoTests
{
    private static readonly string Content = Path.Combine(TestPaths.Root, "content");

    private static SimulationHost Island() =>
        SimulationHost.CreateGeneratedWorld(Content, 7, new MockDecisionPort(), "de", "start_island");

    [Fact]
    public void TheNearestThingWithinTheRadiusIsMeant()
    {
        WorldObject[] things = [new("obj-2", "stone", new Position(10.6, 5)), new("obj-1", "apple", new Position(10, 5.4)), new("obj-3", "stone", new Position(30, 5))];
        Assert.Equal("obj-1", ThingInfo.Nearest(things, 10, 5, 1.0));
        Assert.Null(ThingInfo.Nearest(things, 20, 5, 1.0)); // nothing near the click
        Assert.Equal("obj-1", ThingInfo.Nearest([new("obj-2", "stone", new Position(10, 6)), new("obj-1", "apple", new Position(10, 4))], 10, 5, 2.0)); // a tie: by id
    }

    [Fact]
    public void ItSaysWhatTheInhabitantsBelieveAndTheGround()
    {
        var host = Island();
        var thing = host.Objects.All.First();
        var knower = host.Inhabitants.All.First();
        knower.Beliefs.Set(thing.TypeId, "edible", false, 0.3, KnowledgeSources.Guessed);

        var info = ThingInfo.Of(host, thing.Id);
        Assert.NotNull(info);
        Assert.Equal(thing.TypeId, info.TypeId);
        var mine = Assert.Single(info.Knowers, k => k.InhabitantId == knower.Id);
        Assert.Contains(new ThingFact("edible", false, KnowledgeSources.Guessed), mine.Facts);
        Assert.All(info.Knowers, k => Assert.True(host.Inhabitants.All.Single(i => i.Id == k.InhabitantId).Beliefs.Knows(thing.TypeId)));

        // The ground under it, from the core's map: a known biome and a speed between a crawl and full speed.
        var ground = Assert.IsType<GroundInfo>(info.Ground);
        Assert.False(string.IsNullOrEmpty(ground.Biome));
        Assert.InRange(ground.SpeedPercent, 1, 100);
        Assert.Equal(ground.SpeedPercent < 100, ground.Slower);
        Assert.Null(ThingInfo.Of(host, "obj-does-not-exist"));
    }

    [Fact]
    public void EveryTextOfTheThingWindowIsThereInBothLanguages()
    {
        // The sources of a belief (KnowledgeSources) and the biomes are shown by key: each needs its words.
        string[] keys =
        [
            "ui.thing.title", "ui.thing.built_by", "ui.thing.invented_by", "ui.thing.known", "ui.thing.nobody_knows", "ui.thing.knows",
            "ui.thing.fact", "ui.thing.fact_not", "ui.thing.ground", "ui.thing.ground_slow",
            .. new[] { KnowledgeSources.Experienced, KnowledgeSources.Seen, KnowledgeSources.Heard, KnowledgeSources.Read, KnowledgeSources.Guessed }.Select(s => "ui.thing.source." + s),
        ];
        foreach (var locale in new[] { "de", "en" })
        {
            using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(Content, "i18n", locale + ".json")));
            var present = catalog.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToHashSet(StringComparer.Ordinal);
            Assert.All(keys, k => Assert.Contains(k, present));
        }
    }
}
