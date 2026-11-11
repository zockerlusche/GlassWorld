using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

public sealed class DreamImagesTests
{
    private static NightDreams Data() => ContractSerializer.Deserialize<LookNight>(
        File.ReadAllText(Path.Combine(TestPaths.Root, "content", "look", "night.json"))).Dreams;

    private static NightItems Items(string session, params NightItem[] items)
    {
        var list = new NightItems { ContractVersion = ContractInfo.Version, Session = session, Phase = NightItemsPhase.Night };
        foreach (var item in items)
        {
            list.Items.Add(item);
        }

        return list;
    }

    private static NightItem Item(int seq, string job, JobKind kind, JobState state, string? type, string? image = null) =>
        new() { Seq = seq, JobId = job, Kind = kind, State = state, Inhabitant = "inh-000001", ObjectType = type, Image = image };

    private static List<DreamImage> Run(DreamImages dreams, double seconds, bool reduce = false)
    {
        IReadOnlyList<DreamImage> last = [];
        for (var t = 0.0; t < seconds; t += 0.1)
        {
            last = dreams.Update(0.1, reduce);
        }

        return [.. last];
    }

    [Fact]
    public void AJobAboutAThingShowsItsSymbolOnceAndItsPictureWhenDone()
    {
        var dreams = new DreamImages(Data());
        dreams.Take(Items("s1", Item(1, "j-1", JobKind.Image, JobState.Queued, "flint"), Item(2, "j-1", JobKind.Image, JobState.Running, "flint")));
        var first = Assert.Single(Run(dreams, 0.2));
        Assert.Equal(("j-1", "flint", (string?)null), (first.JobId, first.ObjectType, first.ImageFile));

        dreams.Take(Items("s1", Item(3, "j-1", JobKind.Image, JobState.Done, "flint", "flint_7.png")));
        Run(dreams, 10);
        Assert.Equal(["j-1", "j-1"], dreams.Shown);
        Assert.Equal(3, dreams.Since);
    }

    [Fact]
    public void KindsWithoutAnAppearanceAndJobsWithoutAThingShowNothing()
    {
        var dreams = new DreamImages(Data());
        dreams.Take(Items("s1", Item(1, "j-1", JobKind.Dream, JobState.Queued, "flint"), Item(2, "j-2", JobKind.Question, JobState.Queued, null)));
        Assert.Empty(Run(dreams, 5));
        Assert.Empty(dreams.Shown);
    }

    [Fact]
    public void ImagesComeOneAfterTheOtherNeverMoreThanAllowedAndSoftly()
    {
        var data = Data();
        var dreams = new DreamImages(data);
        dreams.Take(Items("s1", [.. Enumerable.Range(1, 8).Select(i => Item(i, "j-" + i, JobKind.Question, JobState.Queued, "flint"))]));

        var starts = new List<double>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var previous = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var t = 0.0; t < 40; t += 0.05)
        {
            var now = dreams.Update(0.05, false);
            Assert.True(now.Count <= data.MaxAtOnce);
            foreach (var image in now)
            {
                if (seen.Add(image.Key))
                {
                    starts.Add(t);
                }

                // Soft: never a jump of more than a small step per frame, never above the brightness.
                Assert.True(Math.Abs(image.Alpha - previous.GetValueOrDefault(image.Key)) <= 0.12);
                Assert.True(image.Alpha <= data.Brightness + 1e-9);
                previous[image.Key] = image.Alpha;
            }
        }

        Assert.Equal(8, starts.Count);
        Assert.All(starts.Zip(starts.Skip(1)), pair => Assert.True(pair.Second - pair.First >= data.MinGapS - 0.051));

        // The protection bound (D637 R13): an image appearing and one fading are the changes of brightness;
        // with the least gap the data allows there are never more than three in any second.
        Assert.True(2 / data.MinGapS <= FlickerLimiter.AllowedChangesPerSecond);
    }

    [Fact]
    public void ANewServiceSessionStartsTheListOver()
    {
        var dreams = new DreamImages(Data());
        dreams.Take(Items("s1", Item(40, "j-1", JobKind.Question, JobState.Queued, "flint")));
        Assert.Equal(40, dreams.Since);
        // The page that reveals the new session was asked for with the old number: it is dropped and asked again.
        Assert.True(dreams.Take(Items("s2", Item(41, "j-50", JobKind.Question, JobState.Queued, "reed"))));
        Assert.Equal(0, dreams.Since);
        dreams.Take(Items("s2", Item(1, "j-9", JobKind.Question, JobState.Queued, "reed")));
        Assert.Equal(1, dreams.Since);
        Assert.Equal("j-9", Assert.Single(Run(dreams, 0.2)).JobId);
    }

    [Fact]
    public void AListFromBeforeTheServiceSwitchedToTheNightIsNotShown()
    {
        var dreams = new DreamImages(Data());
        var day = Items("s1", Item(1, "j-1", JobKind.Question, JobState.Queued, "flint"));
        day.Phase = NightItemsPhase.Day;
        Assert.False(dreams.Take(day));
        Assert.Empty(Run(dreams, 3));
        Assert.Equal(0, dreams.Since);
    }

    [Fact]
    public void ReduceMotionFadesSlowerAndPlacesStayOnScreenClearOfTheWords()
    {
        var data = Data();
        var dreams = new DreamImages(data);
        dreams.Take(Items("s1", Item(1, "j-1", JobKind.Question, JobState.Queued, "flint")));
        var slow = Assert.Single(Run(dreams, data.FadeInS, reduce: true));
        Assert.True(slow.Alpha < data.Brightness * 0.75);

        foreach (var key in Enumerable.Range(0, 500).Select(i => "job-" + i + "|symbol"))
        {
            var (x, y) = DreamImages.Place(key);
            Assert.InRange(x, 0.1, 0.9);
            Assert.InRange(y, 0.15, 0.85);
            Assert.False(y is > 0.42 and < 0.58 && x is > 0.3 and < 0.7);
            Assert.Equal((x, y), DreamImages.Place(key));
        }
    }
}
