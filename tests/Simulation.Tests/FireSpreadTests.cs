using GlassWorld.Contracts;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B5-10 (spec Bogen 30 R45-R47, D1084): wet things do not burn, a fire takes hold of dry burnable things near
/// it - capped, so the calm world never sees a wildfire - and a fire that burnt down leaves ash.
/// </summary>
public sealed class FireSpreadTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    [Fact]
    public void AFireTakesHoldOfDryGrassNextToItAndItBurnsAsAFire()
    {
        var (host, at) = Island(sure: true);
        host.Objects.Spawn("fire", at);
        var grass = host.Objects.Spawn("dry_grass", new Position(at.X + 0.5, at.Z));

        // A sure chance right at the fire, falling off a little with the distance: within a few checks.
        host.World.Run(10);

        Assert.False(host.Objects.TryGet(grass.Id, out _));
        var burning = host.Fire.Burning.Select(b => host.Objects.TryGet(b.Key, out var o) ? o : null).OfType<WorldObject>().ToList();
        Assert.Equal(2, burning.Count);
        Assert.Contains(burning, b => b.Position == grass.Position && b.TypeId == "fire");
    }

    [Fact]
    public void NoMoreThingsBurnThanTheDataAllowOnTheIsland()
    {
        var (host, at) = Island(sure: true);
        host.Objects.Spawn("fire", at);
        for (var i = 0; i < 8; i++)
        {
            host.Objects.Spawn("dry_grass", new Position(at.X + 0.3 + (i * 0.2), at.Z + 0.3));
        }

        var most = Content.Value.Fire!.Spread!.MaxBurning;
        for (var tick = 0; tick < 30; tick++)
        {
            host.World.Run(1);
            Assert.True(host.Fire.Burning.Count() <= Math.Min(most, 3), $"{host.Fire.Burning.Count()} burning at tick {tick}");
        }
    }

    [Fact]
    public void AfterRainWetGrassNeitherCatchesFireNorFeedsOneUntilItHasDried()
    {
        var (host, at) = Island(sure: true);
        var weather = host.WeatherSystem!;
        var takt = host.Nature!.TaktTicks;

        // A wet takt on the island, then dry weather again.
        weather.Force("rain", host.World.Tick);
        host.World.Run(takt + 1);
        weather.Force("clear", host.World.Tick);
        host.World.Run(1);
        host.Fire.Settings = Settings(dryingHours: 12);
        Assert.True(host.Fire.IsWet(host.World, at));

        var fire = host.Objects.Spawn("fire", at);
        var grass = host.Objects.Spawn("dry_grass", new Position(at.X + 0.5, at.Z));
        host.World.Run(5);
        Assert.True(host.Objects.TryGet(grass.Id, out _));
        Assert.False(host.Fire.Feed(host.World, fire.Id, grass.Id));

        // Dried (no drying time): now it burns.
        host.Fire.Settings = Settings(dryingHours: 0);
        Assert.False(host.Fire.IsWet(host.World, at));
        Assert.True(host.Fire.Feed(host.World, fire.Id, grass.Id));
    }

    [Fact]
    public void AFireThatBurntDownLeavesAshButOneThatTheRainPutOutDoesNot()
    {
        var (host, at) = Island(sure: false);
        var fire = host.Objects.Spawn("fire", at);
        var minutes = Content.Value.Objects.Params("fire").BurnMinutes!.Value;
        host.World.Run(((long)minutes * host.World.Calendar.Settings.TicksPerHour / 60) + 2);

        Assert.False(host.Objects.TryGet(fire.Id, out _));
        Assert.Contains(host.Objects.All, o => o.TypeId == "ash" && o.Position == at);

        var second = host.Objects.Spawn("fire", new Position(at.X + 5, at.Z));
        host.World.Run(1);
        host.WeatherSystem!.Force("rain", host.World.Tick);
        host.World.Run(2);
        Assert.False(host.Objects.TryGet(second.Id, out _));
        Assert.DoesNotContain(host.Objects.All, o => o.TypeId == "ash" && o.Position == second.Position);
    }

    [Fact]
    public void WhatTheInhabitantsBuiltDoesNotCatchFireYet()
    {
        // Review GW1 #396: a built thing of burnable material waits for the damage by quality (A1382 S2).
        var (host, at) = Island(sure: true);
        host.Fire.Settings = Settings(dryingHours: 0, spares: ["crafted"]);
        host.Types.Register("test_shelter", ["burnable", "crafted", "sheltering"]);
        host.Objects.Spawn("fire", at);
        var shelter = host.Objects.Spawn("test_shelter", new Position(at.X + 0.5, at.Z));

        host.World.Run(10);
        Assert.True(host.Objects.TryGet(shelter.Id, out _));
    }

    [Fact]
    public void ABigFireCloseByMakesAnInhabitantAfraidButACampfireAloneDoesNot()
    {
        // B5 R47: the sensation near_fire (data: fire that took hold of several things within the radius) drives fear.
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        host.WeatherSystem!.Force("clear", host.World.Tick);
        var inhabitant = host.Inhabitants.All.First();
        var felt = Content.Value.Fire!.Felt!.First();
        Assert.False(host.Fire.Feels(inhabitant.Position, felt.Sensation));

        // One campfire next to the inhabitant: warm, not frightening.
        var at = inhabitant.Position;
        host.Objects.Spawn("fire", new Position(at.X + 1, at.Z));
        host.World.Run(1);
        Assert.False(host.Fire.Feels(inhabitant.Position, felt.Sensation));
        var calm = inhabitant.Moods["fear"];

        // Dry grass around it and a sure spread: the fire takes hold of it - a big fire close by.
        var sure = Settings(dryingHours: 0);
        sure.Spread!.MaxBurning = felt.MinBurning + 2;
        sure.Felt = Content.Value.Fire!.Felt;
        host.Fire.Settings = sure;
        for (var i = 0; i < felt.MinBurning; i++)
        {
            host.Objects.Spawn("dry_grass", new Position(at.X + 1.3 + (i * 0.3), at.Z));
        }

        for (var tick = 0; tick < 60 && !host.Fire.Feels(inhabitant.Position, felt.Sensation); tick++)
        {
            host.World.Run(1);
        }

        Assert.True(host.Fire.Feels(inhabitant.Position, felt.Sensation));
        host.World.Run(host.World.Calendar.Settings.TicksPerHour / 4);
        Assert.True(inhabitant.Moods["fear"] > calm, $"{inhabitant.Moods["fear"]} <= {calm}");
    }

    [Fact]
    public void TwoCampfiresAtTheCampFrightenNobody()
    {
        // Review GW2 #398: only fire that took hold counts as a big fire, not the fires the inhabitants made.
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        host.WeatherSystem!.Force("clear", host.World.Tick);
        host.Fire.Settings = null;
        var inhabitant = host.Inhabitants.All.First();
        var felt = Content.Value.Fire!.Felt!.First();
        Assert.True(felt.CaughtOnly);
        for (var i = 0; i < felt.MinBurning + 1; i++)
        {
            host.Objects.Spawn("fire", new Position(inhabitant.Position.X + 1 + i, inhabitant.Position.Z));
        }

        host.World.Run(1);
        host.Fire.Settings = Content.Value.Fire;
        Assert.False(host.Fire.Feels(inhabitant.Position, felt.Sensation));
    }

    [Fact]
    public void WhereFireTookHoldIsRememberedInTheSaveGame()
    {
        var (host, at) = Island(sure: true);
        host.Objects.Spawn("fire", at);
        host.Objects.Spawn("dry_grass", new Position(at.X + 0.5, at.Z));
        host.World.Run(10);
        var caught = Assert.Single(host.Fire.Capture(), b => b.Caught);
        var restored = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new MockDecisionPort(), "de", "start_island");
        restored.Fire.Restore(host.Fire.Capture());
        Assert.Contains(restored.Fire.Capture(), b => b.Id == caught.Id && b.Caught);
    }

    [Fact]
    public void ThePlaceholderWorldKeepsItsFiresToThemselves()
    {
        // Spreading, drying and remains need an island (weather, nature takt), like the other B5 systems.
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 7, new MockDecisionPort(), "de");
        Assert.Null(host.Fire.Settings);
    }

    /// <summary>A start island in clear weather and a free place far from the inhabitants (nobody feeds or eats there).</summary>
    private static (SimulationHost Host, Position At) Island(bool sure)
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new MockDecisionPort(), "de", "start_island");
        host.World.Run(1);
        host.WeatherSystem!.Force("clear", host.World.Tick);
        host.World.Run(1);
        if (sure)
        {
            host.Fire.Settings = Settings(dryingHours: 0);
        }

        var first = host.Inhabitants.All.First().Position;
        var at = new Position(first.X + 40, first.Z);

        // The place is cleared of anything that burns, so only the test's things are there.
        var near = new List<WorldObject>();
        host.Objects.Within(at, 6, near);
        foreach (var thing in near)
        {
            host.Objects.Remove(thing.Id);
        }

        return (host, at);
    }

    /// <summary>The data's fire settings with a sure spread (every tick, always at the fire, one at a time, at most 3).</summary>
    private static FireSettings Settings(int dryingHours, ICollection<string>? spares = null)
    {
        var data = Content.Value.Fire!;
        return new FireSettings
        {
            ContractVersion = data.ContractVersion,
            FuelProperty = data.FuelProperty,
            Drying = [new FireDryingBand { BelowTenthC = null, Hours = dryingHours }],
            Spread = new FireSpread { EveryMinutes = 1, RadiusCm = 250, ChancePermille = 1000, WindPermille = 0, MaxNew = 1, MaxBurning = 3, Becomes = data.Spread!.Becomes, Spares = spares ?? [] },
            Remains = data.Remains,
        };
    }
}
