using System.Globalization;
using System.Net;
using System.Text;

namespace GlassWorld.Presentation.World;

/// <summary>A season of the chronicle's time line: when, and how many entries it holds.</summary>
/// <param name="Year">Year since the founding, from 1.</param>
/// <param name="SeasonKey">Season text key (<c>time.season.*</c>).</param>
/// <param name="FirstDay">First game day of the season that has an entry (absolute day number).</param>
/// <param name="Count">Entries in this season.</param>
public sealed record ChronicleSeason(long Year, string SeasonKey, long FirstDay, int Count);

/// <summary>
/// Day pages and the time line of the chronicle (B8 R11, A1354): the window shows one game day at a
/// time, pages to the day before or after that has entries, and jumps to a season of a year. Days are
/// absolute game days (tick / ticks per day), so they stay the same after a save game is loaded.
/// </summary>
public sealed partial class ChronicleBook
{
    /// <summary>The absolute game day of an entry.</summary>
    public long DayOf(ChronicleEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Tick / calendar.TicksPerDay;
    }

    /// <summary>Days with entries, newest first.</summary>
    public IReadOnlyList<long> Days() => [.. Entries.Select(DayOf).Distinct().OrderDescending()];

    /// <summary>The entries of a day, in the order they happened.</summary>
    public IReadOnlyList<ChronicleEntry> On(long day) => [.. Entries.Where(e => DayOf(e) == day).OrderBy(e => e.Tick)];

    /// <summary>The day with entries before (<paramref name="step"/> -1) or after (+1) a day, or null at the end.</summary>
    public long? Next(long day, int step)
    {
        var days = Days();
        return step < 0 ? days.Where(d => d < day).Cast<long?>().FirstOrDefault() : days.Where(d => d > day).Cast<long?>().LastOrDefault();
    }

    /// <summary>The seasons that hold entries, oldest first (the time line over seasons and years).</summary>
    public IReadOnlyList<ChronicleSeason> Timeline() =>
        [.. Entries
            .GroupBy(e => (e.Year, e.SeasonKey))
            .Select(g => new ChronicleSeason(g.Key.Year, g.Key.SeasonKey, g.Min(DayOf), g.Count()))
            .OrderBy(s => s.FirstDay)];
}

/// <summary>
/// The chronicle as a readable file (B8 R11, F5234): plain text and a standalone HTML page, oldest day
/// first. The caller passes the lines already in the player's language (the window's own text), so
/// this stays free of translation and Godot. Nothing is sent anywhere: the files are written locally.
/// </summary>
public static class ChronicleExport
{
    /// <summary>The chronicle as plain text.</summary>
    /// <param name="book">The chronicle.</param>
    /// <param name="title">Heading (e.g. the world's name).</param>
    /// <param name="dayTitle">Text of a day heading (absolute day).</param>
    /// <param name="line">Text of one entry.</param>
    public static string Text(ChronicleBook book, string title, Func<long, string> dayTitle, Func<ChronicleEntry, string> line)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(dayTitle);
        ArgumentNullException.ThrowIfNull(line);
        var text = new StringBuilder();
        text.Append(title).Append('\n').Append(new string('=', Math.Max(3, title.Length))).Append("\n\n");
        foreach (var (day, entries) in ByDay(book))
        {
            text.Append(dayTitle(day)).Append('\n');
            foreach (var entry in entries)
            {
                text.Append("- ").Append(line(entry)).Append('\n');
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The chronicle as one standalone HTML page (no scripts, no online resources).</summary>
    /// <param name="book">The chronicle.</param>
    /// <param name="title">Page title (e.g. the world's name).</param>
    /// <param name="locale">Language of the page (<c>de</c>, <c>en</c>).</param>
    /// <param name="dayTitle">Text of a day heading (absolute day).</param>
    /// <param name="line">Text of one entry.</param>
    public static string Html(ChronicleBook book, string title, string locale, Func<long, string> dayTitle, Func<ChronicleEntry, string> line)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(dayTitle);
        ArgumentNullException.ThrowIfNull(line);
        static string E(string s) => WebUtility.HtmlEncode(s);
        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"").Append(E(locale)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n<title>")
            .Append(E(title)).Append("</title>\n<style>body{font-family:sans-serif;max-width:46rem;margin:2rem auto;padding:0 1rem;line-height:1.5;color:#2b2420;background:#f3ead8}h1{font-size:1.6rem}h2{font-size:1.1rem;margin-top:1.6rem;border-bottom:1px solid #c9b896}li{margin:.2rem 0}</style>\n</head>\n<body>\n<h1>")
            .Append(E(title)).Append("</h1>\n");
        foreach (var (day, entries) in ByDay(book))
        {
            html.Append("<h2>").Append(E(dayTitle(day))).Append("</h2>\n<ul>\n");
            foreach (var entry in entries)
            {
                html.Append("<li>").Append(E(line(entry))).Append("</li>\n");
            }

            html.Append("</ul>\n");
        }

        return html.Append("</body>\n</html>\n").ToString();
    }

    /// <summary>The entries grouped by day in one pass, oldest day first, each day in the order it happened (review GW1 #136).</summary>
    private static IEnumerable<(long Day, IReadOnlyList<ChronicleEntry> Entries)> ByDay(ChronicleBook book) =>
        book.Entries
            .GroupBy(book.DayOf)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, (IReadOnlyList<ChronicleEntry>)[.. g.OrderBy(e => e.Tick)]));

    /// <summary>A file name stem from a world name: letters, digits and dashes only.</summary>
    public static string FileStem(string worldName, DateTimeOffset now)
    {
        var clean = new string([.. (worldName ?? string.Empty).Select(c => char.IsLetterOrDigit(c) ? c : '-')]).Trim('-');
        return (clean.Length == 0 ? "chronik" : clean) + "_" + now.ToString("yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture);
    }
}
