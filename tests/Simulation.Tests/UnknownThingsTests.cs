using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1551: the unknown_object trigger asks about the nearest unknown thing the inhabitant may ask about now - a
/// nearer thing it is already asking about, or cooling down on, does not hide a second unknown thing.
/// </summary>
public sealed class UnknownThingsTests
{
    private const string Near = "flint";
    private const string Far = "pyrite";

    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);

    [Fact]
    public void ANearerThingAlreadyAskedAboutDoesNotHideASecondUnknownThing()
    {
        var w = NewWorld();
        w.Objects.Spawn(Near, new Position(0, 3));
        w.Objects.Spawn(Far, new Position(0, 8));

        // First the nearer thing; then, with room for a second question, the farther one.
        w.World.Step();
        Assert.Equal(["type:" + Near], w.Subjects());
        w.World.Step();
        Assert.Equal(["type:" + Near, "type:" + Far], w.Subjects());
    }

    [Fact]
    public void ANearerThingInItsCooldownDoesNotHideASecondUnknownThing()
    {
        var w = NewWorld();
        var near = w.Objects.Spawn(Near, new Position(0, 3));
        w.World.Step();
        Assert.Equal(["type:" + Near], w.Subjects());

        // The question about the nearer thing closes without the inhabitant learning anything; its cooldown runs.
        Assert.True(w.Decisions.Settle(w.World, w.Decisions.Open.Single().Id, "test"));
        Assert.Empty(w.Decisions.Open);
        w.Objects.Spawn(Far, new Position(0, 8));
        w.World.Step();
        Assert.Equal(["type:" + Far], w.Subjects());
        Assert.True(w.Objects.TryGet(near.Id, out _));
    }

    [Fact]
    public void WithOneUnknownThingNothingChanges()
    {
        var w = NewWorld();
        w.Objects.Spawn(Near, new Position(0, 3));
        w.World.Run(5);
        Assert.Equal(["type:" + Near], w.Subjects());
    }

    private static TestWorld NewWorld()
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));

        // Only things are asked about here, and nothing is held back: the second question goes out as well.
        settings.ChoiceProtectionHours = null;
        foreach (var trigger in settings.Triggers.Where(t => t.Kind != TriggerKind.Unknown_object))
        {
            trigger.Enabled = false;
        }

        var world = new World(1, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour);
        var decisions = new DecisionsSystem(settings, inhabitants, objects, Content.Actions, Content.Brain, Content.Vocabularies[VocabularyKind.Action], new SilentPort(), Content.Inventions.Guard, "de");
        inhabitants.QuestionOpen = decisions.IsAskingAbout;
        world.Add(objects).Add(decisions).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [new Position(0, 0), new Position(5000, 5000), new Position(10000, 0), new Position(0, 10000)]);
        return new TestWorld(world, objects, decisions);
    }

    private sealed record TestWorld(World World, WorldObjects Objects, DecisionsSystem Decisions)
    {
        /// <summary>The subjects of the open questions of inh-000001, in order.</summary>
        public List<string> Subjects() =>
            [.. Decisions.Open.Where(q => q.InhabitantId == "inh-000001").Select(q => q.Subject).Order(StringComparer.Ordinal)];
    }

    /// <summary>A decider that never answers.</summary>
    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
