using System.Text;
using GlassWorld.Contracts;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Ui;

/// <summary>What the inhabitants window shows (B2749): all, the pinned favourites, who asks, who knocks, who is weak.</summary>
public enum InhabitantFilter
{
    /// <summary>Every inhabitant.</summary>
    All,

    /// <summary>The player's pinned favourites.</summary>
    Pinned,

    /// <summary>Who puzzles over an open question.</summary>
    Asking,

    /// <summary>Who knocks on the glass (an open message).</summary>
    Knocking,

    /// <summary>Whose will to live is in the bad range of the bar rule.</summary>
    Weak,
}

/// <summary>
/// Search and filter of the inhabitants window (B2749, TL #25515): with many inhabitants the corner shows only the
/// important ones, the window finds all of them. Sorting stays with the table's columns.
/// </summary>
public static class InhabitantListModel
{
    /// <summary>The filters in the order of the choice.</summary>
    public static IReadOnlyList<InhabitantFilter> Filters { get; } = Enum.GetValues<InhabitantFilter>();

    /// <summary>Text key of a filter.</summary>
    public static string Key(InhabitantFilter filter) => "ui.inhabitants.filter." + filter.ToString().ToLowerInvariant();

    /// <summary>The rows that match the search text (in the shown name, ignoring case) and the filter, in their order.</summary>
    /// <param name="rows">All rows.</param>
    /// <param name="search">What the player typed; empty matches every row.</param>
    /// <param name="filter">The chosen filter.</param>
    /// <param name="knocking">Who knocks.</param>
    /// <param name="bars">The bar rule (the bad range of a state).</param>
    /// <param name="name">The name as the list shows it.</param>
    public static IReadOnlyList<InhabitantRow> Filter(
        IReadOnlyList<InhabitantRow> rows, string search, InhabitantFilter filter, IReadOnlySet<string> knocking, Bars bars, Func<InhabitantRow, string> name)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(knocking);
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(name);
        var text = Fold((search ?? string.Empty).Trim());
        return [.. rows.Where(r =>
            (text.Length == 0 || Fold(name(r)).Contains(text, StringComparison.Ordinal))
            && filter switch
            {
                InhabitantFilter.Pinned => r.Favourite,
                InhabitantFilter.Asking => CornerModel.Brooding(r),
                InhabitantFilter.Knocking => knocking.Contains(r.Id),
                InhabitantFilter.Weak => r.WillToLivePercent < bars.StateBadBelow,
                _ => true,
            })];
    }

    // The letters with marks and, at the same place, the letter without.
    private const string Marked = "àáâãäåāăąçćĉċčďđèéêëēĕėęěĝğġģĥħìíîïĩīĭįıĵķĺļľŀłñńņňŉòóôõöøōŏőŕŗřśŝşšșţťŧțùúûüũūŭůűųŵýÿŷźżž";
    private const string Plain = "aaaaaaaaacccccddeeeeeeeeegggghhiiiiiiiiijklllllnnnnnooooooooorrrsssssttttuuuuuuuuuuwyyyzzz";

    /// <summary>
    /// A text for the search: lower case, without accents ("Jörn" finds "jorn"). The game runs with invariant
    /// globalization, where neither the culture's comparison nor normalization drops accents, so a table of the Latin
    /// letters with marks does it here; "ß" reads as "ss", "æ" and "œ" as "ae" and "oe".
    /// </summary>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var upper in text)
        {
            var c = char.ToLowerInvariant(upper);
            var at = Marked.IndexOf(c, StringComparison.Ordinal);
            builder.Append(c switch
            {
                'ß' => "ss",
                'æ' => "ae",
                'œ' => "oe",
                _ => at >= 0 ? Plain[at].ToString() : c.ToString(),
            });
        }

        return builder.ToString();
    }
}
