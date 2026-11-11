using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// The reveal map of the cloud cover (B2-6 R25): it takes only what the core revealed since its version,
/// clears new cells slowly over real time within a per-frame budget, shows a first or loaded view at once
/// and never covers a cell again.
/// </summary>
public sealed class RevealMapTests
{
    private const int Width = 8;
    private const int Depth = 6;

    [Fact]
    public void TheFirstViewIsClearAtOnceAndTheRestLiesUnderCloud()
    {
        var view = new View();
        view.Reveal(0, 1, Width);
        var map = new RevealMap(Width, Depth, clearSeconds: 3, cellsPerFrame: 100);
        Assert.Equal(0, map.At(0, 0));

        map.Follow(view.Version, view.Since);

        // A new world and a loaded one: version 0 gives everything, and none of it is shown clearing.
        Assert.Equal([0], view.Asked);
        Assert.Equal(3, map.Version);
        Assert.Equal((0, 0), (map.Waiting, map.Clearing));
        Assert.Equal(RevealMap.Clear, map.At(0, 0));
        Assert.Equal(RevealMap.Clear, map.At(1, 0));
        Assert.Equal(RevealMap.Clear, map.At(0, 1));
        Assert.Equal(0, map.At(2, 0));
        Assert.Equal(3.0 / (Width * Depth), map.SeenShare, 9);
        Assert.True(map.Advance(0));
        Assert.False(map.Advance(1));
    }

    [Fact]
    public void LaterCellsAreAskedByVersionAndClearSlowlyOverRealTime()
    {
        var view = new View();
        view.Reveal(0);
        var map = new RevealMap(Width, Depth, clearSeconds: 4, cellsPerFrame: 100);
        map.Follow(view.Version, view.Since);
        map.Advance(0);

        // The same version asks nothing.
        map.Follow(view.Version, view.Since);
        Assert.Single(view.Asked);

        view.Reveal(5, 6);
        map.Follow(view.Version, view.Since);
        Assert.Equal([0, 1], view.Asked);
        Assert.Equal(2, map.Waiting);
        Assert.Equal(0, map.At(5, 0));

        // The frame they start in shows them still covered; then they clear by the share of the time.
        Assert.False(map.Advance(0.5));
        Assert.Equal((0, 2), (map.Waiting, map.Clearing));
        Assert.True(map.Advance(1));
        Assert.Equal(63, map.At(5, 0));
        Assert.True(map.Advance(1));
        Assert.Equal(127, map.At(6, 0));

        // A pause (0 seconds) holds the clearing.
        Assert.False(map.Advance(0));
        Assert.Equal(127, map.At(5, 0));
        Assert.True(map.Advance(2));
        Assert.Equal(RevealMap.Clear, map.At(5, 0));
        Assert.Equal(0, map.Clearing);
        Assert.False(map.Advance(1));
    }

    [Fact]
    public void AtMostTheBudgetOfCellsStartsPerFrameAndEveryCellGetsItsFullTime()
    {
        var view = new View();
        view.Reveal(0);
        var map = new RevealMap(Width, Depth, clearSeconds: 1, cellsPerFrame: 2);
        map.Follow(view.Version, view.Since);
        view.Reveal(10, 11, 12, 13, 14);
        map.Follow(view.Version, view.Since);

        map.Advance(0.25);
        Assert.Equal((3, 2), (map.Waiting, map.Clearing));
        map.Advance(0.25);
        Assert.Equal((1, 4), (map.Waiting, map.Clearing));

        // The first two are a frame ahead of the next two; the last one has not started yet.
        Assert.Equal(63, map.At(2, 1));
        Assert.Equal(0, map.At(4, 1));
        Assert.Equal(0, map.At(6, 1));
        map.Advance(0.25);
        Assert.Equal((0, 5), (map.Waiting, map.Clearing));
        Assert.True(map.At(2, 1) > map.At(4, 1) && map.At(4, 1) > map.At(6, 1));
        map.Advance(1);
        Assert.Equal(RevealMap.Clear, map.At(6, 1));
        Assert.Equal(0, map.Clearing);
    }

