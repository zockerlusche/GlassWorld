using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Presentation.Tests;

/// <summary>Island mode of the main game (TL decision #19505): switch, object look, ground ray, roster, observer.</summary>
public sealed class IslandLookTests
{
    private static readonly string Content = Path.Combine(TestPaths.Root, "content");

    private static T Read<T>(params string[] path)
        where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine([Content, .. path])));

    private static LookWorld World(LookWorldWorld world)
    {
        var look = Read<LookWorld>("look", "world.json");
        look.World = world;
        return look;
    }

    private static ObjectLooks Looks() =>
        new(Read<LookObjects>("look", "objects.json"), Read<LookPalette>("look", "palette.json").Colors.Select(c => c.Id).ToHashSet(StringComparer.Ordinal));

    [Theory]
    [InlineData(LookWorldWorld.Placeholder, null, WorldLookMode.Placeholder)]
    [InlineData(LookWorldWorld.Island, null, WorldLookMode.Island)]
    [InlineData(LookWorldWorld.Placeholder, "island", WorldLookMode.Island)]
    [InlineData(LookWorldWorld.Placeholder, " Island ", WorldLookMode.Island)]
    [InlineData(LookWorldWorld.Island, "placeholder", WorldLookMode.Placeholder)]
    [InlineData(LookWorldWorld.Island, "", WorldLookMode.Island)]
    public void TheEnvironmentOverridesTheDataForNewWorlds(LookWorldWorld data, string? environment, WorldLookMode expected)
    {
        Assert.Equal(expected, WorldLookChoice.ForNewWorld(World(data), environment, out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void AnUnknownEnvironmentValueKeepsTheDataValueAndSaysSo()
    {
        Assert.Equal(WorldLookMode.Placeholder, WorldLookChoice.ForNewWorld(World(LookWorldWorld.Placeholder), "islnd", out var problem));
        Assert.Contains(WorldLookChoice.EnvironmentVariable, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorldDataNamesAPresetAndADayPhaseThatExist()
    {
        var look = Read<LookWorld>("look", "world.json");
        Assert.True(File.Exists(Path.Combine(Content, "world", "presets", look.IslandPreset + ".json")));
        Assert.Contains(Read<LookDaylight>("look", "daylight.json").Phases, p => p.Phase.ToString().Equals(look.DayPhase, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryCatalogTypeHasALookAndEveryColourIsInThePalette()
    {
        // The constructor rejects colours outside the palette and types with two visuals.
        var looks = Looks();
        var catalog = Read<ObjectTypes>("objects", "start-catalog.json");
        Assert.All(catalog.Types, t => Assert.True(looks.Knows(t.Id), $"{t.Id} has no entry in content/look/objects.json"));
        Assert.Equal(ObjectLookShape.Marker, looks.Fallback.Shape);
    }

    [Fact]
    public void ALoadedWorldIsDrawnTheWayItWasMade()
    {
        var host = SimulationHost.CreateGeneratedWorld(Content, 7, new MockDecisionPort(), "de", Read<LookWorld>("look", "world.json").IslandPreset);
        host.World.Run(120);

        // The save game carries the island (SaveGameStore round trip: Simulation.Tests/IslandWalkTests).
        var loaded = StateCodec.Restore(new SimulationHostFactory(host.Content, host.DeciderSettings, new MockDecisionPort(), "de"), StateCodec.Capture(host));
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(WorldLookMode.Island, WorldLookChoice.ForWorld(loaded.Map is not null));
        Assert.Equal(host.Map!.IslandHash, loaded.Map!.IslandHash);

        var placeholder = SimulationHost.CreatePlaceholderWorld(Content, 7, new MockDecisionPort(), "de");
        Assert.Equal(WorldLookMode.Placeholder, WorldLookChoice.ForWorld(placeholder.Map is not null));
    }

    [Fact]
    public void EveryObjectOfTheStartIslandHasItsOwnLookAndSeaWaterIsNotDrawn()
    {
        var host = SimulationHost.CreateGeneratedWorld(Content, 7, new MockDecisionPort(), "de", "start_island");
        var looks = Looks();
        var unknown = new List<string>();
        var layout = looks.Layout(host.Objects.All, (_, _) => 1.0, unknown.Add);
        Assert.Empty(unknown);
        Assert.DoesNotContain(ObjectLooks.FallbackId, layout.Keys);
        var seaWater = host.Objects.All.Count(o => looks.For(o.TypeId).Shape == ObjectLookShape.None);
        Assert.Equal(host.Objects.Count - seaWater, layout.Values.Sum(l => l.Count));
        Assert.Contains("broadleaf", layout.Keys);
    }

    [Fact]
    public void AnUnknownTypeGetsTheMarkerAndIsReportedOnce()
    {
        var looks = Looks();
        var objects = new[] { new WorldObject("o1", "glass_marble", new Position(1, 2)), new WorldObject("o2", "glass_marble", new Position(3, 4)) };
        var reported = new List<string>();
        var layout = looks.Layout(objects, (_, _) => 0.5, reported.Add);
        Assert.Equal(["glass_marble"], reported);
        Assert.Equal(2, layout[ObjectLooks.FallbackId].Count);
        looks.Layout(objects, (_, _) => 0.5, reported.Add);
        Assert.Single(reported);
    }

    [Fact]
    public void PlacementStandsOnTheGroundAndComesFromTheIdAlone()
    {
        var look = new ObjectLook("tree", ObjectLookShape.Broadleaf_tree, ["foliage"], 2.0, 0.2, -0.1);
        var tree = new WorldObject("obj_42", "deciduous_tree", new Position(10, 20));
        var a = ObjectLooks.Place(tree, look, (x, z) => (x + z) / 10);
        var b = ObjectLooks.Place(tree with { }, look, (x, z) => (x + z) / 10);
        Assert.Equal(a, b);
        Assert.Equal(3.0 - 0.1, a.Y, 9);
        Assert.InRange(a.Scale, 2.0 * 0.8, 2.0 * 1.2);
        Assert.InRange(a.YawRad, 0, 2 * Math.PI);
        var other = ObjectLooks.Place(tree with { Id = "obj_43" }, look, (_, _) => 0);
        Assert.NotEqual(a.YawRad, other.YawRad);
        Assert.Equal("foliage", look.Color(2));
    }

    [Fact]
    public void AGiftOnWaterOrOffTheIslandIsRefusedWithAReason()
    {
        // Column 0 is sea, column 1 a lake, the rest dry land; the sea surface lies at 2.0 m.
        var builder = new Simulation.Map.WorldMapBuilder(4, 1, 1, ["low"]);
        for (var z = 0; z < 4; z++)
        {
            for (var x = 0; x < 4; x++)
            {
                builder.Set(x, z, x switch
                {
                    0 => new Simulation.Map.MapCell(10, Simulation.Map.WaterKind.Salt, 10, -1, 1000, 150, 0, Simulation.Map.MapCell.Blocked),
                    1 => new Simulation.Map.MapCell(26, Simulation.Map.WaterKind.Fresh, 4, -1, 1000, 150, 0, 3000),
                    _ => new Simulation.Map.MapCell(30, Simulation.Map.WaterKind.None, 0, -1, 400, 150, 0, 1000),
                });
            }
        }

        var map = builder.Build();
        Assert.Equal(GiftSpot.OnWater, GiftSpot.Refusal(map, 1, 0.5, 2.5, 1.0, 2.0));
        Assert.Equal(GiftSpot.OnWater, GiftSpot.Refusal(map, 1, 1.5, 2.5, 2.6, 2.0));
        Assert.Null(GiftSpot.Refusal(map, 1, 2.5, 2.5, 3.0, 2.0));
        Assert.Equal(GiftSpot.OffIsland, GiftSpot.Refusal(map, 1, -0.5, 2.5, 3.0, 2.0));
        Assert.Equal(GiftSpot.OffIsland, GiftSpot.Refusal(map, 1, 2.5, 4.2, 3.0, 2.0));

        // Dry land below the sea surface (a dip at the coast) counts as water too.
        Assert.Equal(GiftSpot.OnWater, GiftSpot.Refusal(map, 1, 3.5, 0.5, 1.9, 2.0));

        // The click ray meets the sea surface where the seabed lies lower: it lands where the player clicked.
        var surface = GiftSpot.Surface((x, _) => x < 10 ? -3.0 : 1.0, 0.0);
        var hit = GroundRay.Hit(new Point3(0, 10, 0), new Point3(0, -1, 1), surface, 0.5, 1000, 20)!.Value;
        Assert.Equal(10.0, hit.Z, 3);
        Assert.Equal(0.0, hit.Y, 9);

        // Every reason has a text in both languages.
        foreach (var language in new[] { "de", "en" })
        {
            var catalog = File.ReadAllText(Path.Combine(Content, "i18n", language + ".json"));
            Assert.Contains($"\"{GiftSpot.OnWater}\"", catalog, StringComparison.Ordinal);
            Assert.Contains($"\"{GiftSpot.OffIsland}\"", catalog, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(8.0, 0.0)]
    [InlineData(260.0, 1.0)]
    [InlineData(134.0, 0.5)]
    [InlineData(2.0, 0.0)]
    [InlineData(500.0, 1.0)]
    public void TheSoundZoomFollowsTheCameraDistanceOverItsZoomRange(double distance, double share)
    {
        var camera = Read<LookCamera>("look", "camera.json");
        Assert.Equal((8.0, 260.0), (camera.DistanceM.Min, camera.DistanceM.Max));
        Assert.Equal(share, CameraModel.ZoomShareOf(distance, camera), 9);
        var model = new CameraModel(camera, 0, 0, 100, 100);
        Assert.Equal(CameraModel.ZoomShareOf(camera.DistanceM.Start, camera), model.ZoomShare, 9);
    }

    [Fact]
    public void TheLeaveKeyHandsTheCameraBackToTheObserverAtOnce()
    {
        var observer = new ObserverFocus(new WorldLookObserver { GroupSwitchS = 30, ThoughtFocusS = 12, ResumeAfterIdleS = 45, DistanceM = 48 });
        observer.Choose("inh_1");
        Assert.False(observer.Observing(10));
        observer.HandBack(10);
        Assert.Null(observer.Chosen);
        Assert.True(observer.Observing(10));

        // Also after panning: the leave key ends the idle pause.
        observer.PlayerTookCamera(20);
        Assert.False(observer.Observing(21));
        observer.HandBack(21);
        Assert.True(observer.Observing(21));
    }

    [Fact]
    public void ASpringIsANaturalPoolLyingOnTheSlopeWithItsStonesUphill()
    {
        // Ground rising with x (slope 0.5): the pool lies on the slope (no post below it, no level disc
        // floating above it), its local +x side (the stones) faces uphill; trees stay upright (D576).
        Func<double, double, double> slope = (x, _) => x / 2;
        var spring = Looks().For("spring_water");
        Assert.Equal((ObjectLookShape.Pool, ObjectLookAlign.Ground_uphill), (spring.Shape, spring.Align));
        Assert.Equal(["fresh_water", "rock", "ground"], spring.Colors);
        var placed = ObjectLooks.Place(new WorldObject("o1", "spring_water", new Position(10, 4)), spring, slope);
        Assert.Equal(slope(10, 4) + spring.LiftM, placed.Y, 6);
        var norm = Math.Sqrt(1.25);
        Assert.Equal(-0.5 / norm, placed.Up.X, 9);
        Assert.Equal(1 / norm, placed.Up.Y, 9);
        Assert.Equal(0, placed.Up.Z, 9);
        var (ax, _, _) = placed.Axes();
        Assert.True(ax.X > 0 && ax.Y > 0 && Math.Abs(ax.Z) < 1e-9, "the stones' side faces uphill");
        foreach (var (lx, lz) in new[] { (0.75, 0.0), (-0.75, 0.0), (0.0, 0.75), (0.5, -0.5) })
        {
            var p = placed.ToWorld(new Point3(lx, 0, lz));
            Assert.Equal(slope(p.X, p.Z) + spring.LiftM, p.Y, 6);
        }

        var tree = ObjectLooks.Place(new WorldObject("o2", "deciduous_tree", new Position(10, 4)), Looks().For("deciduous_tree"), slope);
        Assert.Equal(slope(10, 4), tree.Y, 9);
        Assert.Equal(new Point3(0, 1, 0), tree.Up);
    }

    [Fact]
    public void ALogLiesOnTheGroundAndTurnsOnlyWhereTheGroundIsUneven()
    {
        var wood = Looks().For("dry_wood");
        Assert.Equal((ObjectLookShape.Log, ObjectLookAlign.Ground), (wood.Shape, wood.Align));
        var log = new WorldObject("obj_7", "dry_wood", new Position(10, 4));

        // On flat ground it keeps the turn from its id and lies flat.
        var flat = ObjectLooks.Place(log, wood, (_, _) => 2);
        Assert.Equal(ObjectLooks.Hash01("obj_7", 1) * 2 * Math.PI, flat.YawRad, 9);
        Assert.Equal((0, 1, 0), (Math.Round(flat.Up.X, 9), Math.Round(flat.Up.Y, 9), Math.Round(flat.Up.Z, 9)));
        Assert.Equal(2 + wood.LiftM, flat.Y, 9);

        // On a slope both ends touch the ground.
        Func<double, double, double> slope = (x, z) => (x * 0.4) - (z * 0.2);
        var placed = ObjectLooks.Place(log, wood, slope);
        foreach (var end in new[] { -0.5, 0.5 })
        {
            var p = placed.ToWorld(new Point3(end, 0, 0));
            Assert.Equal(slope(p.X, p.Z) + wood.LiftM, p.Y, 6);
        }

        // In a small hollow running along z it turns into the hollow instead of bridging it.
        Func<double, double, double> hollow = (x, _) => Math.Abs(x - 10) * 0.8;
        var along = ObjectLooks.Place(log with { Id = "obj_9" }, wood, hollow);
        var (ax, _, _) = along.Axes();
        Assert.True(Math.Abs(ax.X) < Math.Abs(ax.Z), "the log lies along the hollow");
    }

    [Fact]
    public void ALogThatCannotLieFlatAcrossAWallStandsUprightInstead()
    {
        // A 2 m rock wall right under the log in every direction: no turn fits within the tolerance.
        var wood = Looks().For("dry_wood");
        Func<double, double, double> wall = (x, z) => (x - 10) * (z - 4) > 0 ? 2.0 : 0.0;
        var placed = ObjectLooks.Place(new WorldObject("obj_5", "dry_wood", new Position(10.01, 4.01)), wood, wall, Looks().Fit);
        Assert.Equal(new Point3(0, 1, 0), placed.Up);
        Assert.Equal(wall(10.01, 4.01) + wood.LiftM, placed.Y, 9);
        Assert.Equal((0.06, 8, 0.05), (Looks().Fit.ToleranceM, Looks().Fit.TurnTries, Looks().Fit.MinFootprintM));
    }

    [Fact]
    public void ARefreshPlacesOnlyNewObjects()
    {
        // The ground never changes: placements are kept by id, type and position (GW3 #120).
        var looks = Looks();
        var samples = 0;
        Func<double, double, double> ground = (x, z) =>
        {
            samples++;
            return (x * 0.2) + (z * 0.1);
        };
        var objects = new List<WorldObject> { new("a", "dry_wood", new Position(3, 4)), new("b", "flint", new Position(5, 6)), new("c", "deciduous_tree", new Position(7, 8)) };
        var first = looks.Layout(objects, ground);
        Assert.True(samples > 0);
        samples = 0;
        var again = looks.Layout(objects, ground);
        Assert.Equal(0, samples);
        Assert.Equal(first["dry_wood"], again["dry_wood"]);

        // One eaten, one new, one moved: only the new and the moved one are placed.
        objects.RemoveAt(1);
        objects.Add(new WorldObject("d", "flint", new Position(1, 1)));
        objects[0] = objects[0] with { Position = new Position(3.5, 4) };
        looks.Layout(objects, ground);
        var expected = 0;
        var count = 0;
        Func<double, double, double> counting = (x, z) =>
        {
            count++;
            return (x * 0.2) + (z * 0.1);
        };
        ObjectLooks.Place(objects[0], looks.For("dry_wood"), counting, looks.Fit);
        ObjectLooks.Place(objects[2], looks.For("flint"), counting, looks.Fit);
        expected = count;
        Assert.Equal(expected, samples);

        // Another ground function starts afresh.
        samples = 0;
        looks.Layout(objects, (x, z) => ground(x, z));
        Assert.True(samples > expected);
    }

    [Theory]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(2026UL)]
    public void EveryObjectOfTheStartIslandRestsOnTheDrawnGround(ulong seed)
    {
        // Objects take their height from the drawn mesh (TerrainMesher.GroundHeight); lying ones follow its
        // slope, so no part of their underside floats above or sinks below it by more than the tolerance.
        var host = SimulationHost.CreateGeneratedWorld(Content, seed, new MockDecisionPort(), "de", "start_island");
        var map = host.Map!;
        var terrain = TerrainLook.From(Read<LookTerrain>("look", "terrain.json"), Read<LookPalette>("look", "palette.json"), map.Map, host.Content.WorldGen.Presets[map.Island.PresetId]);
        double Ground(double x, double z) => TerrainMesher.GroundHeight(map.Map, terrain, x, z);
        var looks = Looks();
        Assert.InRange(looks.GroundToleranceM, 0.0, 0.1);
        var (lying, worst, upright) = (0, 0.0, 0);
        bool Flat(Simulation.Objects.WorldObject o) =>
            new[] { (0.3, 0.0), (-0.3, 0.0), (0.0, 0.3), (0.0, -0.3) }.All(d => Math.Abs(Ground(o.Position.X + d.Item1, o.Position.Z + d.Item2) - Ground(o.Position.X, o.Position.Z)) < 1e-6);
        foreach (var obj in host.Objects.All)
        {
            var look = looks.For(obj.TypeId);
            if (look.Shape == ObjectLookShape.None)
            {
                continue;
            }

            var placed = ObjectLooks.Place(obj, look, Ground, looks.Fit);
            if (look.Lies && placed.Up == new Point3(0, 1, 0) && !Flat(obj))
            {
                // Its best fit was beyond the tolerance (across a rock wall): it stands upright instead.
                upright++;
            }

            if (!look.Lies || placed.Up == new Point3(0, 1, 0))
            {
                Assert.Equal(ObjectLookRest.Centre, look.Rest);
                Assert.True(!look.Lies || Math.Abs(placed.YawRad - (ObjectLooks.Hash01(obj.Id, 1) * 2 * Math.PI)) < 1e-9 || !Flat(obj), "an upright fallback keeps its own turn");
                Assert.Equal(Ground(obj.Position.X, obj.Position.Z), placed.Y - look.LiftM, 6);
                continue;
            }

            lying++;
            Assert.InRange(Math.Abs(look.LiftM), 0, looks.GroundToleranceM);
            var (length, width) = (look.FootprintM * placed.Scale, look.HalfWidthM * placed.Scale);
            for (var k = 0; k < 16; k++)
            {
                foreach (var share in new[] { 0.0, 0.5, 1.0 })
                {
                    var a = k * Math.PI / 8;
                    var p = placed.ToWorld(new Point3(Math.Cos(a) * length * share / placed.Scale, 0, Math.Sin(a) * width * share / placed.Scale));
                    var off = p.Y - look.LiftM - Ground(p.X, p.Z);
                    worst = Math.Max(worst, Math.Abs(off));
                    Assert.True(Math.Abs(off) <= looks.GroundToleranceM, $"seed {seed}: {obj.TypeId} {obj.Id} at ({obj.Position.X:0.0}, {obj.Position.Z:0.0}) is {off:0.000} m off the ground");
                }
            }
        }

        Assert.True(lying > 20, $"seed {seed}: only {lying} lying objects");
        Assert.True(upright <= lying / 10, $"seed {seed}: {upright} of {lying} lying objects stand upright");
        Assert.True(worst > 0, "the check samples real slopes");
    }

    [Fact]
    public void TheSignatureChangesWithTheObjectSetButNotWithItsOrder()
    {
        var a = new WorldObject("a", "apple", new Position(1, 1));
        var b = new WorldObject("b", "flint", new Position(2, 2));
        Assert.Equal(ObjectLooks.Signature([a, b]), ObjectLooks.Signature([b, a]));
        Assert.NotEqual(ObjectLooks.Signature([a, b]), ObjectLooks.Signature([a]));
        Assert.NotEqual(ObjectLooks.Signature([a, b]), ObjectLooks.Signature([a, b with { Position = new Position(2, 2.5) }]));
        Assert.NotEqual(ObjectLooks.Signature([a]), ObjectLooks.Signature([a with { TypeId = "pyrite" }]));
    }

    [Fact]
    public void TheGroundRayLandsOnFlatAndSlopingGround()
    {
        var flat = GroundRay.Hit(new Point3(0, 10, 0), new Point3(0, -1, 1), (_, _) => 2, 0.5, 1000, 20);
        Assert.NotNull(flat);
        Assert.Equal(8, flat.Value.Z, 3);
        Assert.Equal(2, flat.Value.Y, 9);

        // Ground rising with x: y = x / 2; the ray goes down at 45° along x from (0, 10).
        Func<double, double, double> slope = (x, _) => x / 2;
        var hit = GroundRay.Hit(new Point3(0, 10, 0), new Point3(1, -1, 0), slope, 0.5, 1000, 24)!.Value;
        Assert.Equal(20.0 / 3, hit.X, 3);
        Assert.Equal(slope(hit.X, hit.Z), hit.Y, 9);
    }

    [Fact]
    public void ARayIntoTheSkyFindsNoGroundAndARayFromInsideAHillStandsUnderTheCamera()
    {
        Assert.Null(GroundRay.Hit(new Point3(0, 10, 0), new Point3(0, 1, 1), (_, _) => 0, 1, 500, 10));
        Assert.Null(GroundRay.Hit(new Point3(0, 10, 0), new Point3(0, -0.001, 1), (_, _) => 0, 1, 500, 10));
        Assert.Equal(new Point3(3, 50, 4), GroundRay.Hit(new Point3(3, 10, 4), new Point3(0, -1, 0), (_, _) => 50, 1, 500, 10));
    }

    [Fact]
    public void TheRosterFollowsBirthsAndDeathsInIdOrder()
    {
        var (added, removed) = FigureRoster.Diff(["inh_1", "inh_2", "inh_3"], ["inh_3", "inh_5", "inh_1", "inh_4"]);
        Assert.Equal(["inh_4", "inh_5"], added);
        Assert.Equal(["inh_2"], removed);
        var (none, gone) = FigureRoster.Diff(["a"], ["a"]);
        Assert.Empty(none);
        Assert.Empty(gone);
    }

    [Fact]
    public void TheObserverMovesOnFollowsThoughtsAndYieldsToThePlayer()
    {
        var data = new WorldLookObserver { GroupSwitchS = 30, ThoughtFocusS = 12, ResumeAfterIdleS = 45, DistanceM = 48 };
        var observer = new ObserverFocus(data);
        int[] groups = [1, 2, 3];
        Assert.Equal(1, observer.Update(0, groups));
        Assert.Equal(1, observer.Update(29, groups));
        Assert.Equal(2, observer.Update(30, groups));

        // A thought in group 3 turns the observer there for the focus time, then it moves on.
        observer.Thought(3, 40);
        Assert.Equal(3, observer.Update(41, groups));
        Assert.Equal(1, observer.Update(52, groups));

        // The player pans: the observer waits, then takes the camera back.
        Assert.True(observer.Observing(60));
        observer.PlayerTookCamera(60);
        Assert.False(observer.Observing(104));
        Assert.True(observer.Observing(105));

        // A chosen figure keeps the camera until it is let go; then the pause runs.
        observer.Choose("inh_2");
        Assert.False(observer.Observing(1000));
        observer.Release(1000);
        Assert.Null(observer.Chosen);
        Assert.False(observer.Observing(1044));
        Assert.True(observer.Observing(1045));
    }

    [Fact]
    public void TheObserverSkipsAGroupThatIsGone()
    {
        var observer = new ObserverFocus(new WorldLookObserver { GroupSwitchS = 30, ThoughtFocusS = 12, ResumeAfterIdleS = 45, DistanceM = 48 });
        Assert.Equal(2, observer.Update(0, [2, 3]));
        Assert.Equal(3, observer.Update(1, [3]));
        Assert.Null(observer.Update(2, []));
    }

    [Fact]
    public void FiguresWalkTheNextTicksAtTheNewTimeFactor()
    {
        var time = Read<WorldTimeSettings>("world", "time.json");
        var shared = new FigureShared(Read<AnimationLibrary>("animation", "library.json"), Read<LookFigures>("look", "figures.json"), time, 1);
        var normal = shared.RealSecondsPerTick;
        shared.UseSpeed(16);
        Assert.Equal(normal / 16, shared.RealSecondsPerTick, 9);
        shared.UseSpeed(0);
        Assert.True(double.IsPositiveInfinity(shared.RealSecondsPerTick));
    }
}
