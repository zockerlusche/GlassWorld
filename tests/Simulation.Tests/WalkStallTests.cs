using System.Text.Json;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1751 (D1159): a walk that comes no closer to its destination for the data's stall_minutes is given up
/// (action_cancelled, detail blocked) - before, four walkers jammed on a narrow way waited seven days. A walk that
/// gets on keeps nothing of it (its hash stays as before).
/// </summary>
public sealed class WalkStallTests
{
    private static readonly ContentSet Content = ContentSet.Load(Path.Combine(TestData.Root, "content"));

    [Fact]
    public void AWalkThatGetsNoCloserIsGivenUpAfterTheDataMinutes()
    {
        var space = Content.Gaits.Data.Spacing!;
        Assert.True(space.StallMinutes > 0, "content: a stuck walk is given up");

        // Test data, not the game's: a crawl of 0.1 m a tick against a gain of 5 m - within the data's minutes it
        // never gets on far enough (as in the jam, where keeping apart left only a jostle).
        var data = JsonSerializer.Deserialize<GaitSettings>(JsonSerializer.Serialize(Content.Gaits.Data))!;
        data.WalkMPerTick = 0.1;
        data.Spacing!.StallGainM = 5.0;
        var gaits = GaitTable.Create(data, Content.Vocabularies[VocabularyKind.Action].Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal), Content.Brain.Weights, Content.InhabitantModel);
        var (world, inhabitants, events) = NewWorld(gaits);
        var walker = inhabitants.Get("inh-000001");
        walker.Activity = new Activity("wander", null, new Position(walker.Position.X + 100, walker.Position.Z), null);

        var limit = (long)space.StallMinutes!.Value * world.Calendar.Settings.TicksPerHour / 60;
        var ticks = 0L;
        while (walker.Activity is { Action: "wander" } && ticks < limit + 10)
        {
            world.Step();
            ticks++;
        }

        var blocked = Assert.Single(events, e => e.Kind == InhabitantsSystem.ActionCancelled && e.Detail == InhabitantsSystem.Blocked);
        Assert.Equal("inh-000001", blocked.InhabitantId);
        Assert.InRange(ticks, limit, limit + 2);
    }

    [Fact]
    public void AWalkThatGetsOnKeepsNoStallMark()
    {
        var (world, inhabitants, events) = NewWorld();
        var walker = inhabitants.Get("inh-000001");
        walker.Activity = new Activity("wander", null, new Position(walker.Position.X + 30, walker.Position.Z), null);
        for (var i = 0; i < 10 && walker.Activity is { RemainingTicks: null }; i++)
        {
            world.Step();
            Assert.True(walker.Activity is null or { StallSinceTick: null }, "getting on leaves no mark");
        }

        Assert.DoesNotContain(events, e => e.Detail == InhabitantsSystem.Blocked);
    }

    private static (World World, InhabitantsSystem Inhabitants, List<WorldEvent> Events) NewWorld(GaitTable? gaits = null)
    {
        var world = new World(3, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, gaits ?? Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Content.Knowledge,
        };
        world.Add(objects).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [new(0, 0), new(500, 0), new(0, 500), new(500, 500)]);
        foreach (var other in inhabitants.All.Where(i => i.Id != "inh-000001"))
        {
            other.Activity = new Activity("idle", null, other.Position, 100_000);
        }

        var events = new List<WorldEvent>();
        inhabitants.Reported += events.Add;
        return (world, inhabitants, events);
    }
}
