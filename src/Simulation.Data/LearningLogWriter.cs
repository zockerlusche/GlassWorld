using System.Globalization;
using System.Text;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Simulation.Data;

/// <summary>
/// Writes the learning log (A1620, B2685 part B): one Markdown file per finished game day in the world's folder,
/// in the player's language from the text keys of content/i18n (KR4). A day already written is never overwritten;
/// the lines it takes are forgotten by the core's log. Read only for Marcel and the TL - nothing reads it back.
/// </summary>
public static class LearningLogWriter
{
    private const string KnowledgeGained = "knowledge_gained";

    /// <summary>
    /// Writes every finished day the log holds (all lines before today) into <paramref name="worldFolder"/>/folder
    /// and returns the files written. Nothing without the log (data switched off) or without lines.
    /// </summary>
    public static IReadOnlyList<string> WriteFinishedDays(SimulationHost host, string worldFolder, string contentDirectory)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrEmpty(worldFolder);
        if (host.LearningLog is not { } log || host.Content.LearningLog is not { } settings)
        {
            return [];
        }

        var ticksPerDay = host.World.Calendar.TicksPerDay;
        var taken = log.TakeBefore(host.World.Tick / ticksPerDay * ticksPerDay);
        if (taken.Count == 0)
        {
            return [];
        }

        var texts = LoadTexts(contentDirectory, host.DisplayLocale);
        var keys = settings.Kinds.ToDictionary(k => k.Event, k => k.TextKey, StringComparer.Ordinal);
        var folder = Path.Combine(worldFolder, settings.Folder);
        Directory.CreateDirectory(folder);
        var written = new List<string>();
        foreach (var day in taken.GroupBy(e => (e.Tick / ticksPerDay) + 1))
        {
            var path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"day-{day.Key:0000}.md"));
            if (File.Exists(path))
            {
                continue;
            }

            File.WriteAllText(path, Render(host, day.Key, [.. day], keys, texts), new UTF8Encoding(false));
            written.Add(path);
        }

        return written;
    }

    /// <summary>The Markdown of one day: per inhabitant (by id) its lines in time order, or the 'nothing new' line.</summary>
    public static string Render(SimulationHost host, long day, IReadOnlyList<LearningEntry> entries, IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> texts)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(texts);
        var text = new StringBuilder();
        text.Append("# ").AppendLine(Text(texts, "msg.learnlog.title").Replace("{day}", day.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)).AppendLine();
        foreach (var inhabitant in host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            text.Append("## ").AppendLine(NameOf(host, inhabitant.Id)).AppendLine();
            var mine = entries.Where(e => e.InhabitantId == inhabitant.Id).OrderBy(e => e.Tick).ToList();
            if (mine.Count == 0)
            {
                text.Append("- ").AppendLine(Text(texts, "msg.learnlog.nothing"));
            }

            // What was learned about one thing at one moment from one source is one line ("ist nicht trinkbar, ist
            // nicht salzig"), so a look at a thing does not fill the page.
            for (var i = 0; i < mine.Count; i++)
            {
                var entry = mine[i];
                var same = new List<LearningEntry> { entry };
                while (entry.Kind == KnowledgeGained && i + 1 < mine.Count && mine[i + 1] is var next
                    && next.Kind == KnowledgeGained && next.Tick == entry.Tick && next.TypeId == entry.TypeId && next.Source == entry.Source && next.From == entry.From)
                {
                    same.Add(next);
                    i++;
                }

                text.Append("- ").AppendLine(Line(host, entry, same, keys, texts));
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static string Line(SimulationHost host, LearningEntry entry, IReadOnlyList<LearningEntry> same, IReadOnlyDictionary<string, string> keys, IReadOnlyDictionary<string, string> texts)
    {
        var key = keys.GetValueOrDefault(entry.Kind, "msg.learnlog." + entry.Kind);
        if (entry.Kind == KnowledgeGained && entry.Source is { } source)
        {
            key += "." + source;
        }

        var time = host.World.Calendar.At(entry.Tick);
        // '{is} {property}' once per fact of the line, joined (the text keys keep the two placeholders together).
        var facts = string.Join(", ", same.Where(e => e.Property is not null).Select(e => Fact(texts, e.Property!, e.Value != false)));
        return Text(texts, key)
            .Replace("{time}", string.Create(CultureInfo.InvariantCulture, $"{time.Hour:00}:{time.Minute:00}"), StringComparison.Ordinal)
            .Replace("{thing}", entry.TypeId is { } type ? ThingName(host, texts, type) : string.Empty, StringComparison.Ordinal)
            .Replace("{is} {property}", facts, StringComparison.Ordinal)
            .Replace("{word}", entry.Detail ?? string.Empty, StringComparison.Ordinal)
            .Replace("{from}", entry.From is { } from ? NameOf(host, from) : "?", StringComparison.Ordinal)
            .Replace("{other}", entry.OtherId is { } other ? NameOf(host, other) : string.Empty, StringComparison.Ordinal)
            .Replace("{need}", entry.Action is { } need ? Vocab(texts, "need", need) : string.Empty, StringComparison.Ordinal)
            .Replace("{action}", entry.Action is { } action ? texts.GetValueOrDefault("vocab.process." + action) ?? Vocab(texts, "action", action) : string.Empty, StringComparison.Ordinal);
    }

    private static string ThingName(SimulationHost host, IReadOnlyDictionary<string, string> texts, string typeId) =>
        texts.GetValueOrDefault("vocab.object." + typeId)
        ?? (host.Inventions.Invention(typeId) is { } invented ? (host.DisplayLocale == "en" ? invented.Name.En : invented.Name.De) : typeId);

    private static string NameOf(SimulationHost host, string inhabitantId) =>
        host.Inhabitants.TryGet(inhabitantId, out var who) && who.Name is { } name ? name : inhabitantId;

    private static string Vocab(IReadOnlyDictionary<string, string> texts, string kind, string id) =>
        texts.GetValueOrDefault("vocab." + kind + "." + id) ?? id;

    private static string Text(IReadOnlyDictionary<string, string> texts, string key) => texts.GetValueOrDefault(key) ?? key;

    /// <summary>
    /// One fact: 'is' or 'is not' and the property's label in lower case ('ist essbar'). A label that is a noun or a
    /// verb has its own phrase per language and truth (<c>msg.learnlog.fact.&lt;property&gt;.true|false</c>, e.g.
    /// 'ist Stein', 'schlägt keine Funken'), so the log never reads 'ist stein' or 'ist schlägt funken'.
    /// </summary>
    private static string Fact(IReadOnlyDictionary<string, string> texts, string property, bool value) =>
        texts.GetValueOrDefault("msg.learnlog.fact." + property + (value ? ".true" : ".false"))
        ?? Text(texts, value ? "msg.learnlog.is.true" : "msg.learnlog.is.false") + " " + Lower(Vocab(texts, "property", property));

    private static string Lower(string word) =>
        word.Length > 0 && char.IsUpper(word[0]) ? char.ToLower(word[0], CultureInfo.InvariantCulture) + word[1..] : word;

    private static Dictionary<string, string> LoadTexts(string contentDirectory, string locale)
    {
        var catalog = ContractSerializer.Deserialize<I18n>(File.ReadAllText(Path.Combine(contentDirectory, "i18n", locale + ".json")));
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in catalog.Entries)
        {
            texts[entry.Key] = entry.Text;
        }

        return texts;
    }
}
