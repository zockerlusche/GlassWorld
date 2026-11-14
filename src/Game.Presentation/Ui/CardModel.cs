using GlassWorld.Contracts;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Ui;

/// <summary>What a value bar is about, for the bar rule (B2642, D772).</summary>
public enum BarKind
{
    /// <summary>A state: much is good (needs, will to live, favour).</summary>
    State,

    /// <summary>An uplifting feeling: much is good, little is no harm (joy, love, pride).</summary>
    Uplift,

    /// <summary>A burdening feeling: much is bad (fear, sadness ...).</summary>
    Burden,

    /// <summary>Neither good nor bad (character traits, feelings the data does not name).</summary>
    Neutral,
}

/// <summary>How a bar looks after the rule: calm, with a thumb up, or in the bad range (warning colour, thumb down).</summary>
public enum BarLook
{
    /// <summary>Plain bar, no thumb.</summary>
    Calm,

    /// <summary>Good: a thumb up.</summary>
    Good,

    /// <summary>In the bad range: warning colour and a thumb down.</summary>
    Bad,
}

/// <summary>
/// Rules of the inhabitant card on the right (S2, D957 v4 section 2, A1683) that need no Godot: the bar rule with its
/// thresholds from the data (B2642), which open question stands on top (stable, B2595), and how many diary entries
/// and conversation lines show (B2603).
/// </summary>
public static class CardModel
{
    private const string MoodPrefix = "vocab.mood.";
    private const string TraitPrefix = "vocab.trait.";

    /// <summary>
    /// What a bar is about, by its text key: a feeling as the data names it (uplift or burden, else neutral), a
    /// character trait neutral, everything else a state (needs, will to live, favour).
    /// </summary>
    public static BarKind KindOf(string key, Bars bars)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(bars);
        if (key.StartsWith(TraitPrefix, StringComparison.Ordinal))
        {
            return BarKind.Neutral;
        }

        if (!key.StartsWith(MoodPrefix, StringComparison.Ordinal))
        {
            return BarKind.State;
        }

        var id = key[MoodPrefix.Length..];
        return bars.Moods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal)) is { } mood
            ? mood.Kind == UiBarMoodKind.Burden ? BarKind.Burden : BarKind.Uplift
            : BarKind.Neutral;
    }

    /// <summary>The bar rule (B2642): the length shows how much is there; colour and thumb say whether that is good.</summary>
    public static BarLook Judge(BarKind kind, int percent, Bars bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        return kind switch
        {
            BarKind.State when percent < bars.StateBadBelow => BarLook.Bad,
            BarKind.State or BarKind.Uplift when percent >= bars.StateGoodFrom => BarLook.Good,
            BarKind.Burden when percent >= bars.BurdenBadFrom => BarLook.Bad,
            _ => BarLook.Calm,
        };
    }

    /// <summary>
    /// The question on top of the card (B2595: it stays until answered or solved by the inhabitant itself): the one
    /// shown so far while it is open, else the oldest open one. Null without open questions.
    /// </summary>
    /// <param name="own">The inhabitant's open questions, in any order.</param>
    /// <param name="shownId">The question shown on top so far, or null.</param>
    public static QuestionRow? TopQuestion(IReadOnlyList<QuestionRow> own, string? shownId)
    {
        ArgumentNullException.ThrowIfNull(own);
        if (shownId is not null && own.FirstOrDefault(q => string.Equals(q.Id, shownId, StringComparison.Ordinal)) is { } kept)
        {
            return kept;
        }

        return own.OrderBy(q => q.AskedTick).ThenBy(q => q.Id, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>
    /// The other open questions below the top one, oldest first; one per thing and kind, so four times "What is
    /// that?" about the same thing reads once (D1060 find 14).
    /// </summary>
    public static IReadOnlyList<QuestionRow> OtherQuestions(IReadOnlyList<QuestionRow> own, QuestionRow? top)
    {
        ArgumentNullException.ThrowIfNull(own);
        return [.. own
            .Where(q => top is null || !string.Equals(q.Id, top.Id, StringComparison.Ordinal))
            .Where(q => top is null || !string.Equals(q.Kind, top.Kind, StringComparison.Ordinal) || !string.Equals(q.TypeId, top.TypeId, StringComparison.Ordinal))
            .OrderBy(q => q.AskedTick).ThenBy(q => q.Id, StringComparer.Ordinal)
            .DistinctBy(q => (q.Kind, q.TypeId))];
    }

    /// <summary>The first <paramref name="count"/> entries of a list that is newest first (the diary on top of the card).</summary>
    public static IReadOnlyList<T> Newest<T>(IReadOnlyList<T> newestFirst, int count)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);
        return [.. newestFirst.Take(Math.Max(0, count))];
    }

    /// <summary>The last <paramref name="count"/> entries of a list that is oldest first (the conversation above the talk field).</summary>
    public static IReadOnlyList<T> Last<T>(IReadOnlyList<T> oldestFirst, int count)
    {
        ArgumentNullException.ThrowIfNull(oldestFirst);
        return [.. oldestFirst.Skip(Math.Max(0, oldestFirst.Count - Math.Max(0, count)))];
    }
}
