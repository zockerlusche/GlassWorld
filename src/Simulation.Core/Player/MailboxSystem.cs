using GlassWorld.Contracts;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Player;

/// <summary>A message in the player's mailbox (B6 R11; contract player-message).</summary>
/// <param name="Id">Mailbox id (<c>m-000001</c>).</param>
/// <param name="InhabitantId">Sender; null for messages of the world itself.</param>
/// <param name="Kind">Message kind (content/player/message_kinds.json).</param>
/// <param name="CreatedTick">When it arrived.</param>
/// <param name="TextKey">Text key of the base text.</param>
/// <param name="Detail">Language-neutral detail for the text (e.g. the need of a knock).</param>
/// <param name="Urgency">0 = calm; rises while unacknowledged up to the kind's limit.</param>
/// <param name="Repeats">How often it was shown again while unacknowledged.</param>
/// <param name="AcknowledgedTick">When the player acknowledged it; null while open.</param>
/// <param name="LastShownTick">When it was last shown (arrival or repeat).</param>
public sealed record MailMessage(string Id, string? InhabitantId, string Kind, long CreatedTick, string TextKey, string? Detail, int Urgency, int Repeats, long? AcknowledgedTick, long LastShownTick)
{
    /// <summary>True once the reason is over (a knock whose call ended): it stays calm and never repeats again.</summary>
    public bool Settled { get; init; }

    /// <summary>True while the player has not acknowledged it.</summary>
    public bool IsOpen => AcknowledgedTick is null;
}

/// <summary>The mailbox as saved.</summary>
/// <param name="Counter">Last message number.</param>
/// <param name="Messages">All messages, oldest first (open and history).</param>
public sealed record MailboxState(long Counter, IReadOnlyList<MailMessage> Messages);

/// <summary>
/// The player's mailbox (B6 R10/R11, a world system after the player system): messages of the
/// inhabitants to the player. An open call for help is a knock at the glass (A1341). Unacknowledged
/// messages come back after the kind's repeat time with rising urgency (calm: shown by order and a
/// warm tint, never by blinking or sound, B2466); acknowledged ones move to the history. All values
/// come from content/player/message_kinds.json.
/// </summary>
public sealed class MailboxSystem : IWorldSystem, IOptionalWorldState
{
    /// <summary>Event: a message arrived (InhabitantId = sender, TargetId = message id, Detail = kind).</summary>
    public const string MailArrived = "mail_arrived";

    /// <summary>Event: an unacknowledged message is shown again (TargetId = message id).</summary>
    public const string MailRepeated = "mail_repeated";

    /// <summary>Command kind: the player acknowledged a message (MessageId).</summary>
    public const string Acknowledge = "acknowledge_mail";

    /// <summary>Message kind of a knock at the glass (data id).</summary>
    public const string KnockKind = "knock";

    /// <summary>Reason: no open message with this id.</summary>
    public const string ReasonNoMessage = "msg.command.no_message";

    private readonly Dictionary<string, MessageKind> _kinds;
    private readonly InhabitantsSystem _inhabitants;
    private readonly PlayerSystem _player;
    private readonly int _historyMax;
    private readonly int _ticksPerHour;
    private readonly List<MailMessage> _messages = [];
    private long _counter;