    [Fact]
    public void ACellNeverGoesBackUnderCloud()
    {
        var view = new View();
        view.Reveal(0);
        var map = new RevealMap(Width, Depth, clearSeconds: 2, cellsPerFrame: 100);
        map.Follow(view.Version, view.Since);

        // The core never names a cell twice; if it did, a clearing or clear cell keeps what it has.
        view.Reveal(3, 0, 3);
        map.Follow(view.Version, view.Since);
        Assert.Equal(1, map.Waiting);
        Assert.Equal(RevealMap.Clear, map.At(0, 0));

        var before = new byte[Width * Depth];
        var after = new byte[Width * Depth];
        var random = new Random(7);
        for (var frame = 0; frame < 200; frame++)
        {
            map.CopyTo(before);
            if (frame % 5 == 0)
            {
                view.Reveal(random.Next(Width * Depth));
                map.Follow(view.Version, view.Since);
            }

            map.Advance(random.NextDouble() * 0.2);
            map.CopyTo(after);
            Assert.All(Enumerable.Range(0, before.Length), i => Assert.True(after[i] >= before[i]));
        }

        map.ShowShare(100);
        map.CopyTo(before);
        map.ShowShare(0);
        map.CopyTo(after);
        Assert.Equal(before, after);
    }

    [Fact]
    public void ASmallerVersionIsAnotherStateOfTheWorldAndShowsAtOnce()
    {
        var view = new View();
        view.Reveal(0, 1, 2, 3);
        var map = new RevealMap(Width, Depth, clearSeconds: 3, cellsPerFrame: 100);
        map.Follow(view.Version, view.Since);
        map.Advance(0);

        // A state loaded in place: its view counts from 0 again and none of it clears slowly.
        var loaded = new View();
        loaded.Reveal(20, 21);
        map.Follow(loaded.Version, loaded.Since);
        Assert.Equal([0], loaded.Asked);
        Assert.Equal(2, map.Version);
        Assert.Equal((0, 0), (map.Waiting, map.Clearing));
        Assert.Equal(RevealMap.Clear, map.At(20 % Width, 20 / Width));
        Assert.Equal(0, map.At(0, 0));
        Assert.True(map.Advance(0));

        // What comes after it clears slowly again.
        loaded.Reveal(22);
        map.Follow(loaded.Version, loaded.Since);
        Assert.Equal(1, map.Waiting);
    }

    [Fact]
    public void WithoutAClearingTimeNewCellsShowAtOnce()
    {
        var view = new View();
        view.Reveal(0);
        var map = new RevealMap(Width, Depth, clearSeconds: 0, cellsPerFrame: 1);
        map.Follow(view.Version, view.Since);
        view.Reveal(1, 2, 3);
        map.Follow(view.Version, view.Since);
        Assert.Equal(0, map.Waiting);
        Assert.Equal(RevealMap.Clear, map.At(3, 0));
    }

    [Fact]
    public void OpenSeaBeyondTheMarginIsNeverCoveredAndDoesNotCount()
    {
        // Land is one cell in the middle of a row of 9; margin 2 keeps two cells of coast on each side.
        var open = RevealMap.OpenSea(9, 1, (x, _) => x == 4, 2);
        Assert.Equal([true, true, false, false, false, false, false, true, true], open);

        // Diagonals count a little more than straight steps (chamfer 3-4): margin 1 leaves the corners open.
        var square = RevealMap.OpenSea(3, 3, (x, z) => x == 1 && z == 1, 1);
        Assert.Equal([true, false, true, false, false, false, true, false, true], square);

        var map = new RevealMap(9, 1, clearSeconds: 3, cellsPerFrame: 10, open);
        Assert.Equal(RevealMap.Clear, map.At(0, 0));
        Assert.Equal(0, map.At(4, 0));
        Assert.Equal(0, map.SeenShare);
        var view = new View();
        view.Reveal(0, 4);
        map.Follow(view.Version, view.Since);
        Assert.Equal(1.0 / 5, map.SeenShare, 9);

        // Open cells the core reveals later are clear already: nothing waits for them.
        view.Reveal(8, 5);
        map.Follow(view.Version, view.Since);
        Assert.Equal(1, map.Waiting);
        Assert.Throws<ArgumentException>(() => new RevealMap(9, 1, 3, 10, new bool[4]));
    }

