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

    /// <summary>
    /// Placeholder values for texts about an inhabitant: <c>number</c> and <c>name</c>, the name the screen
    /// shows (<see cref="DisplayName"/>, the one rule, W1698).
    /// </summary>
    /// <param name="row">The inhabitant.</param>
    /// <param name="unnamedText">The translated text of <c>ui.inhabitant.unnamed</c>.</param>
    public static IReadOnlyDictionary<string, string> NameArgs(InhabitantRow row, string unnamedText) =>
        new Dictionary<string, string> { ["number"] = Number(row), ["name"] = DisplayName(row, unnamedText) };

    /// <summary>
    /// The name the screen shows for an inhabitant (W1698): its own name, or "Inhabitant N" from the
    /// <c>ui.inhabitant.unnamed</c> text only while it has none (cannot happen after #171, saves get names on load).
    /// </summary>
    /// <param name="row">The inhabitant.</param>
    /// <param name="unnamedText">The translated text of <c>ui.inhabitant.unnamed</c>.</param>
    public static string DisplayName(InhabitantRow row, string unnamedText)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(unnamedText);
        return string.IsNullOrWhiteSpace(row.Name)
            ? TextFill.Fill(unnamedText, new Dictionary<string, string> { ["number"] = Number(row) })
            : row.Name;
    }

    private static string Number(InhabitantRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Calm hints for new events (D366 section 9): an inhabitant has a new open question (B2487: that,
    /// not what). Hints of this kind form one group, so many questions bundle.
    /// </summary>
    /// <param name="events">New events.</param>
    /// <param name="rows">The inhabitants.</param>
    /// <param name="unnamedText">The translated text of <c>ui.inhabitant.unnamed</c>, for <see cref="NameArgs"/>.</param>
    /// <param name="questionSubject">What a question (by id) is about, so the same question is hinted only once (W1696: "13x"); null: every question.</param>
    /// <param name="hinted">Questions hinted already (inhabitant and subject), kept by the caller.</param>
    public static IEnumerable<ToastMessage> Hints(IEnumerable<WorldEvent> events, IReadOnlyList<InhabitantRow> rows, string unnamedText, Func<string, string?>? questionSubject = null, ISet<string>? hinted = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(unnamedText);
        foreach (var e in events.Where(e => e.Kind == Simulation.Player.PlayerSystem.HelpNeeded))
        {
            var row = rows.FirstOrDefault(r => r.Id == e.InhabitantId);
            if (row is not null && e.Detail is not null)
            {
                yield return new ToastMessage("msg.hint.help_needed", new Dictionary<string, string>(NameArgs(row, unnamedText)) { ["need"] = VocabKeys.Need(e.Detail) }, "help", row.Id);
            }
        }

        foreach (var e in events.Where(e => e.Kind == "question_opened"))
        {
            var row = rows.FirstOrDefault(r => r.Id == e.InhabitantId);
            var subject = e.TargetId is { } id ? questionSubject?.Invoke(id) : null;
            if (row is not null && (subject is null || hinted is null || hinted.Add(row.Id + "|" + subject)))
            {
                // A hint leads to the inhabitant (W1695: hints were not clickable).
                yield return new ToastMessage("msg.hint.question", NameArgs(row, unnamedText), "question", row.Id);
            }
        }

        // Talking without an answer (service off or too slow, R16): one calm hint, the favour is back.
        if (events.Any(e => e.Kind == "no_answer"))
        {
            yield return new ToastMessage("msg.hint.no_answer", new Dictionary<string, string>(), "no_answer");
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
