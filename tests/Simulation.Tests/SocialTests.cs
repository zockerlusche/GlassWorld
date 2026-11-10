using GlassWorld.Contracts;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// TL #20190: company reaches both participants of a social action (data: listener_share), and being near a
/// familiar, awake inhabitant fills company slowly up to a cap (data: togetherness). Only need effects.
/// </summary>
public sealed class SocialTests
{
    private static readonly ContentSet Content = ContentSet.Load(Path.Combine(TestData.Root, "content"));
    private static readonly Togetherness Together = Content.InhabitantModel.Togetherness!;
    private static readonly Relation Stranger = new(0.0, 0.0, 0.5);

    [Fact]
    public void TheListenerOfAWarningGetsTheDataShareOfTheCompany()
    {
        var effect = Content.Actions.Applying("warn", null).SelectMany(r => r.Effects).Single(e => e.Id == "company");
        Assert.True(effect.ListenerShare > 0, "content/effects: warn shares company with the listener");

        var (world, inhabitants) = NewWorld(new(0, 0), new(1.2, 0));
        var teller = inhabitants.Get("inh-000001");
        var listener = inhabitants.Get("inh-000002");
        listener.Relationships.Set(teller.Id, Stranger); // no togetherness for the listener: only the share counts
        listener.Needs["company"] = 0.1;
        var start = world.Tick;
        teller.Activity = new Activity("warn", listener.Id, listener.Position, null);
        for (var i = 0; i < 1_000 && teller.Activity is not null; i++)
        {
            world.Step();
        }

        Assert.Null(teller.Activity);
        var decay = DecayPerTick(world) * (world.Tick - start);
        Assert.Equal(0.1 - decay + (effect.Delta * effect.ListenerShare!.Value), listener.Needs["company"], 9);
    }

    [Fact]
    public void BeingNearAFamiliarAwakeInhabitantFillsCompanyButNotNearAStrangerOrASleeper()
    {
        var near = Together.DistanceM!.Value * 0.8;
        var (world, inhabitants) = NewWorld(new(0, 0), new(near, 0));
        var a = inhabitants.Get("inh-000001");
        var b = inhabitants.Get("inh-000002");
        var c = inhabitants.Get("inh-000003");
        a.Needs["company"] = b.Needs["company"] = c.Needs["company"] = 0.1;

        world.Step();
        var decay = DecayPerTick(world);
        var fill = Together.PerHour / world.Calendar.Settings.TicksPerHour;
        Assert.Equal(0.1 - decay + fill, a.Needs["company"], 12);
        Assert.Equal(0.1 - decay + fill, b.Needs["company"], 12);
        Assert.Equal(0.1 - decay, c.Needs["company"], 12); // familiar, but far away

        // Each sees the other with its own eyes: a now finds b a stranger, b still finds a familiar.
        a.Relationships.Set(b.Id, Stranger);
        a.Needs["company"] = b.Needs["company"] = 0.1;
        world.Step();
        Assert.Equal(0.1 - decay, a.Needs["company"], 12);
        Assert.Equal(0.1 - decay + fill, b.Needs["company"], 12);

        // Nobody fills while the other sleeps.
        a.Relationships.Set(b.Id, new Relation(Together.MinAffection, Together.MinTrust, 0.5));
        a.Activity = new Activity(Together.SleepAction, null, a.Position, 1_000);
        a.Needs["company"] = b.Needs["company"] = 0.1;
        world.Step();
        Assert.Equal(0.1 - decay, a.Needs["company"], 12);
        Assert.Equal(0.1 - decay, b.Needs["company"], 12);
    }

    [Fact]
    public void TogethernessNeverFillsAboveTheCap()
    {
        var (world, inhabitants) = NewWorld(new(0, 0), new(1.2, 0));
        var a = inhabitants.Get("inh-000001");
        a.Needs["company"] = Together.Cap - 0.0001;
        for (var i = 0; i < 30; i++)
        {
            world.Step();
            Assert.Equal(Together.Cap, a.Needs["company"], 12);
        }

        // Above the cap (from a social action) it only decays.
        a.Needs["company"] = 0.9;
        world.Step();
        Assert.Equal(0.9 - DecayPerTick(world), a.Needs["company"], 12);
    }

    private static double DecayPerTick(World world) =>
        Content.InhabitantModel.Needs.Single(n => n.Id == "company").DecayPerHour / world.Calendar.Settings.TicksPerHour;

    /// <summary>A world with a and b at the given spots and the other two far away; everyone idles until told otherwise.</summary>
    private static (World World, InhabitantsSystem Inhabitants) NewWorld(Position a, Position b)
    {
        var world = new World(3, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Content.Knowledge,
        };
        world.Add(objects).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [a, b, new(60, 60), new(-60, -60)]);
        foreach (var inhabitant in inhabitants.All)
        {
            inhabitant.Activity = new Activity("idle", null, inhabitant.Position, 1_000);
        }

        return (world, inhabitants);
    }
}