    /// <summary>Creates the mailbox.</summary>
    public MailboxSystem(MessageKinds kinds, InhabitantsSystem inhabitants, PlayerSystem player, int ticksPerHour)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        _kinds = kinds.Kinds.ToDictionary(k => k.Id, StringComparer.Ordinal);
        _historyMax = kinds.HistoryMax;
        _inhabitants = inhabitants ?? throw new ArgumentNullException(nameof(inhabitants));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _ticksPerHour = ticksPerHour;
    }

    /// <inheritdoc/>
    public string Name => "mailbox";

    /// <inheritdoc/>
    public bool HasState => _counter > 0;

    /// <summary>Open messages, most urgent first, then newest.</summary>
    public IReadOnlyList<MailMessage> Open => [.. _messages.Where(m => m.IsOpen).OrderByDescending(m => m.Urgency).ThenByDescending(m => m.CreatedTick).ThenBy(m => m.Id, StringComparer.Ordinal)];

    /// <summary>Acknowledged messages, newest first.</summary>
    public IReadOnlyList<MailMessage> History => [.. _messages.Where(m => !m.IsOpen).OrderByDescending(m => m.AcknowledgedTick).ThenBy(m => m.Id, StringComparer.Ordinal)];

    /// <summary>Checks an acknowledge command.</summary>
    public CommandResult Check(PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _messages.Any(m => m.Id == command.MessageId && m.IsOpen) ? new CommandResult(true, null) : new CommandResult(false, ReasonNoMessage);
    }

    /// <summary>Applies an acknowledge command: the message moves to the history.</summary>
    public void Apply(World world, PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(command);
        var index = _messages.FindIndex(m => m.Id == command.MessageId && m.IsOpen);
        if (index >= 0)
        {
            _messages[index] = _messages[index] with { AcknowledgedTick = world.Tick };

            // A bounded history (data): the oldest read messages go.
            var read = _messages.Where(m => !m.IsOpen).OrderBy(m => m.AcknowledgedTick).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
            foreach (var old in read.Take(Math.Max(0, read.Count - _historyMax)))
            {
                _messages.Remove(old);
            }
        }
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);

        // A call for help is a knock at the glass (A1341).
        foreach (var call in _inhabitants.Events.Where(e => e.Tick == world.Tick && e.Kind == PlayerSystem.HelpNeeded).ToList())
        {
            if (_kinds.ContainsKey(KnockKind))
            {
                Add(world, call.InhabitantId, KnockKind, "msg.mail.knock", call.Detail);
            }
        }

        for (var i = 0; i < _messages.Count; i++)
        {
            var message = _messages[i];
            var kind = _kinds.GetValueOrDefault(message.Kind);
            if (!message.IsOpen || kind is null || kind.RepeatAfterHours <= 0)
            {
                continue;
            }

            // A knock whose call for help is over (need met, or the sender is gone) calms down and stops
            // repeating; it stays in the mailbox until read (review #86, B2466).
            if (message.Settled)
            {
                continue;
            }

            if (message.Kind == KnockKind && !CallStillOpen(message))
            {
                // A later call for the same need brings its own knock.
                _messages[i] = message with { Urgency = 0, Settled = true };
                continue;
            }

            if (world.Tick - message.LastShownTick >= (long)(kind.RepeatAfterHours * _ticksPerHour))
            {
                _messages[i] = message with
                {
                    Repeats = message.Repeats + 1,
                    Urgency = Math.Min(kind.MaxUrgency, message.Urgency + 1),
                    LastShownTick = world.Tick,
                };
                _inhabitants.Report(new WorldEvent(MailRepeated, world.Tick, message.InhabitantId ?? string.Empty, null, message.Id) { Detail = message.Kind });
            }
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_counter).Write(_messages.Count);
        foreach (var m in _messages)
        {
            hasher.Write(m.Id).Write(m.InhabitantId ?? string.Empty).Write(m.Kind).Write(m.CreatedTick).Write(m.TextKey).Write(m.Detail ?? string.Empty)
                .Write(m.Urgency).Write(m.Repeats).Write(m.AcknowledgedTick ?? -1).Write(m.LastShownTick).Write(m.Settled);
        }
    }

    /// <summary>The state to save.</summary>
    public MailboxState Capture() => new(_counter, [.. _messages]);

    /// <summary>
    /// Restores a saved state (null = a save game from before the mailbox: empty). Unknown kinds, ids
    /// twice or above the counter make the load fail cleanly (review #86).
    /// </summary>
    public void Restore(MailboxState? state)
    {
        _counter = 0;
        _messages.Clear();
        if (state is null)
        {
            return;
        }

        var problems = new List<string>();
        problems.AddRange(state.Messages.Where(m => !_kinds.ContainsKey(m.Kind)).Select(m => $"unknown kind '{m.Kind}'"));
        problems.AddRange(state.Messages.GroupBy(m => m.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"id '{g.Key}' twice"));
        problems.AddRange(state.Messages.Where(m => !long.TryParse(m.Id.AsSpan(2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) || n > state.Counter || n <= 0)
            .Select(m => $"id '{m.Id}' does not fit the counter {state.Counter}"));
        if (problems.Count > 0)
        {
            throw new InvalidDataException("Save game mailbox: " + string.Join("; ", problems) + ".");
        }

        _counter = state.Counter;
        _messages.AddRange(state.Messages);
    }

    private bool CallStillOpen(MailMessage message) =>
        message.InhabitantId is { } who
        && _inhabitants.All.Any(i => i.Id == who)
        && _player.Open.Any(c => c.InhabitantId == who && c.Need == message.Detail);

    private void Add(World world, string? inhabitantId, string kind, string textKey, string? detail)
    {
        _counter++;
        var id = "m-" + _counter.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        _messages.Add(new MailMessage(id, inhabitantId, kind, world.Tick, textKey, detail, 0, 0, null, world.Tick));
        _inhabitants.Report(new WorldEvent(MailArrived, world.Tick, inhabitantId ?? string.Empty, null, id) { Detail = kind });
    }
}
