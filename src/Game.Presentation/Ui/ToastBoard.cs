using GlassWorld.Contracts;

namespace GlassWorld.Presentation.Ui;

/// <summary>A calm hint (D366 section 9): a text key with arguments and an optional group for bundling.</summary>
/// <param name="TextKey">Translation key, e.g. <c>msg.mail.kind.knock</c>.</param>
/// <param name="Args">Placeholder values (<c>{name}</c> in the text).</param>
/// <param name="Group">Hints of the same group are bundled; null = the text key is the group.</param>
/// <param name="LookAt">Inhabitant the hint is about: the hint gets a "look" button, the only way an event moves the camera (TL #19976).</param>
/// <param name="ManyKey">Text key shown instead of a count when the hint bundles several ("wonders about several things"); null = text with count.</param>
public sealed record ToastMessage(string TextKey, IReadOnlyDictionary<string, string> Args, string? Group, string? LookAt = null, string? ManyKey = null);

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
/// not run while the mouse is on it; in fast-forward – or when <c>bundle_after_waiting</c> hints are
/// waiting – hints of one group are bundled into one with a count, and the bundle starts its time again.
/// Time is passed in (real seconds), so the board is testable and independent of game speed.
/// <see cref="Version"/> changes whenever what is shown changes, so the UI rebuilds only then.
/// </summary>
public sealed class ToastBoard
{
    private readonly UiToastTokens _tokens;
    private readonly List<Entry> _visible = [];
    private readonly Queue<Entry> _waiting = new();
    private long _next;
    private ToastSettings _settings;

    /// <summary>Creates an empty queue.</summary>
    public ToastBoard(UiToastTokens tokens, ToastSettings settings)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Current settings (factor clamped to the tokens).</summary>
    public ToastSettings Settings
    {
        get => _settings;
        set => _settings = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Changes whenever the visible hints or their counts change.</summary>
    public long Version { get; private set; }

    /// <summary>Hints on screen, oldest first.</summary>
    public IReadOnlyList<VisibleToast> Visible => [.. _visible.Select(e => new VisibleToast(e.Id, e.Message, e.Count))];

    /// <summary>Hints waiting for a free place.</summary>
    public int Waiting => _waiting.Count;

    /// <summary>Adds a hint; in fast-forward a hint of the same group raises that hint's count instead.</summary>
    public void Push(ToastMessage message, bool fastForward)
    {
        ArgumentNullException.ThrowIfNull(message);
        // A hint with a text for "several" is one hint per group at any speed (one per inhabitant, D705 A4).
        var bundle = fastForward || _waiting.Count >= _tokens.BundleAfterWaiting || message.ManyKey is not null;
        if (bundle)
        {
            var same = _visible.Concat(_waiting).FirstOrDefault(e => GroupOf(e.Message) == GroupOf(message));
            if (same is not null)
            {
                same.Count++;
                same.Age = 0;
                Version++;
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

            if (_visible.RemoveAll(e => e.Age >= lifetime) > 0)
            {
                Version++;
            }
        }

        Fill();
    }

    /// <summary>Closes a hint at once (player click).</summary>
    public void Dismiss(long id)
    {
        if (_visible.RemoveAll(e => e.Id == id) > 0)
        {
            Version++;
        }

        Fill();
    }

    /// <summary>Closes every hint, shown or waiting, that no longer holds (e.g. the question it points to is answered).</summary>
    public void DismissWhere(Func<ToastMessage, bool> stale)
    {
        ArgumentNullException.ThrowIfNull(stale);
        var removed = _visible.RemoveAll(e => stale(e.Message));
        var keep = _waiting.Where(e => !stale(e.Message)).ToList();
        if (removed == 0 && keep.Count == _waiting.Count)
        {
            return;
        }

        _waiting.Clear();
        foreach (var entry in keep)
        {
            _waiting.Enqueue(entry);
        }

        Version++;
        Fill();
    }

    /// <summary>Closes the oldest visible hint (keyboard action, XAG 116).</summary>
    public void DismissOldest()
    {
        if (_visible.Count > 0)
        {
            Dismiss(_visible[0].Id);
        }
    }

    private static string GroupOf(ToastMessage message) => message.Group ?? message.TextKey;

    private void Fill()
    {
        while (_visible.Count < _tokens.MaxVisible && _waiting.Count > 0)
        {
            _visible.Add(_waiting.Dequeue());
            Version++;
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
