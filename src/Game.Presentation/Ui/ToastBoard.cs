using GlassWorld.Contracts;

namespace GlassWorld.Presentation.Ui;

/// <summary>A calm hint (D366 section 9): a text key with arguments and an optional group for bundling.</summary>
/// <param name="TextKey">Translation key, e.g. <c>msg.mail.kind.knock</c>.</param>
/// <param name="Args">Placeholder values (<c>{name}</c> in the text).</param>
/// <param name="Group">Hints of the same group are bundled in fast-forward; null = never bundled.</param>
public sealed record ToastMessage(string TextKey, IReadOnlyDictionary<string, string> Args, string? Group);

/// <summary>A hint on screen.</summary>
/// <param name="Id">Running number.</param>
/// <param name="Message">What it says.</param>
/// <param name="Count">How many hints it bundles (1 = single).</param>
public sealed record VisibleToast(long Id, ToastMessage Message, int Count);

/// <summary>Player settings for hints (D366 section 9/13, XAG 116).</summary>
/// <param name="DurationFactor">Display time factor 1..max (tokens).</param>
/// <param name="UntilDismissed">Hints stay until the player closes them.</param>
public sealed record ToastSettings(int DurationFactor, bool UntilDismissed);

/// <summary>
/// The hint board of the UI standard: at most <c>max_visible</c> hints at once, the rest wait in
/// order; each stays <c>min_seconds × factor</c> after it appeared (or until dismissed); time does
/// not run while the mouse is on it; in fast-forward hints of one group are bundled into one with a
/// count. Time is passed in (real seconds), so the queue is testable and independent of game speed.
/// </summary>
public sealed class ToastBoard
{
    private readonly UiToastTokens _tokens;
    private readonly List<Entry> _visible = [];
    private readonly Queue<Entry> _waiting = new();
    private long _next;

    /// <summary>Creates an empty queue.</summary>
    public ToastBoard(UiToastTokens tokens, ToastSettings settings)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Current settings (factor clamped to the tokens).</summary>
    public ToastSettings Settings { get; set; }

    /// <summary>Hints on screen, oldest first.</summary>
    public IReadOnlyList<VisibleToast> Visible => [.. _visible.Select(e => new VisibleToast(e.Id, e.Message, e.Count))];

    /// <summary>Hints waiting for a free place.</summary>
    public int Waiting => _waiting.Count;

    /// <summary>Adds a hint; in fast-forward a hint of the same group raises that hint's count instead.</summary>
    public void Push(ToastMessage message, bool fastForward)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (fastForward && message.Group is not null)
        {
            var same = _visible.Concat(_waiting).FirstOrDefault(e => e.Message.Group == message.Group);
            if (same is not null)
            {
                same.Count++;
                return;
            }
        }

        _waiting.Enqueue(new Entry(_next++, message));
        Fill();
    }

    /// <summary>Advances real time by <paramref name="seconds"/>; the hovered hint does not age.</summary>
    public void Advance(double seconds, long? hoveredId)
    {
        if (!Settings.UntilDismissed)
        {
            var lifetime = _tokens.MinSeconds * Math.Clamp(Settings.DurationFactor, 1, _tokens.MaxDurationFactor);
            foreach (var entry in _visible.Where(e => e.Id != hoveredId))
            {
                entry.Age += seconds;
            }

            _visible.RemoveAll(e => e.Age >= lifetime);
        }

        Fill();
    }

    /// <summary>Closes a hint at once (player click).</summary>
    public void Dismiss(long id)
    {
        _visible.RemoveAll(e => e.Id == id);
        Fill();
    }

    private void Fill()
    {
        while (_visible.Count < _tokens.MaxVisible && _waiting.Count > 0)
        {
            _visible.Add(_waiting.Dequeue());
        }
    }

    private sealed class Entry(long id, ToastMessage message)
    {
        public long Id { get; } = id;

        public ToastMessage Message { get; } = message;

        public int Count { get; set; } = 1;

        public double Age { get; set; }
    }
}
