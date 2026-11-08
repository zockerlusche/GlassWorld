using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Presentation.Ui;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Presentation.World;

/// <summary>A choice of the time control (B6 R5): a compact, language-neutral symbol plus a tooltip key.</summary>
/// <param name="Symbol">What the button shows (no letters, e.g. <c>||</c>, <c>&gt;</c>, <c>&gt;&gt; ×4</c>).</param>
/// <param name="TooltipKey">Text key of the tooltip (D366 section 6: symbol buttons always have one).</param>
/// <param name="Args">Placeholder values for the tooltip (e.g. the factor).</param>
/// <param name="Speed">Speed factor for the world: 0 = pause, 1 = normal.</param>
public sealed record TimeChoice(string Symbol, string TooltipKey, IReadOnlyDictionary<string, string> Args, double Speed);

/// <summary>
/// Decisions of the game screen that need no Godot (ADR-0008): time control choices from the world
/// time data, the neutral name of an inhabitant, which events become calm hints, and which figure a
/// click selects.
/// </summary>
public static class ScreenModel
{
    /// <summary>Pause, normal and the fast-forward factors of content/world/time.json, in this order.</summary>
    public static IReadOnlyList<TimeChoice> TimeChoices(WorldTimeSettings time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var none = new Dictionary<string, string>();
        return
        [
            new("||", "ui.time.pause", none, 0),
            new(">", "ui.time.normal", none, 1),
            .. time.FastForwardFactors.Order().Select(f => new TimeChoice(
                ">> ×" + f.ToString(CultureInfo.InvariantCulture),
                "ui.time.fast",
                new Dictionary<string, string> { ["factor"] = f.ToString(CultureInfo.InvariantCulture) },
                f)),
        ];
    }

    /// <summary>Placeholder values for <c>ui.inhabitant.unnamed</c> (names come with B9).</summary>
    public static IReadOnlyDictionary<string, string> NameArgs(InhabitantRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new Dictionary<string, string> { ["number"] = row.Number.ToString(CultureInfo.InvariantCulture) };
    }

    /// <summary>
    /// Calm hints for new events (D366 section 9): an inhabitant has a new open question (B2487: that,
    /// not what). Hints of this kind form one group, so many questions bundle.
    /// </summary>
    public static IEnumerable<ToastMessage> Hints(IEnumerable<WorldEvent> events, IReadOnlyList<InhabitantRow> rows)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var e in events.Where(e => e.Kind == Simulation.Player.PlayerSystem.HelpNeeded))
        {
            var row = rows.FirstOrDefault(r => r.Id == e.InhabitantId);
            if (row is not null && e.Detail is not null)
            {
                yield return new ToastMessage("msg.hint.help_needed", new Dictionary<string, string>(NameArgs(row)) { ["need"] = "vocab.need." + e.Detail }, "help");
            }
        }

        foreach (var e in events.Where(e => e.Kind == "question_opened"))
        {
            var row = rows.FirstOrDefault(r => r.Id == e.InhabitantId);
            if (row is not null)
            {
                yield return new ToastMessage("msg.hint.question", NameArgs(row), "question");
            }
        }
    }

    /// <summary>The figure nearest to a click within the pick radius, or null (screen positions in logical px).</summary>
    public static string? Pick(IEnumerable<(string Id, double X, double Y)> figures, double clickX, double clickY, double radius)
    {
        ArgumentNullException.ThrowIfNull(figures);
        return figures
            .Select(f => (f.Id, Distance: Math.Sqrt(((f.X - clickX) * (f.X - clickX)) + ((f.Y - clickY) * (f.Y - clickY)))))
            .Where(f => f.Distance <= radius)
            .OrderBy(f => f.Distance)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .Select(f => f.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Width of the screen part the inhabitant card is centred in: everything left of the side bar and
    /// its open panel (<paramref name="freeRightEdge"/>). If the card does not fit there with its safe
    /// margins, it becomes a foreground window over the whole width (deliberately over the panel).
    /// </summary>
    public static double CardRoom(double areaWidth, double freeRightEdge, double cardMinWidth, double safeMargin)
        => freeRightEdge - (2 * safeMargin) >= cardMinWidth ? freeRightEdge : areaWidth;

    /// <summary>Question marks over a head: one per open question, at most three (B2487: several at once).</summary>
    public static string QuestionMarks(int openQuestions) => new('?', Math.Clamp(openQuestions, 0, 3));
}
