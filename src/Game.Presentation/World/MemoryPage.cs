using System.Net;
using System.Text;
using GlassWorld.Simulation.Player;

namespace GlassWorld.Presentation.World;

/// <summary>One item of a memory page: a chronicle event the inhabitant took part in, or a line of a conversation.</summary>
/// <param name="Tick">When it was.</param>
/// <param name="Event">The chronicle event, or null for a conversation line.</param>
/// <param name="Talk">The conversation line, or null for an event.</param>
public sealed record MemoryItem(long Tick, ChronicleEntry? Event, ConversationLineState? Talk);

/// <summary>
/// The memory page of an inhabitant (B8 R16, B2518, Marcel W1603): its history put together from what the world kept -
/// the chronicle events it took part in (inventions, first buildings, discoveries, meetings ...; as the one acting or
/// as the one it was about) and its conversations with the player as they were said. Nothing is made up; system texts
/// come in the player's language, the inhabitant's words in the language they were said in. Godot-free.
/// </summary>
public static class MemoryPage
{
    /// <summary>The items of the inhabitant's page, oldest first; events and talk at the same tick keep their order.</summary>
    /// <param name="inhabitantId">Whose page.</param>
    /// <param name="chronicle">The chronicle entries (in the order they happened).</param>
    /// <param name="talk">The inhabitant's conversation history (oldest first).</param>
    public static IReadOnlyList<MemoryItem> Items(string inhabitantId, IEnumerable<ChronicleEntry> chronicle, IEnumerable<ConversationLineState> talk)
    {
        ArgumentException.ThrowIfNullOrEmpty(inhabitantId);
        ArgumentNullException.ThrowIfNull(chronicle);
        ArgumentNullException.ThrowIfNull(talk);
        var items = chronicle
            .Where(e => e.InhabitantId == inhabitantId || e.TargetId == inhabitantId)
            .Select((e, i) => (Item: new MemoryItem(e.Tick, e, null), Order: (0, i)))
            .Concat(talk.Select((t, i) => (Item: new MemoryItem(t.Tick, null, t), Order: (1, i))));
        return [.. items.OrderBy(x => x.Item.Tick).ThenBy(x => x.Order).Select(x => x.Item)];
    }

    /// <summary>The text key of a season (<c>time.season.*</c>) for the day headings, as the chronicle has it.</summary>
    public static string SeasonKey(string seasonId) => "time.season." + seasonId;

    /// <summary>The page as plain text, grouped by day.</summary>
    /// <param name="items">The page's items (<see cref="Items"/>).</param>
    /// <param name="title">Heading (e.g. "Erinnerungen an Hano").</param>
    /// <param name="dayOf">Absolute day of a tick.</param>
    /// <param name="dayTitle">Text of a day heading (absolute day).</param>
    /// <param name="line">Text of one item.</param>
    public static string Text(IReadOnlyList<MemoryItem> items, string title, Func<long, long> dayOf, Func<long, string> dayTitle, Func<MemoryItem, string> line)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(dayOf);
        ArgumentNullException.ThrowIfNull(dayTitle);
        ArgumentNullException.ThrowIfNull(line);
        var text = new StringBuilder();
        text.Append(title).Append('\n').Append(new string('=', Math.Max(3, title.Length))).Append("\n\n");
        foreach (var (day, dayItems) in ByDay(items, dayOf))
        {
            text.Append(dayTitle(day)).Append('\n');
            foreach (var item in dayItems)
            {
                text.Append("- ").Append(line(item)).Append('\n');
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The page as one standalone HTML page (no scripts, no online resources), like the chronicle export.</summary>
    /// <param name="items">The page's items (<see cref="Items"/>).</param>
    /// <param name="title">Page title.</param>
    /// <param name="locale">Language of the page (<c>de</c>, <c>en</c>).</param>
    /// <param name="dayOf">Absolute day of a tick.</param>
    /// <param name="dayTitle">Text of a day heading (absolute day).</param>
    /// <param name="line">Text of one item.</param>
    public static string Html(IReadOnlyList<MemoryItem> items, string title, string locale, Func<long, long> dayOf, Func<long, string> dayTitle, Func<MemoryItem, string> line)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(dayOf);
        ArgumentNullException.ThrowIfNull(dayTitle);
        ArgumentNullException.ThrowIfNull(line);
        static string E(string s) => WebUtility.HtmlEncode(s);
        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"").Append(E(locale)).Append("\">\n<head>\n<meta charset=\"utf-8\">\n<title>")
            .Append(E(title)).Append("</title>\n<style>body{font-family:sans-serif;max-width:46rem;margin:2rem auto;padding:0 1rem;line-height:1.5;color:#2b2420;background:#f3ead8}h1{font-size:1.6rem}h2{font-size:1.1rem;margin-top:1.6rem}li{margin:.2rem 0}</style>\n</head>\n<body>\n<h1>")
            .Append(E(title)).Append("</h1>\n");
        foreach (var (day, dayItems) in ByDay(items, dayOf))
        {
            html.Append("<h2>").Append(E(dayTitle(day))).Append("</h2>\n<ul>\n");
            foreach (var item in dayItems)
            {
                html.Append("<li>").Append(E(line(item))).Append("</li>\n");
            }

            html.Append("</ul>\n");
        }

        return html.Append("</body>\n</html>\n").ToString();
    }

    /// <summary>The items grouped by day in one pass, oldest day first, each day in the order it happened.</summary>
    private static IEnumerable<(long Day, IReadOnlyList<MemoryItem> Items)> ByDay(IReadOnlyList<MemoryItem> items, Func<long, long> dayOf)
    {
        var day = long.MinValue;
        var current = new List<MemoryItem>();
        foreach (var item in items)
        {
            var d = dayOf(item.Tick);
            if (d != day && current.Count > 0)
            {
                yield return (day, current);
                current = [];
            }

            day = d;
            current.Add(item);
        }

        if (current.Count > 0)
        {
            yield return (day, current);
        }
    }
}
