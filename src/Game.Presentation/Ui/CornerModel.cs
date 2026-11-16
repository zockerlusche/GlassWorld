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
    /// The inhabitants the corner shows (B2749, TL #25515): all of them while they are fewer than
    /// <c>portraits_important_from</c>; from there on only the important ones - the one followed or selected, who knocks
    /// (a call for help, review GW3 #454), the pinned favourites, who puzzles over a question - at most
    /// <c>portraits_important_max</c>, in the order of the
    /// list (so nobody jumps), and how many more there are (the "+N" that opens the inhabitants window).
    /// </summary>
    /// <param name="rows">All inhabitants in the order of the list.</param>
    /// <param name="zones">The thresholds.</param>
    /// <param name="selected">The inhabitant followed or selected, or null.</param>
    /// <param name="knocking">Who knocks on the glass (an open message in the mailbox).</param>
    public static (IReadOnlyList<InhabitantRow> Shown, int More) Shown(IReadOnlyList<InhabitantRow> rows, Zones zones, string? selected, IReadOnlySet<string> knocking)
    {
        ArgumentNullException.ThrowIfNull(knocking);
        return Shown(rows, zones, selected, r => knocking.Contains(r.Id) ? PortraitState.Knock : Brooding(r) ? PortraitState.Question : PortraitState.None);
    }

    /// <summary>
    /// The inhabitants the corner shows, ranked by the state their portrait shows (A1752, B2792): the one followed or
    /// selected, then who knocks, who worries, who had an aha, who puzzles over a question, then the favourites - at most
    /// <c>portraits_important_max</c>, in the order of the list (so nobody jumps).
    /// </summary>
    /// <param name="rows">All inhabitants in the order of the list.</param>
    /// <param name="zones">The thresholds.</param>
    /// <param name="selected">The inhabitant followed or selected, or null.</param>
    /// <param name="stateOf">The state each portrait shows.</param>
    public static (IReadOnlyList<InhabitantRow> Shown, int More) Shown(IReadOnlyList<InhabitantRow> rows, Zones zones, string? selected, Func<InhabitantRow, PortraitState> stateOf)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(stateOf);
        if (rows.Count < zones.PortraitsImportantFrom)
        {
            return (rows, 0);
        }

        int Rank(InhabitantRow r) =>
            r.Id == selected ? 0 : stateOf(r) switch
            {
                PortraitState.Knock => 1,
                PortraitState.Worry => 2,
                PortraitState.Aha => 3,
                PortraitState.Question => 4,
                _ => r.Favourite ? 5 : -1,
            };

        List<InhabitantRow> shown = [.. rows
            .Select((row, index) => (Row: row, Index: index, Rank: Rank(row)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Index)
            .Take(Math.Max(0, zones.PortraitsImportantMax))
            .OrderBy(x => x.Index)
            .Select(x => x.Row)];
        return (shown, rows.Count - shown.Count);
    }

    /// <summary>
    /// The one state a portrait shows (A1752, B2792): the most important of knock (an open message at the glass), worry (a
    /// need long below its neglect line, or fading), aha (something succeeded lately) and question (one still puzzles it).
    /// </summary>
    /// <param name="row">The inhabitant.</param>
    /// <param name="knocking">It knocks at the glass.</param>
    /// <param name="aha">It had an aha that still shows.</param>
    /// <param name="data">The thresholds (tokens portrait_states).</param>
    public static PortraitState State(InhabitantRow row, bool knocking, bool aha, UiPortraitStates data)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(data);
        return knocking ? PortraitState.Knock
            : Worried(row, data) ? PortraitState.Worry
            : aha ? PortraitState.Aha
            : Brooding(row) ? PortraitState.Question
            : PortraitState.None;
    }

    /// <summary>Whether the worry mark shows (A1752, GW1 #27065): a need below its neglect line for the data's hours, or fading.</summary>
    public static bool Worried(InhabitantRow row, UiPortraitStates data)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(data);
        return row.Fading || row.NeglectHours >= data.WorryAfterHours;
    }

    /// <summary>
    /// The knock ring's strength <paramref name="seconds"/> into the knocking (A1752, B2792): the data's taps, short and
    /// apart, then a pause, again and again - 1 at the height of a tap, 0 between. With reduced motion it stands at 1.
    /// </summary>
    public static double KnockPulse(UiPortraitStates data, double seconds, bool reduceMotion)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (reduceMotion)
        {
            return 1;
        }

        var period = (data.KnockTaps * data.KnockTapS) + ((data.KnockTaps - 1) * data.KnockGapS) + data.KnockPauseS;
        var t = ((seconds % period) + period) % period;
        for (var i = 0; i < data.KnockTaps; i++)
        {
            var start = i * (data.KnockTapS + data.KnockGapS);
            if (t >= start && t < start + data.KnockTapS)
            {
                return Math.Sin(Math.PI * (t - start) / data.KnockTapS);
            }
        }

        return 0;
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

/// <summary>The one state a portrait shows (A1752, B2792), from the least to the most important.</summary>
public enum PortraitState
{
    /// <summary>Nothing to show.</summary>
    None,

    /// <summary>A question still puzzles it: "?".</summary>
    Question,

    /// <summary>Something succeeded lately (invented, built, a new word): the ring lights up once, then stays calmly bright; a star.</summary>
    Aha,

    /// <summary>A need long below its neglect line, or fading: "!", no ring, no motion.</summary>
    Worry,

    /// <summary>It knocks at the glass: the ring pulses in the knock's rhythm; sound arcs.</summary>
    Knock,
}