    [Fact]
    public void AForcedShareClearsTheNearestCellsAroundWhatIsSeenAndSettles()
    {
        var view = new View();
        view.Reveal((2 * Width) + 2);
        var map = new RevealMap(Width, Depth, clearSeconds: 3, cellsPerFrame: 1);
        map.Follow(view.Version, view.Since);
        view.Reveal((2 * Width) + 3);
        map.Follow(view.Version, view.Since);
        Assert.Equal(1, map.Waiting);

        map.ShowShare(250);

        // 12 of 48 cells, settled: the two seen ones and the ten nearest to their middle.
        Assert.Equal((0, 0), (map.Waiting, map.Clearing));
        Assert.Equal(0.25, map.SeenShare, 9);
        Assert.Equal(RevealMap.Clear, map.At(3, 2));
        Assert.Equal(RevealMap.Clear, map.At(2, 1));
        Assert.Equal(RevealMap.Clear, map.At(3, 3));
        Assert.Equal(0, map.At(7, 5));
        Assert.True(map.Advance(0));

        // The core's version is untouched, and what it reveals later inside the patch is not taken twice.
        Assert.Equal(2, map.Version);
        view.Reveal((1 * Width) + 2, 47);
        map.Follow(view.Version, view.Since);
        Assert.Equal(1, map.Waiting);

        map.ShowShare(1000);
        Assert.Equal(1, map.SeenShare, 9);
        Assert.Equal(RevealMap.Clear, map.At(7, 5));
    }

    [Fact]
    public void TheCloudSetShowsOneViewEarlyAndLaterAndTheDataHasALightCover()
    {
        var shots = Read<LookShots>("shots_clouds.json");
        var none = new HashSet<string>(StringComparer.Ordinal);
        var plan = ShotPlan.Build(shots, none, none);

        Assert.Equal(["island_cover_early_perspective_day_1920x1080.png", "island_cover_later_perspective_day_1920x1080.png"], plan.Select(s => s.FileName(1920, 1080)));
        var (early, later) = (plan[0].Point, plan[1].Point);
        Assert.Equal((early.TargetXPermille, early.TargetZPermille, early.YawDeg, early.DistanceM), (later.TargetXPermille, later.TargetZPermille, later.YawDeg, later.DistanceM));
        Assert.Null(early.RevealedPermille);
        Assert.InRange(later.RevealedPermille ?? 0, 500, 1000);

        // The cover's colours are palette colours, light ones (never black, R25).
        var cover = Read<LookNature>("nature.json").Cover;
        var palette = Read<LookPalette>("palette.json").Colors.ToDictionary(c => c.Id, c => c.Color, StringComparer.Ordinal);
        foreach (var id in new[] { cover.Color, cover.ShadeColor })
        {
            var rgb = Convert.ToInt32(palette[id].TrimStart('#'), 16);
            Assert.All(new[] { rgb >> 16, (rgb >> 8) & 0xFF, rgb & 0xFF }, part => Assert.InRange(part, 0xC0, 0xFF));
        }
    }

    private static T Read<T>(string file)
        where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", file)));

    /// <summary>A stand-in for the core's view: cells in the order they were revealed.</summary>
    private sealed class View
    {
        private readonly List<int> _revealed = [];

        public int Version => _revealed.Count;

        public List<int> Asked { get; } = [];

        public void Reveal(params int[] cells) => _revealed.AddRange(cells);

        public int[] Since(int version)
        {
            Asked.Add(version);
            return [.. _revealed.Skip(version)];
        }
    }
}
