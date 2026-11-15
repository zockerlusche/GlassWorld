using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B2773 (GW1 #26014): the need safety falls only while all of its decay_when conditions hold - at night, nobody
/// else near, no shelter; missing one of them it stays or recovers (recover_per_hour).
/// </summary>
public sealed class SafetyNeedTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);
    private const string Safety = "safety";

    [Fact]
    public void AloneAtNightWithoutShelterSafetyFalls()
    {
        var (world, inhabitants) = NewWorld(apart: 100, sheltered: false);
        Assert.Equal(DayPhase.Night, world.Now.Phase);
        var inhabitant = inhabitants.All.First();
        var hour = world.Calendar.Settings.TicksPerHour;
        world.Run(hour);
        var when = Content.InhabitantModel.Needs.First(n => n.Id == Safety).DecayWhen!;
        Assert.Equal(1.0 - when.PerHour, inhabitant.Needs[Safety], 6);
    }

    [Fact]
    public void WithCompanyNearSafetyStays()
    {
        var (world, inhabitants) = NewWorld(apart: 2, sheltered: false);
        var inhabitant = inhabitants.All.First();
        inhabitant.Needs[Safety] = 0.5;
        world.Run(world.Calendar.Settings.TicksPerHour);
        Assert.True(inhabitant.Needs[Safety] >= 0.5, $"{inhabitant.Needs[Safety]} fell");
    }

    [Fact]
    public void UnderShelterSafetyStays()
    {
        var (world, inhabitants) = NewWorld(apart: 100, sheltered: true);
        var inhabitant = inhabitants.All.First();
        inhabitant.Needs[Safety] = 0.5;
        world.Run(world.Calendar.Settings.TicksPerHour);
        Assert.True(inhabitant.Needs[Safety] >= 0.5, $"{inhabitant.Needs[Safety]} fell");
    }

    [Fact]
    public void ByDaySafetyStays()
    {
        var (world, inhabitants) = NewWorld(apart: 100, sheltered: false);
        while (world.Now.Phase != DayPhase.Day)
        {
            world.Step();
        }

        var inhabitant = inhabitants.All.First();
        inhabitant.Needs[Safety] = 0.5;
        world.Run(world.Calendar.Settings.TicksPerHour);
        Assert.True(inhabitant.Needs[Safety] > 0.5, $"{inhabitant.Needs[Safety]} did not recover by day");
    }

    /// <summary>A bare world with the start inhabitants <paramref name="apart"/> metres from each other, all idling, safety full.</summary>
    private static (World World, InhabitantsSystem Inhabitants) NewWorld(double apart, bool sheltered)
    {
        var world = new World(3, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Content.Knowledge,
            Sheltered = _ => sheltered,
        };
        world.Add(objects).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [new(0, 0), new(apart, 0), new(0, apart), new(apart, apart)]);
        foreach (var inhabitant in inhabitants.All)
        {
            inhabitant.Activity = new Activity("idle", null, inhabitant.Position, 100_000);
            inhabitant.Needs[Safety] = 1.0;
        }

        return (world, inhabitants);
    }
}
