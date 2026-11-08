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
    public void APoolRestsLevelOnTheHighestGroundOfItsFootprint()
    {
        // Ground rising with x (slope 0.5): the pool's level surface lies at the highest point under it, so
        // the slope never buries it; centre-resting objects stand on the ground under their position.
        Func<double, double, double> slope = (x, _) => x / 2;
        var spring = Looks().For("spring_water");
        Assert.Equal((ObjectLookShape.Pool, ObjectLookRest.Highest), (spring.Shape, spring.Rest));
        var pool = new ObjectLook("pool", ObjectLookShape.Pool, ["fresh_water"], 1.0, 0, 0.03, ObjectLookRest.Highest, 0.75);
        var placed = ObjectLooks.Place(new WorldObject("o1", "spring_water", new Position(10, 4)), pool, slope);
        Assert.Equal(slope(10.75, 4) + 0.03, placed.Y, 9);
        var low = ObjectLooks.Place(new WorldObject("o1", "spring_water", new Position(10, 4)), pool with { Rest = ObjectLookRest.Lowest }, slope);
        Assert.Equal(slope(9.25, 4) + 0.03, low.Y, 9);
        var tree = ObjectLooks.Place(new WorldObject("o2", "deciduous_tree", new Position(10, 4)), Looks().For("deciduous_tree"), slope);
        Assert.Equal(slope(10, 4), tree.Y, 9);
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
