using GlassWorld.Contracts;
using GlassWorld.Presentation.Ui;

namespace GlassWorld.Presentation.World;

/// <summary>
/// The log's categories and its filter (A1684, B2607 - Marcel W1740): every line has a kind (the conversation's,
/// a hint's or the chronicle's), the data put kinds into categories (talk, thought, discovery, idea, built,
/// important, note); the player filters by category and searches. Godot-free.
/// </summary>
public static class LogModel
{
    /// <summary>Kind of a hint line about a question (it was the hint "wonders about something").</summary>
    public const string QuestionHint = "question";

    /// <summary>Kind of a hint line about a call for help (it also stays a hint in the box).</summary>
    public const string HelpHint = "help";

    /// <summary>Kind of a hint line that answers the player (exported, gift placed, nothing there).</summary>
    public const string NoteHint = "note";

    /// <summary>The kind id of a line, as the data's categories name it.</summary>
    public static string KindId(LogLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.Kind switch
        {
            LogLineKind.Hint or LogLineKind.Chronicle => line.Text,
            LogLineKind.NotUnderstood => "not_understood",
            LogLineKind.NoAnswer => "no_answer",
            _ => line.Kind.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>The category of a line, or null when no category names its kind.</summary>
    public static UiLogCategory? CategoryOf(LogLine line, Log log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var kind = KindId(line);
        return log.Categories.FirstOrDefault(c => c.Kinds.Contains(kind, StringComparer.Ordinal));
    }

    /// <summary>Text key of a category.</summary>
    public static string CategoryKey(string categoryId) => "ui.log.category." + categoryId;

    /// <summary>
    /// The lines of a category (null = all) whose text holds the search (case and accents ignored), newest first.
    /// </summary>
    /// <param name="lines">The log's lines, newest first.</param>
    /// <param name="categoryId">The chosen category, or null for all.</param>
    /// <param name="search">What the player typed; empty matches every line.</param>
    /// <param name="log">The categories.</param>
    /// <param name="text">The line as shown (for the search).</param>
    public static IReadOnlyList<LogLine> Filter(IReadOnlyList<LogLine> lines, string? categoryId, string search, Log log, Func<LogLine, string> text)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(text);
        var folded = InhabitantListModel.Fold((search ?? string.Empty).Trim());
        return [.. lines.Where(l =>
            (categoryId is null || CategoryOf(l, log)?.Id == categoryId)
            && (folded.Length == 0 || InhabitantListModel.Fold(text(l)).Contains(folded, StringComparison.Ordinal)))];
    }
}
