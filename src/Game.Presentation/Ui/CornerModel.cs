using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Ui;

/// <summary>
/// Rules of the corners of Variante A (D957 v4 section 1, A1682) that need no Godot: when the portrait list turns
/// compact, which rows of it are in view, which drawn symbol stands for a season, a weather or a speed, the count
/// on a round button, and the rectangle in the middle of the picture that stays free.
/// </summary>
public static class CornerModel
{
    /// <summary>The largest count a round button shows; more reads as this number and a plus.</summary>
    public const int MaxBadge = 99;

    /// <summary>From the data's number of inhabitants on, the list shows portraits only (the name on hover) and scrolls.</summary>
    public static bool Compact(int inhabitants, Zones zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        return inhabitants >= zones.PortraitsCompactFrom;
    }

    /// <summary>
    /// The rows of the portrait list that are in view (or one row beyond, so scrolling never shows an empty
    /// portrait first): only these render their portrait, so a large world does not render all at once.
    /// </summary>
    /// <param name="count">Number of rows.</param>
    /// <param name="rowPx">Height of a row in pixels.</param>
    /// <param name="viewPx">Height of the visible part in pixels.</param>
    /// <param name="scrollPx">How far the list is scrolled in pixels.</param>
    /// <returns>First and last row in view; (0, -1) when there is none.</returns>
    public static (int First, int Last) InView(int count, double rowPx, double viewPx, double scrollPx)
    {
        if (count <= 0 || rowPx <= 0)
        {
            return (0, -1);
        }

        var first = Math.Clamp((int)Math.Floor(Math.Max(0, scrollPx) / rowPx) - 1, 0, count - 1);
        var last = Math.Clamp((int)Math.Ceiling((Math.Max(0, scrollPx) + Math.Max(0, viewPx)) / rowPx), 0, count - 1);
        return (first, last);
    }

    /// <summary>The symbol the data gives an id (season, weather); <see cref="UiIconChoiceIcon.Unknown"/> when it names none.</summary>
    public static UiIconChoiceIcon IconFor(IEnumerable<UiIconChoice> choices, string? id)
    {
        ArgumentNullException.ThrowIfNull(choices);
        return choices.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal))?.Icon ?? UiIconChoiceIcon.Unknown;
    }

    /// <summary>The season id of a season text key (<c>time.season.&lt;id&gt;</c>).</summary>
    public static string SeasonId(string seasonKey)
    {
        ArgumentNullException.ThrowIfNull(seasonKey);
        const string prefix = "time.season.";
        return seasonKey.StartsWith(prefix, StringComparison.Ordinal) ? seasonKey[prefix.Length..] : seasonKey;
    }

    /// <summary>
    /// The symbol of a time choice and how many arrows it draws: pause, play (one arrow), and each fast-forward
    /// factor one arrow more than the one before (two for the slowest), so the speeds read without letters.
    /// </summary>
    public static (UiIconChoiceIcon Icon, int Arrows) SpeedIcon(IReadOnlyList<TimeChoice> choices, TimeChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(choice);
        if (choice.Speed <= 0)
        {
            return (UiIconChoiceIcon.Pause, 0);
        }

        if (choice.Speed <= 1)
        {
            return (UiIconChoiceIcon.Play, 1);
        }

        var faster = choices.Where(c => c.Speed > 1).Select(c => c.Speed).Distinct().Order().ToList();
        return (UiIconChoiceIcon.Fast, 2 + Math.Max(0, faster.IndexOf(choice.Speed)));
    }

    /// <summary>The count on a round button (open questions, open mail): empty for none, capped at <see cref="MaxBadge"/>.</summary>
    public static string Badge(int count) => count <= 0
        ? string.Empty
        : count > MaxBadge ? MaxBadge.ToString(CultureInfo.InvariantCulture) + "+" : count.ToString(CultureInfo.InvariantCulture);

    /// <summary>The rectangle in the middle of the picture that no control may cover (D957 section 5).</summary>
    /// <param name="width">Width of the picture.</param>
    /// <param name="height">Height of the picture.</param>
    /// <param name="share">Share of width and height the rectangle takes (data: zones.middle_share).</param>
    public static (double X, double Y, double Width, double Height) Middle(double width, double height, double share)
    {
        var w = Math.Max(0, width) * Math.Clamp(share, 0, 1);
        var h = Math.Max(0, height) * Math.Clamp(share, 0, 1);
        return ((width - w) / 2, (height - h) / 2, w, h);
    }

    /// <summary>The letter a portrait shows while its picture is not there (no rendering, headless): the name's first letter.</summary>
    public static string Initial(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return "?";
        }

        return StringInfo.GetNextTextElement(trimmed).ToUpper(CultureInfo.InvariantCulture);
    }

    /// <summary>Whether a portrait shows the "?" (B2596): a question still puzzles the inhabitant, as the mark over its head.</summary>
    public static bool Brooding(InhabitantRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.PuzzlingQuestions > 0;
    }
}
