using GlassWorld.Presentation.World;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Presentation.Tests;

public sealed class ActivityDiaryTests
{
    private static readonly string Content = Path.Combine(TestPaths.Root, "content");

    private sealed class FakeDiary(params DiaryEntry[] entries) : IInhabitantDiary
    {
        public long Version => entries.Length == 0 ? 0 : entries[^1].Seq;

        public IReadOnlyList<DiaryEntry> Since(long version) => [.. entries.Where(e => e.Seq > version)];
    }

    [Fact]
    public void AnX16RunShowsTheNewestEntriesOfTheCoreDiaryNewestFirst()
    {
        var host = SimulationHost.CreatePlaceholderWorld(Content, 7, new MockDecisionPort(), "de");
        var diary = new ActivityDiary(5, host.World.Calendar);
        var perFrame = host.World.Calendar.Settings.MaxTicksPerFrame;
        for (var frame = 0; frame < host.World.Calendar.TicksPerDay / perFrame; frame++)
        {
            host.World.Run(perFrame);
            diary.Sync(host.Diary);
        }

        foreach (var inhabitant in host.Inhabitants.All)
        {
            var core = host.Diary.Of(inhabitant.Id);
            Assert.True(core.Count >= 5);
            Assert.Equal(Enumerable.Reverse(core).Take(5), diary.EntriesOf(inhabitant.Id));
        }
    }

    [Fact]
    public void ALanguageSwitchChangesTheLinesNotTheEntries()
    {
        var entries = new FakeDiary(
            new DiaryEntry(1, 0, "a", "inspect", "obj-1", "nettle", null),
            new DiaryEntry(2, 10, "a", "warn", "b", null, "nettle"),
            new DiaryEntry(3, 20, "b", "drink", null, null, null));
        var calendar = SimulationHost.CreatePlaceholderWorld(Content, 1, new MockDecisionPort(), "de").World.Calendar;
        var diary = new ActivityDiary(10, calendar);
        Assert.Equal(3, diary.Sync(entries));
        var before = diary.EntriesOf("a");

        var de = new Dictionary<string, string>
        {
            ["ui.activity.inspect.object"] = "untersucht {thing}",
            ["ui.activity.warn.inhabitant_topic"] = "warnt {name} vor {thing}",
            ["vocab.object.nettle"] = "Brennnessel",
        };
        var en = new Dictionary<string, string>
        {
            ["ui.activity.inspect.object"] = "inspects {thing}",
            ["ui.activity.warn.inhabitant_topic"] = "warns {name} about {thing}",
            ["vocab.object.nettle"] = "nettle",
        };
        static string Name(string id) => id == "b" ? "Bo" : id;

        Assert.Equal(["warnt Bo vor Brennnessel", "untersucht Brennnessel"], diary.Of("a", k => de.GetValueOrDefault(k, k), Name).Select(l => l.Text));
        Assert.Equal(["warns Bo about nettle", "inspects nettle"], diary.Of("a", k => en.GetValueOrDefault(k, k), Name).Select(l => l.Text));

        // The thing's type rides along, for an invention's icon (A1603); a line about another inhabitant has none.
        Assert.Equal([null, "nettle"], diary.Of("a", k => k, Name).Select(l => l.TargetType));
        Assert.Equal(before, diary.EntriesOf("a"));
        Assert.Single(diary.EntriesOf("b"));
        Assert.Empty(diary.Of("nobody", k => k, Name));
    }

    [Fact]
    public void OnlyTheNewestShownEntriesAreKeptAndAnotherDiaryStartsOver()
    {
        var diary = new ActivityDiary(2, SimulationHost.CreatePlaceholderWorld(Content, 1, new MockDecisionPort(), "de").World.Calendar);
        diary.Sync(new FakeDiary([.. Enumerable.Range(1, 4).Select(i => new DiaryEntry(i, i, "a", "act" + i, null, null, null))]));
        Assert.Equal(["act4", "act3"], diary.EntriesOf("a").Select(e => e.Action));

        var version = diary.Version;
        Assert.Equal(1, diary.Sync(new FakeDiary(new DiaryEntry(1, 1, "a", "other", null, null, null))));
        Assert.Equal(["other"], diary.EntriesOf("a").Select(e => e.Action));
        Assert.True(diary.Version > version);
    }
}
