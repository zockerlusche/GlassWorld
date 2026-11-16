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
/// B5-11 (spec Bogen 30 R29/R30, D1084): rain wets an inhabitant out in the open and being wet feels colder; it
/// dries in dry weather; a shelter keeps the rain off; the sun warms where no tree's shade falls.
/// </summary>
public sealed class BodyTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    [Fact]
    public void RainWetsAnInhabitantOutInTheOpenItFeelsColderAndItDriesAgain()
    {
        var (host, inhabitant) = Island();
        var weather = host.WeatherSystem!;
        var body = host.Body!;
        var hour = host.World.Calendar.Settings.TicksPerHour;

        // It stays where it stands: since A1793 wading wets too, so a walk through a stream would keep it wet (A1578: the
        // ore on the island changed where it walked). This test is about rain and drying alone.
        inhabitant.Activity = new Activity("idle", null, inhabitant.Position, 1_000_000);
        weather.Force("rain", host.World.Tick);
        host.World.Run(hour);
        Assert.Equal(1000, body.WetPermille(inhabitant.Id));
        Assert.True(host.Decisions.IsWet!(inhabitant));
        var cool = Content.Value.Body!.Wetness.CoolTenthC;
        var expected = body.SunTenthC(host.World, inhabitant.Position) - cool - body.WindTenthC(host.World, inhabitant.Position) + body.OthersWarmthTenthC(inhabitant);
        Assert.Equal(expected, body.FeltTenthC(host.World, inhabitant));

        weather.Force("clear", host.World.Tick);
        host.World.Run(hour);
        Assert.InRange(body.WetPermille(inhabitant.Id), 0, 999);
        host.World.Run(12 * hour);
        Assert.Equal(0, body.WetPermille(inhabitant.Id));
    }

    [Fact]
    public void WaterWetsWhoStandsInItEvenUnderAClearSkyAndTheWetCools()
    {
        // A1793 (Marcel 15a): wading through cold water made nobody colder - only rain wetted.
        var (host, inhabitant) = Island();
        var body = host.Body!;
        var map = host.Map!;
        host.WeatherSystem!.Force("clear", host.World.Tick);
        Assert.Equal(0, body.WetPermille(inhabitant.Id));

        // The nearest cell with water one can stand in.
        var at = map.CellOf(inhabitant.Position)!.Value;
        var water = Enumerable.Range(0, map.Map.WidthCells * map.Map.DepthCells)
            .Select(i => (X: i % map.Map.WidthCells, Z: i / map.Map.WidthCells))
            .Where(c => map.Map.Cell(c.X, c.Z) is { Water: not Map.WaterKind.None } cell && cell.WalkCost != Map.MapCell.Blocked)
            .OrderBy(c => Math.Abs(c.X - at.X) + Math.Abs(c.Z - at.Z)).ThenBy(c => c.Z).ThenBy(c => c.X)
            .First();
        var hour = host.World.Calendar.Settings.TicksPerHour;
        // The body alone, a quarter of an hour of its steps: the inhabitant's own walking cannot carry it out of the water.
        inhabitant.Position = map.CentreOf(water.X, water.Z);
        for (var t = 0; t < hour / 4; t++)
        {
            body.Advance(host.World);
        }

        // Soaked through, and the wet cools exactly as after rain.
        Assert.Equal(1000, body.WetPermille(inhabitant.Id));
        var cool = Content.Value.Body!.Wetness.CoolTenthC;
        var expected = body.SunTenthC(host.World, inhabitant.Position) - cool - body.WindTenthC(host.World, inhabitant.Position) + body.OthersWarmthTenthC(inhabitant)
            + body.StillAirTenthC(inhabitant.Position);
        Assert.Equal(expected, body.FeltTenthC(host.World, inhabitant));
    }

    [Fact]
    public void UnderAShelterTheStillAirFeelsWarmer()
    {
        // A1793 (B2840): under a shelter the air stands still - it feels the data's degrees warmer than in the open.
        var (host, inhabitant) = Island();
        var body = host.Body!;
        host.WeatherSystem!.Force("clear", host.World.Tick);
        var still = Content.Value.Body!.Wetness.ShelterStillAirTenthC!.Value;
        Assert.True(still > 0);
        Assert.Equal(0, body.StillAirTenthC(inhabitant.Position));

        host.Objects.Spawn("rock_niche", new Position(inhabitant.Position.X + 1, inhabitant.Position.Z));
        Assert.Equal(still, body.StillAirTenthC(inhabitant.Position));
        var expected = body.SunTenthC(host.World, inhabitant.Position) - body.WindTenthC(host.World, inhabitant.Position) + body.OthersWarmthTenthC(inhabitant) + still;
        Assert.Equal(expected, body.FeltTenthC(host.World, inhabitant));
    }

    [Fact]
    public void TheFlatsTheEbbLeavesDryDoNotWet()
    {
        // Review GW2 #518: a sea cell wets only while the tide covers it.
        var (host, _) = Island();
        var body = host.Body!;
        var map = host.Map!;
        var tides = map.Tides!;
        Assert.True(tides.Active);
        var lowest = Enumerable.Range(0, tides.PeriodTicks).Min(t => tides.LevelUnitsAt(host.World.Tick + t));
        var flat = Enumerable.Range(0, map.Map.WidthCells * map.Map.DepthCells)
            .Select(i => (X: i % map.Map.WidthCells, Z: i / map.Map.WidthCells))
            .First(c => map.Map.Cell(c.X, c.Z) is { Water: Map.WaterKind.Salt } cell && cell.WalkCost != Map.MapCell.Blocked && cell.WaterDepth + lowest <= 0);
        var at = map.CentreOf(flat.X, flat.Z);
        var depth = map.Map.Cell(flat.X, flat.Z).WaterDepth;

        // Over one tide period: wet exactly while the water stands over it.
        var (dry, wet) = (false, false);
        for (var t = 0; t < tides.PeriodTicks && !(dry && wet); t++)
        {
            var covered = depth + tides.LevelUnitsAt(host.World.Tick) > 0;
            Assert.Equal(covered, body.InWater!(at));
            (dry, wet) = (dry || !covered, wet || covered);
            host.World.Step();
        }

        Assert.True(dry && wet, "the period has both");
    }

    [Fact]
    public void AShelterKeepsTheRainOff()
    {
        var (host, inhabitant) = Island();
        host.Objects.Spawn("rock_niche", new Position(inhabitant.Position.X + 1, inhabitant.Position.Z));
        host.WeatherSystem!.Force("rain", host.World.Tick);
        host.World.Run(host.World.Calendar.Settings.TicksPerHour);
        Assert.Equal(0, host.Body!.WetPermille(inhabitant.Id));
    }

    [Fact]
    public void ATreeCastsItsShadeAwayFromTheSunAndTheSunWarmsOnlyOutsideIt()
    {
        var (host, inhabitant) = Island();
        host.WeatherSystem!.Force("clear", host.World.Tick);

        // Noon: the shadow falls the way the sun table says.
        for (var i = 0; i < 24 * host.World.Calendar.Settings.TicksPerHour && host.World.Now.Hour != 12; i++)
        {
            host.World.Run(1);
        }

        var sun = Content.Value.Body!.Sun.Positions.Where(p => p.Season == host.World.Now.SeasonId && p.Hour <= 12).MaxBy(p => p.Hour)!;
        var at = new Position(inhabitant.Position.X + 60, inhabitant.Position.Z);
        var cleared = new List<WorldObject>();
        host.Objects.Within(at, 60, cleared);
        foreach (var thing in cleared)
        {
            host.Objects.Remove(thing.Id);
        }

        host.Objects.Spawn("deciduous_tree", at);
        var crownM = Content.Value.Objects.Params("deciduous_tree").CrownRadiusCm!.Value / 100.0;
        var lengthM = Content.Value.Objects.Params("deciduous_tree").ShadeHeightCm!.Value * sun.ShadowPermille / 1000 / 100.0;
        var step = crownM + Math.Min(lengthM, 1.0);
        var behind = new Position(at.X + (sun.TowardXPermille!.Value * step / 1000), at.Z + (sun.TowardZPermille!.Value * step / 1000));
        var before = new Position(at.X - (sun.TowardXPermille!.Value * step / 1000), at.Z - (sun.TowardZPermille!.Value * step / 1000));

        Assert.True(host.Body!.InShade(host.World, behind));
        Assert.False(host.Body.InShade(host.World, before));
        // In the shade the sun warms by what the tree lets through in this season (TL #25357).
        var open = host.Body.SunTenthC(host.World, before);
        Assert.True(open > 0);
        var through = 1000 - host.Body.CoverOf("deciduous_tree", host.World.Now.SeasonId);
        Assert.InRange(host.Body.SunTenthC(host.World, behind), (open * through / 1000) - 1, (open * through / 1000) + 1);
    }

    [Fact]
    public void ABroadLeavedTreeCoversLittleInWinterAndAConiferAllYear()
    {
        // TL #25357: the cover by species and season (data).
        var (host, _) = Island();
        var body = host.Body!;
        Assert.True(body.CoverOf("deciduous_tree", "winter") < body.CoverOf("deciduous_tree", "summer"));
        Assert.Equal(body.CoverOf("conifer_tree", "winter"), body.CoverOf("conifer_tree", "summer"));
        Assert.Equal(1000, body.CoverOf("not_a_tree", "summer"));
    }

    [Fact]
    public void UnderACrownLessRainComesThrough()
    {
        // GW1 #409: a tree's crown keeps part of the rain off, by its cover in the season.
        var (host, inhabitant) = Island();
        var near = new List<WorldObject>();
        host.Objects.Within(inhabitant.Position, 20, near);
        foreach (var thing in near)
        {
            host.Objects.Remove(thing.Id);
        }

        host.Objects.Spawn("conifer_tree", new Position(inhabitant.Position.X + 1, inhabitant.Position.Z));
        host.WeatherSystem!.Force("rain", host.World.Tick);
        host.World.Run(10);
        var cover = host.Body!.CoverOf("conifer_tree", host.World.Now.SeasonId);
        var open = Content.Value.Body!.Wetness.RainPerHour * 10 / host.World.Calendar.Settings.TicksPerHour;
        Assert.InRange(host.Body.WetPermille(inhabitant.Id), 1, Math.Max(1, open * (1000 - cover) / 1000) + 1);
    }

    [Fact]
    public void NearAFireAnInhabitantFindsOutThatItIsHot()
    {
        // B5 R37: what it felt near what becomes a belief with source experienced (no rule what to do about it).
        var (host, inhabitant) = Island();
        host.Objects.Spawn("fire", new Position(inhabitant.Position.X + 0.5, inhabitant.Position.Z));
        host.World.Run(Content.Value.Body!.ExperienceEveryMinutes!.Value * host.World.Calendar.Settings.TicksPerHour / 60 + 1);

        Assert.True(inhabitant.Beliefs.TryGet("fire", "hot", out var hot));
        Assert.True(hot.Value);
        Assert.Equal(KnowledgeSources.Experienced, hot.Source);
    }

    [Fact]
    public void TooCloseToAFireAnInhabitantStepsBackAtOnceAndKnowsItIsHot()
    {
        // A1731 (B2716 1): a reflex from the world, without AI - step back, flinch, know it is hot; nothing shown hurt.
        var (host, inhabitant) = Island();
        var reflex = Content.Value.Body!.Reflex!;
        var flinched = new List<WorldEvent>();
        host.Inhabitants.Reported += e =>
        {
            if (e.Kind == BodySystem.Flinched && e.InhabitantId == inhabitant.Id)
            {
                flinched.Add(e);
            }
        };

        inhabitant.Activity = new Activity("idle", null, inhabitant.Position, 100_000_000); // stands still: only the reflex moves it
        var fire = host.Objects.Spawn("fire", new Position(inhabitant.Position.X + 0.2, inhabitant.Position.Z));
        host.World.Step();

        var e = Assert.Single(flinched);
        Assert.Equal(fire.Id, e.TargetId);
        var distance = Math.Sqrt(Math.Pow(inhabitant.Position.X - fire.Position.X, 2) + Math.Pow(inhabitant.Position.Z - fire.Position.Z, 2));
        Assert.True(distance >= (reflex.StepBackCm / 100.0) - 0.05, $"stepped back to {distance:0.00} m");
        Assert.True(inhabitant.Beliefs.TryGet("fire", reflex.Property ?? "hot", out var hot) && hot.Value, "it knows at once that the fire is hot");
    }

    [Fact]
    public void ASleeperAndOneWhoseActionIsOnTheFireDoNotFlinch()
    {
        // Review GW3 #573: no flinch in sleep, and none at the fire one went to on purpose (no arrive-flinch-return loop).
        var (host, inhabitant) = Island();
        var flinches = 0;
        host.Inhabitants.Reported += e => flinches += e.Kind == BodySystem.Flinched ? 1 : 0;
        var fire = host.Objects.Spawn("fire", new Position(inhabitant.Position.X + 0.2, inhabitant.Position.Z));
        var at = inhabitant.Position;

        inhabitant.Activity = new Activity("sleep", null, at, 100_000_000);
        host.World.Step();
        Assert.Equal(0, flinches);
        Assert.Equal(at, inhabitant.Position);

        inhabitant.Activity = new Activity("inspect", fire.Id, at, 100_000_000);
        host.World.Step();
        Assert.Equal(0, flinches);
        Assert.Equal(at, inhabitant.Position);
    }

    [Fact]
    public void BeyondTheReflexRadiusNobodyFlinches()
    {
        var (host, inhabitant) = Island();
        var flinches = 0;
        host.Inhabitants.Reported += e => flinches += e.Kind == BodySystem.Flinched ? 1 : 0;
        inhabitant.Activity = new Activity("idle", null, inhabitant.Position, 100_000_000); // stands still: only the reflex moves it
        host.Objects.Spawn("fire", new Position(inhabitant.Position.X + 1.0, inhabitant.Position.Z));
        host.World.Step();
        Assert.Equal(0, flinches);
    }

    [Fact]
    public void InTheRainAnInhabitantFindsOutThatARockNicheKeepsItDry()
    {
        var (host, inhabitant) = Island();
        host.Objects.Spawn("rock_niche", new Position(inhabitant.Position.X + 1, inhabitant.Position.Z));
        host.WeatherSystem!.Force("rain", host.World.Tick);
        host.World.Run(Content.Value.Body!.ExperienceEveryMinutes!.Value * host.World.Calendar.Settings.TicksPerHour / 60 + 1);

        Assert.True(inhabitant.Beliefs.TryGet("rock_niche", "sheltering", out var sheltering));
        Assert.True(sheltering.Value);
        Assert.Equal(KnowledgeSources.Experienced, sheltering.Source);
    }

    [Fact]
    public void TooColdAndTooWarmFollowTheComfortLevel()
    {
        // B5 R31: the comfort sensations of the data are felt exactly at their levels.
        var (host, inhabitant) = Island();
        var felt = Content.Value.InhabitantModel.ComfortZone!.Felt!;
        for (var hour = 0; hour < 24; hour++)
        {
            var level = host.Inhabitants.ComfortLevelOf(host.World, inhabitant);
            foreach (var sensation in felt)
            {
                Assert.Equal(level is not null && sensation.Levels.Contains(level), host.Inhabitants.FeelsComfort(host.World, inhabitant, sensation.Sensation));
            }

            host.World.Run(host.World.Calendar.Settings.TicksPerHour);
        }
    }

    [Fact]
    public void AnInhabitantRemembersHowItsPlaceFeltAndForgetsItLater()
    {
        // B5 R32 (D1099): the latest feeling per grid square, bounded and forgotten after the data's time.
        var (host, inhabitant) = Island();
        var places = Content.Value.Body!.FeltPlaces!;
        host.Objects.Spawn("rock_niche", new Position(inhabitant.Position.X + 1, inhabitant.Position.Z));
        host.WeatherSystem!.Force("rain", host.World.Tick);
        host.World.Run(Content.Value.Body!.ExperienceEveryMinutes!.Value * host.World.Calendar.Settings.TicksPerHour / 60 + 1);

        var place = $"{(long)Math.Floor(inhabitant.Position.X / places.CellM)}/{(long)Math.Floor(inhabitant.Position.Z / places.CellM)}";
        Assert.Equal(places.KeptDry, inhabitant.FeltPlaces[place].Felt);

        inhabitant.RememberFelt("far/away", new FeltPlace("cold", host.World.Tick), places.Max, (long)places.ForgetHours * host.World.Calendar.Settings.TicksPerHour);
        inhabitant.RememberFelt("later/on", new FeltPlace("warm", host.World.Tick + ((long)places.ForgetHours * host.World.Calendar.Settings.TicksPerHour)), places.Max, (long)places.ForgetHours * host.World.Calendar.Settings.TicksPerHour);
        Assert.DoesNotContain("far/away", inhabitant.FeltPlaces.Keys);
        Assert.Contains("later/on", inhabitant.FeltPlaces.Keys);
    }

    [Fact]
    public void FeltPlacesAreBoundedPerInhabitant()
    {
        var (_, inhabitant) = Island();
        for (var i = 0; i < 10; i++)
        {
            inhabitant.RememberFelt($"{i}/0", new FeltPlace("mild", 100 + i), 3, 1_000_000);
        }

        Assert.Equal(["7/0", "8/0", "9/0"], inhabitant.FeltPlaces.Keys);
    }

    [Fact]
    public void AWetInhabitantStaysWetAcrossSavingAndLoading()
    {
        var (host, inhabitant) = Island();
        host.WeatherSystem!.Force("rain", host.World.Tick);
        host.World.Run(10);
        var wet = host.Body!.WetPermille(inhabitant.Id);
        Assert.True(wet > 0);

        var path = Path.Combine(Path.GetTempPath(), "gw-body-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
            var loaded = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new SilentPort(), "de"), SaveGameStore.Load(path));
            Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
            Assert.Equal(wet, loaded.Body!.WetPermille(inhabitant.Id));
            Assert.Equal(inhabitant.FeltPlaces, loaded.Inhabitants.Get(inhabitant.Id).FeltPlaces);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ThePlaceholderWorldHasNoBodyWeather()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 7, new SilentPort(), "de");
        Assert.Null(host.Body);
    }

    /// <summary>A start island whose inhabitants get no answers (they stay where they are), the first one out in the open.</summary>
    private static (SimulationHost Host, Inhabitant Inhabitant) Island()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new SilentPort(), "de", "start_island");
        host.World.Run(1);
        var inhabitant = host.Inhabitants.All.First();
        var near = new List<WorldObject>();
        host.Objects.Within(inhabitant.Position, 5, near);
        foreach (var thing in near.Where(t => host.Objects.Catalog.TrueProperties(t.TypeId).Contains("sheltering")))
        {
            host.Objects.Remove(thing.Id);
        }

        return (host, inhabitant);
    }

    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
