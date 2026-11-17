using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1816 step 2 (B5 R23, A1381): complaints behind the switch body.stores.enabled - how they begin (event, low need,
/// worsening, contagion), run their course, cost health and heal faster at rest and near a healing thing; felt, never the cause.
/// </summary>
public sealed class ConditionsTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");

    [Fact]
    public void EatingSomethingPoisonousGivesABellyAcheThatCostsHealthAndEndsAfterItsCourse()
    {
        var (world, objects, inhabitants, stores, conditions, content) = Bare(c => Rule(c, "belly_ache").OnEvents!.First().ChancePermille = 1000);
        var eater = inhabitants.Get("inh-000001");
        var poison = content.Objects.TypeIds.First(t => content.Objects.HasProperty(t, "poisonous"));
        var thing = objects.Spawn(poison, eater.Position);
        eater.Activity = new Activity("idle", null, eater.Position, 1_000_000);

        inhabitants.Report(new WorldEvent(InhabitantsSystem.ActionFinished, world.Tick, eater.Id, "eat", thing.Id) { TargetType = poison });
        Assert.Equal(["belly_ache"], conditions.Of(eater.Id));
        Assert.Empty(conditions.Of("inh-000002"));
        var felt = Assert.Single(conditions.Felt(eater.Id));
        Assert.Equal("belly_ache", felt.Condition);
        Assert.Null(felt.BodyPart);

        // Resting heals at 1.5 x the pace: the 12 hours run out after 8, and health was lost on the way.
        var rule = Rule(content, "belly_ache");
        world.Run((rule.Hours * world.Calendar.Settings.TicksPerHour * 1000 / (1000 + rule.Heal!.RestPermille)) + 2);
        Assert.Empty(conditions.Of(eater.Id));
        Assert.True(stores.Health(eater.Id) < 1, $"{stores.Health(eater.Id):0.000}");
    }

    [Fact]
    public void AnInhabitantWhoStaysColdCatchesAColdThatPassesToSomeoneNearAndMayBecomeAFever()
    {
        var (world, _, inhabitants, _, conditions, content) = Bare(c =>
        {
            Rule(c, "cold").OnNeed!.First().ChancePerHourPermille = 1000;
            Rule(c, "cold").Contagion!.ChancePerHourPermille = 1000;
            Rule(c, "fever").From!.ChancePerHourPermille = 1000;
        });
        var cold = inhabitants.Get("inh-000001");
        var near = inhabitants.Get("inh-000002");
        var far = inhabitants.Get("inh-000003");
        var need = Rule(content, "cold").OnNeed!.First();
        cold.Needs[need.Need] = need.Below / 2;

        world.Run(world.Calendar.Settings.TicksPerHour + 1);
        Assert.Contains("cold", conditions.Of(cold.Id));

        world.Run(world.Calendar.Settings.TicksPerHour);
        Assert.Contains("cold", conditions.Of(near.Id));
        Assert.Empty(conditions.Of(far.Id));

        world.Run((Rule(content, "fever").From!.AfterHours + 1) * world.Calendar.Settings.TicksPerHour);
        Assert.Contains("fever", conditions.Of(cold.Id));
    }

    [Fact]
    public void AHealingThingNearShortensTheCourse()
    {
        var (world, objects, inhabitants, _, conditions, content) = Bare(_ => { });
        var helped = inhabitants.Get("inh-000001");
        var alone = inhabitants.Get("inh-000003");
        foreach (var who in new[] { helped, alone })
        {
            who.Activity = new Activity("explore", null, new Position(10_000, 0), null);
            conditions.Begin(who.Id, "cold", world.Tick);
        }

        var herb = content.Objects.TypeIds.First(t => content.Objects.HasProperty(t, "healing"));
        objects.Spawn(herb, helped.Position);
        var rule = Rule(content, "cold");
        world.Run((rule.Hours * world.Calendar.Settings.TicksPerHour * 1000 / (1000 + rule.Heal!.NearPermille!.Value)) + 2);

        Assert.Empty(conditions.Of(helped.Id));
        Assert.Contains("cold", conditions.Of(alone.Id));
    }

    [Fact]
    public void ComplaintsSurviveSavingAndLoadingAndTheDeciderFeelsThem()
    {
        var content = On(_ => { });
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        var host = SimulationHost.Assemble(content, settings, new World(5, content.Calendar), new SilentPort(), "de");
        host.Inhabitants.SpawnStartInhabitants(host.World, [new(0, 0), new(30, 0), new(60, 60), new(-60, -60)]);
        var first = host.Inhabitants.All.First();
        Assert.True(host.Conditions!.Begin(first.Id, "fever", host.World.Tick));
        Assert.Equal("fever", Assert.Single(host.Decisions.FeltOf!(first.Id)).Condition);
        host.World.Run(5);

        var path = Path.Combine(Path.GetTempPath(), "gw-conditions-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var loaded = StateCodec.Restore(new SimulationHostFactory(content, settings, new SilentPort(), "de"), SaveGameStore.Load(path));
            Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
            Assert.Equal(["fever"], loaded.Conditions!.Of(first.Id));
        }
        finally
        {
            File.Delete(path);
        }

        var off = SimulationHost.CreateEmpty(ContentDirectory, 5, new SilentPort(), "de");
        Assert.Null(off.Conditions);
    }

    private static BodyCondition Rule(ContentSet content, string id) => content.Body!.Stores!.Conditions!.Single(c => c.Id == id);

    private static ContentSet On(Action<ContentSet> change)
    {
        var content = ContentSet.Load(ContentDirectory);
        content.Body!.Stores!.Enabled = true;
        change(content);
        return content;
    }

    /// <summary>Four inhabitants that stand where they are (only stores and complaints run): two close, two far.</summary>
    private static (World World, WorldObjects Objects, InhabitantsSystem Inhabitants, StoresSystem Stores, ConditionsSystem Conditions, ContentSet Content) Bare(Action<ContentSet> change)
    {
        var content = On(change);
        var world = new World(3, content.Calendar);
        var objects = new WorldObjects(content.Objects);
        var inhabitants = new InhabitantsSystem(content.InhabitantModel, content.Actions, content.Brain, content.Gaits, objects, world.Calendar.Settings.TicksPerHour);
        var stores = new StoresSystem(content.Body!.Stores!, inhabitants, objects, content.Objects, content.Gaits, world.Calendar.Settings.TicksPerHour);
        var conditions = new ConditionsSystem([.. content.Body.Stores!.Conditions!], inhabitants, objects, stores, world.Seed, world.Calendar.Settings.TicksPerHour)
        {
            HasProperty = content.Objects.HasProperty,
        };
        stores.Ill = id => conditions.Of(id).Count > 0;
        world.Add(objects).Add(stores).Add(conditions);
        inhabitants.SpawnStartInhabitants(world, [new(0, 0), new(1, 0), new(60, 60), new(-60, -60)]);
        return (world, objects, inhabitants, stores, conditions, content);
    }

    /// <summary>A decider that never answers: nothing is in flight when the world is saved.</summary>
    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
