using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Presentation.Tests;

/// <summary>Day pages, time line and export of the chronicle (B8 R11, A1354).</summary>
public sealed class ChronicleWindowTests
{
    private static readonly ChronicleKinds Kinds = ContractSerializer.Deserialize<ChronicleKinds>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "chronicle", "kinds.json")));

    private static readonly WorldCalendar Calendar = WorldCalendar.FromJson(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "world", "time.json")));

    [Fact]
    public void TheBookPagesByDayAndKeepsTheOrderOfADay()
    {
        var book = Book(
            ("invented", 5, "inh-000001"),
            ("built", 9, "inh-000002"),
            ("first_use", Calendar.TicksPerDay + 3, "inh-000003"),
            ("built", (4 * Calendar.TicksPerDay) + 1, "inh-000004"));

        Assert.Equal([4L, 1, 0], book.Days());
        Assert.Equal(["inh-000001", "inh-000002"], book.On(0).Select(e => e.InhabitantId));
        Assert.Equal(1L, book.Next(4, -1));
        Assert.Equal(4L, book.Next(1, +1));
        Assert.Null(book.Next(0, -1));
        Assert.Null(book.Next(4, +1));
    }

    [Fact]
    public void TheTimeLineShowsEachSeasonOfEachYearOnceOldestFirst()
    {
        var season = Calendar.TicksPerSeason;
        var book = Book(
            ("invented", 5, "inh-000001"),
            ("built", 6, "inh-000001"),
            ("built", season + 2, "inh-000002"),
            ("born", (4 * season) + 7, "inh-000003"));

        var timeline = book.Timeline();
        Assert.Equal(3, timeline.Count);
        Assert.Equal(2, timeline[0].Count);
        Assert.Equal(1L, timeline[0].Year);
        Assert.Equal(2L, timeline[^1].Year); // four seasons later: the second year
        Assert.Equal(book.DayOf(book.Entries[0]), timeline[^1].FirstDay);
    }

    [Fact]
    public void TheExportIsReadableTextAndSafeHtmlOldestFirst()
    {
        var book = Book(("invented", 5, "inh-000001"), ("built", Calendar.TicksPerDay + 3, "inh-000002"));
        string Line(ChronicleEntry e) => e.InhabitantId == "inh-000001" ? "Mara <erfand> & baute" : "Ilka baute etwas";
        string Day(long d) => $"Tag {d + 1}";

        var text = ChronicleExport.Text(book, "Meine Insel", Day, Line);
        Assert.StartsWith("Meine Insel\n===========\n\nTag 1\n- Mara <erfand> & baute\n", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("Tag 1", StringComparison.Ordinal) < text.IndexOf("Tag 2", StringComparison.Ordinal));

        var html = ChronicleExport.Html(book, "Meine <Insel>", "de", Day, Line);
        Assert.Contains("<html lang=\"de\">", html, StringComparison.Ordinal);
        Assert.Contains("<li>Mara &lt;erfand&gt; &amp; baute</li>", html, StringComparison.Ordinal);
        Assert.Contains("<title>Meine &lt;Insel&gt;</title>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileNamesStayPlain()
    {
        var at = new DateTimeOffset(2026, 11, 9, 7, 5, 0, TimeSpan.Zero);
        Assert.Equal("Meine-Insel_2026-11-09_07-05", ChronicleExport.FileStem("Meine Insel!", at));
        Assert.Equal("chronik_2026-11-09_07-05", ChronicleExport.FileStem("../..", at));
    }

    private static ChronicleBook Book(params (string Kind, long Tick, string Inhabitant)[] events)
    {
        var core = new ChronicleSystem(Kinds);
        var eventOf = Kinds.Kinds.ToDictionary(k => k.Id, k => k.Event, StringComparer.Ordinal);
        foreach (var (kind, tick, inhabitant) in events)
        {
            core.Observe(new WorldEvent(eventOf[kind], tick, inhabitant, null, null));
        }

        var book = new ChronicleBook(Kinds, Calendar);
        book.Sync(core);
        return book;
    }
}
