using System.Text;

namespace GlassWorld.Presentation.Ui;

/// <summary>
/// Fills named placeholders such as <c>{name}</c> in a translated text (D366 section 12: never glue
/// sentences from parts, so word order can differ per language). Unknown placeholders stay visible.
/// </summary>
public static class TextFill
{
    /// <summary>Replaces every <c>{key}</c> with its value.</summary>
    public static string Fill(string template, IReadOnlyDictionary<string, string> args)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(args);
        var result = new StringBuilder(template.Length);
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            var close = open < 0 ? -1 : template.IndexOf('}', open + 1);
            if (open < 0 || close < 0)
            {
                result.Append(template, i, template.Length - i);
                break;
            }

            result.Append(template, i, open - i);
            var key = template[(open + 1)..close];
            result.Append(args.TryGetValue(key, out var value) ? value : template[open..(close + 1)]);
            i = close + 1;
        }

        return result.ToString();
    }
}

/// <summary>Stable sorting for UI tables (D366 section 7): equal keys keep their order.</summary>
public static class TableSort
{
    /// <summary>Rows sorted by a key, ascending or descending, stable.</summary>
    public static IReadOnlyList<T> By<T, TKey>(IEnumerable<T> rows, Func<T, TKey> key, bool descending, IComparer<TKey>? comparer = null) =>
        descending ? [.. rows.OrderByDescending(key, comparer)] : [.. rows.OrderBy(key, comparer)];
}
