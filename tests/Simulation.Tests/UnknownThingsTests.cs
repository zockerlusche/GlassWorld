using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1551: a nearer unknown thing the inhabitant is cooling down on does not hide a second unknown thing; one it is
/// asking about right now keeps its place, so questions about things leave room for questions about strangers.
/// </summary>
public sealed class UnknownThingsTests
{
    private const string Near = "flint";
    private const string Far = "pyrite";

    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);

    [Fact]
    public void ANearerThingAskedAboutRightNowKeepsItsPlace()
    {
        var w = NewWorld();
        w.Objects.Spawn(Near, new Position(0, 3));
        w.Objects.Spawn(Far, new Position(0, 8));

        // She stands still (walking on, the farther thing could become the nearer one).
        var a = w.Inhabitants.Get("inh-000001");
        a.Activity = new Activity("idle", null, a.Position, 100_000);

        // The nearer thing is asked about; while that question is out nothing more is asked about things,
        // although there is room for a second question (it stays for a stranger).
        w.World.Step();
        Assert.Equal(["type:" + Near], w.Subjects());
        w.World.Run(5);
        Assert.Equal(["type:" + Near], w.Subjects());
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
        return new TestWorld(world, objects, inhabitants, decisions);
    }

    private sealed record TestWorld(World World, WorldObjects Objects, InhabitantsSystem Inhabitants, DecisionsSystem Decisions)
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
