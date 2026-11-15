using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1380 N2 (R15, D989 v2, review GW1 #27201): too many harvests in one field cell within the window make it bare;
/// while bare its places regrow with the data's factor - only for the time it is bare - and it is green again once
/// all its places have grown back, at the earliest after the data's hours. State only while there is any.
/// </summary>
public sealed class OveruseTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);

    [Fact]
    public void TooManyHarvestsMakeACellBareAndItRecoversLater()
    {
        var overuse = Content.Nature!.Overuse!;
        var (host, map, cell) = Island(overuse.BareAfter);
        var places = PlacesIn(map, cell, overuse).Take(overuse.BareAfter).ToList();

        // One harvest less than the limit: not bare.
        foreach (var place in places.Take(overuse.BareAfter - 1))
        {
            Harvest(host, place);
        }

        Assert.Empty(map.BareCells);
        Assert.NotEmpty(map.CaptureOveruse());

        // The limit: bare.
        Harvest(host, places[^1]);
        Assert.Equal([cell], map.BareCells);
        Assert.True(map.BareAt(map.CentreOf(places[0].X, places[0].Z)));

        // Green again once everything has grown back and the data's hours have passed.
        var ticksPerHour = host.World.Calendar.Settings.TicksPerHour;
        for (var day = 0; day < 120 && map.BareCells.Any(); day++)
        {
            host.World.Run(24 * ticksPerHour);
        }

        Assert.Empty(map.BareCells);
    }

    [Fact]
    public void ABareCellRegrowsSlowerOnlyForTheTimeItIsBare()
    {
        var overuse = Content.Nature!.Overuse!;
        Assert.True(overuse.BareGrowthPermille < 1000, "content: bare grows slower");

        // The same harvests in a world without overuse data and in one with it: the bare cell's places come back later.
        var (plain, plainMap, plainCell) = Island(overuse.BareAfter, withOveruse: false);
        var (bare, bareMap, bareCell) = Island(overuse.BareAfter);
        Assert.Equal(plainCell, bareCell);
        foreach (var place in PlacesIn(plainMap, plainCell, overuse).Take(overuse.BareAfter).ToList())
        {
            Harvest(plain, place);
        }

        foreach (var place in PlacesIn(bareMap, bareCell, overuse).Take(overuse.BareAfter).ToList())
        {
            Harvest(bare, place);
        }

        int Gone(WorldMapSystem map) => map.Occurrences.Count(o => o.GoneSinceTick is not null && FieldOf(map, o, overuse) == bareCell);
        long TicksUntilAllBack(SimulationHost host, WorldMapSystem map)
        {
            var start = host.World.Tick;
            while (Gone(map) > 0 && host.World.Tick - start < 400L * 24 * host.World.Calendar.Settings.TicksPerHour)
            {
                host.World.Run(host.World.Calendar.Settings.TicksPerHour);
            }

            return host.World.Tick - start;
        }

        Assert.True(TicksUntilAllBack(bare, bareMap) > TicksUntilAllBack(plain, plainMap), "bare regrows slower");
    }

    [Fact]
    public void OveruseIsStateOnlyWhileThereIsAny()
    {
        var overuse = Content.Nature!.Overuse!;
        var (host, map, cell) = Island(overuse.BareAfter);
        string Hash()
        {
            using var hasher = new State.StateHasher();
            map.WriteState(hasher);
            return hasher.Finish();
        }

        var before = Hash();
        Assert.Empty(map.CaptureOveruse());
        foreach (var place in PlacesIn(map, cell, overuse).Take(overuse.BareAfter).ToList())
        {
            Harvest(host, place);
        }

        var saved = map.CaptureOveruse();
        Assert.NotEqual(before, Hash());

        map.RestoreOveruse([]);
        Assert.Equal(before, Hash());
        map.RestoreOveruse(saved);
        Assert.Equal([cell], map.BareCells);
    }

    private static (SimulationHost Host, WorldMapSystem Map, (int X, int Z) Cell) Island(int places, bool withOveruse = true)
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 23, new SilentPort(), "de", "start_island");
        host.World.Run(1);
        var map = host.Map!;
        var overuse = Content.Nature!.Overuse!;
        if (!withOveruse)
        {
            map.RestoreOveruse([]);
            map.Overuse = null;
        }

        // The first field cell (in order) with enough places that grow there now.
        var cell = map.Occurrences.Where(o => o.GoneSinceTick is null)
            .GroupBy(o => FieldOf(map, o, overuse))
            .Where(g => g.Count() >= places)
            .Select(g => g.Key)
            .OrderBy(c => c.X).ThenBy(c => c.Z)
            .First();
        return (host, map, cell);
    }

    private static IEnumerable<Occurrence> PlacesIn(WorldMapSystem map, (int X, int Z) cell, Contracts.NatureOveruse overuse) =>
        map.Occurrences.Where(o => o.GoneSinceTick is null && FieldOf(map, o, overuse) == cell).OrderBy(o => o.ObjectId, StringComparer.Ordinal);

    private static (int X, int Z) FieldOf(WorldMapSystem map, Occurrence place, Contracts.NatureOveruse overuse)
    {
        var at = map.CentreOf(place.X, place.Z);
        return ((int)Math.Floor(at.X / overuse.CellM), (int)Math.Floor(at.Z / overuse.CellM));
    }

    private static void Harvest(SimulationHost host, Occurrence place)
    {
        host.Objects.Remove(place.ObjectId);
        host.World.Run(1);
    }

    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
