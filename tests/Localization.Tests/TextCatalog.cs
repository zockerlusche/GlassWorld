using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlassWorld.Localization.Tests;

/// <summary>One language file from content/i18n (ADR-0007), read as ordered key/text pairs.</summary>
public sealed partial class TextCatalog
{
    private TextCatalog(string locale, IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        Locale = locale;
        Entries = entries;
    }

    /// <summary>Locale declared in the file ("de" or "en").</summary>
    public string Locale { get; }

    /// <summary>Entries in file order; duplicates are kept so tests can report them.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Entries { get; }

    /// <summary>All keys of this catalog.</summary>
    public IEnumerable<string> Keys => Entries.Select(e => e.Key);

    /// <summary>Reads a catalog file.</summary>
    public static TextCatalog Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var entries = root.GetProperty("entries").EnumerateArray()
            .Select(e => new KeyValuePair<string, string>(e.GetProperty("key").GetString()!, e.GetProperty("text").GetString()!))
            .ToList();
        return new TextCatalog(root.GetProperty("locale").GetString()!, entries);
    }

    /// <summary>Named placeholders such as {name} used in a text, sorted and distinct.</summary>
    public static IReadOnlyList<string> Placeholders(string text) =>
        [.. PlaceholderPattern().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"\{([a-z][a-z0-9_]*)\}")]
    private static partial Regex PlaceholderPattern();
}
