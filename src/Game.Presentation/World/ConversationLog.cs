using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Presentation.World;

/// <summary>What a line of the conversation log is.</summary>
public enum LogLineKind
{
    /// <summary>An inhabitant thought something.</summary>
    Thought,

    /// <summary>An inhabitant said something (an answer to the player, or declining).</summary>
    Said,

    /// <summary>The player said something to an inhabitant.</summary>
    Player,

    /// <summary>An inhabitant did not understand the player.</summary>
    NotUnderstood,

    /// <summary>The player got no answer (service off or too slow); the favour is back.</summary>
    NoAnswer,

    /// <summary>An inhabitant declined without a word (TL #21712): the log says what was seen.</summary>
    Declined,

    /// <summary>An inhabitant knocked at the glass (B8 R17, B2608): it found nothing for a need and calls the player.</summary>
    Knock,
}

/// <summary>A line of the conversation log.</summary>
/// <param name="Seq">Running number (newer lines have higher numbers).</param>
/// <param name="InhabitantId">The inhabitant who thought or said it, or whom the player spoke to.</param>
/// <param name="Kind">What the line is.</param>
/// <param name="Text">The words, in the player's language (empty for <see cref="LogLineKind.NotUnderstood"/>).</param>
/// <param name="Tick">When it was (last) said.</param>
/// <param name="Count">How often the same was said in a row (Marcel W1696: "sie wiederholen auch immer dasselbe").</param>
public sealed record LogLine(long Seq, string InhabitantId, LogLineKind Kind, string Text, long Tick, int Count);

/// <summary>
/// The conversation log (Marcel W1696): who said or thought what, newest first, the readable source while
/// the bubbles above the heads stay short. The same line of the same inhabitant again counts up instead of
/// filling the log. Godot-free.
/// </summary>
public sealed class ConversationLog
{
    private readonly List<LogLine> _lines = [];
    private readonly int _capacity;
    private long _seq;

    private readonly IReadOnlySet<string> _spoken;

    /// <summary>
    /// Creates a log that keeps the newest <paramref name="capacity"/> lines; the thought with an action of
    /// <paramref name="spokenActions"/> (vocabulary 'spoken', A1510) is something said aloud, not thought.
    /// </summary>
    public ConversationLog(int capacity, IReadOnlySet<string>? spokenActions = null)
    {
        _capacity = Math.Max(1, capacity);
        _spoken = spokenActions ?? new HashSet<string>();
    }

    /// <summary>Changes whenever a line is added or counted up.</summary>
    public long Version { get; private set; }

    /// <summary>The lines, newest first.</summary>
    public IReadOnlyList<LogLine> Newest() => [.. Enumerable.Reverse(_lines)];

    /// <summary>Takes the conversation events from <paramref name="events"/>; others are ignored.</summary>
    public void Add(IEnumerable<WorldEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var e in events)
        {
            LogLineKind? kind = e.Kind switch
            {
                // A1510 (W1728): with a spoken action (introduce, talk, warn, show, teach) the line is said aloud.
                "thought" when e.Text is { Length: > 0 } && e.Action is { } action && _spoken.Contains(action) => LogLineKind.Said,
                "thought" when e.Text is { Length: > 0 } => LogLineKind.Thought,
                "said" or "declined" when e.Text is { Length: > 0 } => LogLineKind.Said,
                "declined" => LogLineKind.Declined,
                "conversation_started" when e.Text is { Length: > 0 } => LogLineKind.Player,
                "not_understood" => LogLineKind.NotUnderstood,
                "no_answer" => LogLineKind.NoAnswer,
                Simulation.Player.MailboxSystem.MailArrived when e.Detail == Simulation.Player.MailboxSystem.KnockKind => LogLineKind.Knock,
                _ => null,
            };
            if (kind is { } k)
            {
                // The text of "no answer" is a key for the bubble, not words, and a knock has none: the log has
                // its own sentence for both.
                Add(e.InhabitantId, k, k is LogLineKind.NoAnswer or LogLineKind.Knock or LogLineKind.Declined ? string.Empty : e.Text ?? string.Empty, e.Tick);
            }
        }
    }

    private void Add(string inhabitantId, LogLineKind kind, string text, long tick)
    {
        Version++;
        var last = _lines.FindLastIndex(l => l.InhabitantId == inhabitantId);
        if (last >= 0 && _lines[last].Kind == kind && _lines[last].Text == text)
        {
            // Said again: one line with a count, moved to the top.
            var again = _lines[last] with { Seq = ++_seq, Tick = tick, Count = _lines[last].Count + 1 };
            _lines.RemoveAt(last);
            _lines.Add(again);
            return;
        }

        _lines.Add(new LogLine(++_seq, inhabitantId, kind, text, tick, 1));
        if (_lines.Count > _capacity)
        {
            _lines.RemoveAt(0);
        }
    }
}
