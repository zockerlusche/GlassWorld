using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Night;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>A1581 (Marcel W2243, D1212 c): material handled by day lets an idea sprout in the inhabitant's own ideas.</summary>
public sealed class MaterialIdeasTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");

    [Fact]
    public void AnIdeaSproutsOnlyFromKnownMaterialAndNeverTwiceForTheSameMaterial()
    {
        // In a living world: every idea that sprouts by day is rooted on one material its inhabitant knows something about
        // at that moment, at the data's start strength, and no inhabitant gets two such ideas on the same material.
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 1, new MockDecisionPort(), "de");
        var data = host.Content.Knowledge.MaterialIdeas!;
        var who = host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).First();
        who.Traits["curiosity"] = 0.9;
        who.Traits["drive"] = 0.9;
        var material = host.Content.Objects.TypeIds.First(t => data.Properties.Any(p => host.Objects.Catalog.HasProperty(t, p)));
        var sprouted = new List<(string Who, string Root)>();
        host.Inhabitants.Reported += e =>
        {
            if (e.Kind != NightJobsSystem.IdeaEvent || e.Detail != MaterialIdeasSystem.Seen)
            {
                return;
            }

            Assert.True(host.Inhabitants.TryGet(e.InhabitantId, out var inhabitant));
            var idea = inhabitant!.Ideas.Ideas.Single(i => i.Id == e.TargetId);
            var root = Assert.Single(idea.Roots);
            Assert.Equal(IdeaRootKinds.Type, root.Kind);
            Assert.True(inhabitant.Beliefs.About(root.Id).Any(), $"{e.InhabitantId} knows nothing about {root.Id}");
            Assert.True(data.Properties.Any(p => host.Objects.Catalog.HasProperty(root.Id, p)), $"{root.Id} is no material");
            Assert.Equal(host.Content.Knowledge.Review!.IdeaStart, idea.Strength, 6);
            sprouted.Add((e.InhabitantId, root.Id));
        };

        who.Beliefs.Set(material, data.Properties.First(), true, 0.8, KnowledgeSources.Experienced);
        Handle(host, who, data.Actions.First(), host.Objects.Spawn(material, who.Position), days: 4);

        // Ideas did sprout (from what they handle themselves and what we handed one of them), each material once.
        Assert.NotEmpty(sprouted);
        Assert.Equal(sprouted.Count, sprouted.Distinct().Count());
    }

    [Fact]
    public void AThingUsedUpByTheActionStillLetsTheIdeaSprout()
    {
        // Review GW2 #571: combining uses the things up - the idea needs only their type, taken when the action ends.
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 2, new MockDecisionPort(), "de");
        var data = host.Content.Knowledge.MaterialIdeas!;
        var who = host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).First();
        who.Traits["curiosity"] = 0.9;
        who.Traits["drive"] = 0.9;
        var material = host.Content.Objects.TypeIds.First(t => data.Properties.Any(p => host.Objects.Catalog.HasProperty(t, p)));
        who.Beliefs.Set(material, data.Properties.First(), true, 0.8, KnowledgeSources.Experienced);
        var perHour = host.World.Calendar.Settings.TicksPerHour;
        for (var hour = 0; hour < 10 * host.World.Calendar.Settings.HoursPerDay && !Rooted(who, material); hour++)
        {
            var thing = host.Objects.Spawn(material, who.Position);
            host.Inhabitants.Report(new WorldEvent(InhabitantsSystem.ActionFinished, host.World.Tick, who.Id, data.Actions.Last(), thing.Id) { TargetType = material });
            host.Objects.Remove(thing.Id); // used up in the same step, before this system looks
            host.World.Run(perHour);
        }

        Assert.True(Rooted(who, material), "an idea on the used-up material");
    }

    private static bool Rooted(Inhabitant who, string material) =>
        who.Ideas.Ideas.Any(i => i.Roots.Any(r => r.Kind == IdeaRootKinds.Type && r.Id == material));

    [Fact]
    public void TheSameSeedSproutsTheSameIdeasOnTheSameDay()
    {
        long? Sprouted()
        {
            var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), "de");
            var data = host.Content.Knowledge.MaterialIdeas!;
            var who = host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).First();
            var material = host.Content.Objects.TypeIds.First(t => data.Properties.Any(p => host.Objects.Catalog.HasProperty(t, p)));
            who.Beliefs.Set(material, data.Properties.First(), true, 0.8, KnowledgeSources.Experienced);
            Handle(host, who, data.Actions.First(), host.Objects.Spawn(material, who.Position), days: 4);
            return who.Ideas.Ideas.Count > 0 ? who.Ideas.Ideas[0].Tick : null;
        }

        Assert.Equal(Sprouted(), Sprouted());
    }

    /// <summary>The inhabitant handles the thing once every game hour for the given days (finished actions, as the routine would).</summary>
    private static void Handle(SimulationHost host, Inhabitant who, string action, WorldObject thing, int days)
    {
        var perHour = host.World.Calendar.Settings.TicksPerHour;
        for (var hour = 0; hour < days * host.World.Calendar.Settings.HoursPerDay; hour++)
        {
            host.Inhabitants.Report(new WorldEvent(InhabitantsSystem.ActionFinished, host.World.Tick, who.Id, action, thing.Id));
            host.World.Run(perHour);
        }
    }
}
