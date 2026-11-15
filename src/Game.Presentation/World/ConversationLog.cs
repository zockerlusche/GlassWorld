using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Presentation.World;

/// <summary>What a line of the conversation log is.</summary>
public enum LogLineKind
{
    /// <summary>An inhabitant thought something.</summary>
    Thought,

    /// <summary>An inhabitant said something (an answer to the player, declining, or aloud to another inhabitant with a spoken action, A1510).</summary>
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

    /// <summary>An inhabitant finished building a thing (A1715, D1112 #6); the line's text is the thing's object id, to find it.</summary>
    Built,

    /// <summary>
    /// A hint that became a line (A1684): the text is its kind (question, help, note), the words are
    /// <see cref="LogLine.Key"/> with <see cref="LogLine.Args"/>; the hint box keeps only knocks and calls for help.
    /// </summary>
    Hint,

    /// <summary>An entry of the chronicle (A1684, B2607: discoveries, ideas, births): the text is its kind id.</summary>
    Chronicle,
}

/// <summary>A line of the conversation log.</summary>
/// <param name="Seq">Running number (newer lines have higher numbers).</param>
/// <param name="InhabitantId">The inhabitant who thought or said it, or whom the player spoke to.</param>
/// <param name="Kind">What the line is.</param>
/// <param name="Text">The words, in the player's language (empty for <see cref="LogLineKind.NotUnderstood"/>).</param>
/// <param name="Tick">When it was (last) said.</param>
/// <param name="Count">How often the same was said in a row (Marcel W1696: "sie wiederholen auch immer dasselbe").</param>
/// <param name="Key">A hint's text key, or a chronicle entry's (A1684); null for the conversation's own lines.</param>
/// <param name="Args">Its placeholder values (names already filled in).</param>
/// <param name="LookAt">Inhabitant a click leads to, if not <paramref name="InhabitantId"/> (a hint without one).</param>
public sealed record LogLine(long Seq, string InhabitantId, LogLineKind Kind, string Text, long Tick, int Count, string? Key = null, IReadOnlyDictionary<string, string>? Args = null, string? LookAt = null)
{
    /// <summary>Whether two lines say the same (kind, words, key and values): the second counts up the first.</summary>
    public bool SaysTheSameAs(LogLine other) =>
        other is not null && other.Kind == Kind && other.Text == Text && other.Key == Key
        && (Args ?? new Dictionary<string, string>()).OrderBy(a => a.Key, StringComparer.Ordinal).SequenceEqual((other.Args ?? new Dictionary<string, string>()).OrderBy(a => a.Key, StringComparer.Ordinal));
}

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
                Simulation.Inventions.InventionSystem.Built when e.TargetId is { Length: > 0 } => LogLineKind.Built,
                _ => null,
            };
            if (kind is { } k)
            {
                // The text of "no answer" is a key for the bubble, not words, and a knock has none: the log has
                // its own sentence for both.
                // A built thing keeps its object id, so the line can lead the camera to it (A1715).
                var text = k switch
                {
                    LogLineKind.Built => e.TargetId!,
                    LogLineKind.NoAnswer or LogLineKind.Knock or LogLineKind.Declined => string.Empty,
                    _ => e.Text ?? string.Empty,
                };
                Add(e.InhabitantId, k, text, e.Tick);
            }
        }
    }

    /// <summary>
    /// A hint becomes a line (A1684): <paramref name="kindId"/> is its kind for the categories (question, help, note),
    /// <paramref name="key"/> and <paramref name="args"/> its words; the same hint again counts up.
    /// </summary>
    public void AddHint(string? inhabitantId, string kindId, string key, IReadOnlyDictionary<string, string> args, long tick, string? lookAt = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(kindId);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(args);
        Add(new LogLine(0, inhabitantId ?? string.Empty, LogLineKind.Hint, kindId, tick, 1, key, args, lookAt));
    }

    /// <summary>
    /// The chronicle's new entries become lines (A1684, B2607), oldest first; <paramref name="newest"/> are the book's
    /// entries newest first, <paramref name="count"/> of them new. The kind "built" is left out: the log has its own
    /// line for it, which leads the camera to the thing.
    /// </summary>
    public void AddChronicle(IReadOnlyList<ChronicleEntry> newest, int count, Func<ChronicleEntry, (string TextKey, IReadOnlyDictionary<string, string> Args)> line)
    {
        ArgumentNullException.ThrowIfNull(newest);
        ArgumentNullException.ThrowIfNull(line);
        for (var i = Math.Min(count, newest.Count) - 1; i >= 0; i--)
        {
            var entry = newest[i];
            if (entry.Kind.Id == "built")
            {
                continue;
            }

            var (key, args) = line(entry);
            Add(new LogLine(0, entry.InhabitantId, LogLineKind.Chronicle, entry.Kind.Id, entry.Tick, 1, key, args, entry.TargetId is { } target && entry.Kind.Params.Contains("other") ? target : null));
        }
    }

    private void Add(string inhabitantId, LogLineKind kind, string text, long tick) =>
        Add(new LogLine(0, inhabitantId, kind, text, tick, 1));

    private void Add(LogLine line)
    {
        var inhabitantId = line.InhabitantId;
        var kind = line.Kind;
        var text = line.Text;
        var tick = line.Tick;
        Version++;
        var last = _lines.FindLastIndex(l => l.InhabitantId == inhabitantId);
        if (last >= 0 && _lines[last].SaysTheSameAs(line))
        {
            // Said again: one line with a count, moved to the top.
            var again = _lines[last] with { Seq = ++_seq, Tick = tick, Count = _lines[last].Count + 1 };
            _lines.RemoveAt(last);
            _lines.Add(again);
            return;
        }

        _lines.Add(line with { Seq = ++_seq, InhabitantId = inhabitantId, Kind = kind, Text = text, Tick = tick, Count = 1 });
        if (_lines.Count > _capacity)
        {
            _lines.RemoveAt(0);
        }
    }
}
