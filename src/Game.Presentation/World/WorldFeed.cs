using System.Globalization;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.View;

namespace GlassWorld.Presentation.World;

/// <summary>A row of the inhabitant list (B6 R5/R6).</summary>
/// <param name="Id">Inhabitant id (never shown).</param>
/// <param name="Number">Neutral number until names exist (B9); shown via <c>ui.inhabitant.unnamed</c>.</param>
/// <param name="WillToLivePercent">Will to live 0-100.</param>
/// <param name="Fading">True while the inhabitant is fading (long neglect).</param>
/// <param name="ActionKey">Text key of the current action or null.</param>
/// <param name="OpenQuestions">Number of open questions (B2487: that, not what).</param>
public sealed record InhabitantRow(string Id, int Number, int WillToLivePercent, bool Fading, string? ActionKey, int OpenQuestions);

/// <summary>A named value of the inhabitant card, as text key and percent.</summary>
/// <param name="Key">Text key, e.g. <c>vocab.need.hunger</c>.</param>
/// <param name="Percent">0-100.</param>
public sealed record CardValue(string Key, int Percent);

/// <summary>
/// The inhabitant card (B6 R6), grouped by the player's questions (D366 1a): how is he
/// (needs, feelings, will to live), who is he (traits), what is he doing (action, open questions).
/// </summary>
/// <param name="Row">Summary row.</param>
/// <param name="Needs">Needs, satisfaction in percent, in vocabulary order.</param>
/// <param name="Feelings">Feelings above zero, strongest first.</param>
/// <param name="Traits">Character traits in vocabulary order.</param>
public sealed record InhabitantCard(InhabitantRow Row, IReadOnlyList<CardValue> Needs, IReadOnlyList<CardValue> Feelings, IReadOnlyList<CardValue> Traits);

/// <summary>World clock for the top bar: text keys plus numbers, formatted by the game.</summary>
/// <param name="SeasonKey"><c>time.season.&lt;id&gt;</c>.</param>
/// <param name="PhaseKey"><c>time.phase.&lt;id&gt;</c>.</param>
/// <param name="Year">Year since founding, from 1.</param>
/// <param name="Day">Day of the season, from 1.</param>
/// <param name="Time">hh:mm.</param>
public sealed record ClockView(string SeasonKey, string PhaseKey, long Year, int Day, string Time);

/// <summary>An open call for help as the screen shows it.</summary>
/// <param name="InhabitantId">Who.</param>
/// <param name="Number">Neutral number of the inhabitant.</param>
/// <param name="Need">Need id (vocabulary).</param>
public sealed record HelpRow(string InhabitantId, int Number, string Need);

/// <summary>
/// Read side of the UI (ADR-0008): turns the core's snapshots and event stream into what the
/// windows show. Snapshots are taken only when the tick changed; events are fetched with a cursor so
/// nothing is lost in fast-forward (several ticks per frame). A feed belongs to exactly one
/// <see cref="SimulationHost"/>: after loading a save game (new host) create a new feed, because the
/// snapshot cache only watches the tick.
/// </summary>
public sealed class WorldFeed
{
    private readonly SimulationHost _host;
    private long _eventCursor;
    private long _snapshotTick = -1;
    private WorldSnapshot? _snapshot;

    /// <summary>Creates a feed for a running simulation.</summary>
    public WorldFeed(SimulationHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _eventCursor = host.Inhabitants.LastEventSeq;
    }

    /// <summary>The simulation this feed reads.</summary>
    public SimulationHost Host => _host;

    /// <summary>True if events were lost because the core's buffer overflowed since the previous fetch.</summary>
    public bool MissedEvents { get; private set; }

    /// <summary>How often events were lost since the feed was created (stays visible after the frame).</summary>
    public int MissedEventBatches { get; private set; }

    /// <summary>Current snapshot (new only when the tick advanced).</summary>
    public WorldSnapshot Snapshot
    {
        get
        {
            if (_snapshot is null || _snapshotTick != _host.World.Tick)
            {
                _snapshot = WorldView.Snapshot(_host.World, _host.Inhabitants, _host.Objects, _host.Decisions);
                _snapshotTick = _host.World.Tick;
            }

            return _snapshot;
        }
    }

    /// <summary>All events since the previous call (in order), e.g. for question marks and sounds.</summary>
    public IReadOnlyList<WorldEvent> NewEvents()
    {
        var events = _host.Inhabitants.EventsSince(_eventCursor);
        MissedEvents = events.Count > 0 && events[0].Seq != _eventCursor + 1;
        if (MissedEvents)
        {
            MissedEventBatches++;
        }

        if (events.Count > 0)
        {
            _eventCursor = events[^1].Seq;
        }

        return [.. events.Select(e => e.Event)];
    }

    /// <summary>Rows of the inhabitant list in id order.</summary>
    public IReadOnlyList<InhabitantRow> Inhabitants() => [.. Snapshot.Inhabitants.Select(Row)];

    /// <summary>The card of one inhabitant, or null if it is no longer there.</summary>
    public InhabitantCard? Card(string inhabitantId)
    {
        var inhabitant = Snapshot.Inhabitants.FirstOrDefault(i => i.Id == inhabitantId);
        return inhabitant is null
            ? null
            : new InhabitantCard(
                Row(inhabitant),
                Values("need", inhabitant.Needs),
                [.. Values("mood", inhabitant.Moods).Where(v => v.Percent > 0).OrderByDescending(v => v.Percent).ThenBy(v => v.Key, StringComparer.Ordinal)],
                Values("trait", inhabitant.Traits));
    }

    /// <summary>
    /// The open-questions window (B2487): who has how many questions, never the wording; most first.
    /// </summary>
    public IReadOnlyList<(string InhabitantId, int Count)> OpenQuestions() =>
        [.. Snapshot.Inhabitants.Where(i => i.OpenQuestionIds.Count > 0)
            .OrderByDescending(i => i.OpenQuestionIds.Count).ThenBy(i => i.Id, StringComparer.Ordinal)
            .Select(i => (i.Id, i.OpenQuestionIds.Count))];

    /// <summary>Current favour and its maximum (B6 R9).</summary>
    public (double Value, double Maximum) Favor() => (_host.Player.Favor, _host.Content.Favor.Maximum);

    /// <summary>Open calls for help (A1340): who and which need, oldest first.</summary>
    public IReadOnlyList<HelpRow> HelpCalls() =>
        [.. _host.Player.Open.Select(c => new HelpRow(c.InhabitantId, NumberOf(c.InhabitantId), c.Need))];

    /// <summary>The world clock.</summary>
    public ClockView Clock()
    {
        var time = Snapshot.Time;
        return new ClockView(
            $"time.season.{time.SeasonId}",
            $"time.phase.{Phase(time.Phase)}",
            time.Year,
            time.DayOfSeason,
            string.Create(CultureInfo.InvariantCulture, $"{time.Hour:00}:{time.Minute:00}"));
    }

    /// <summary>Neutral number from an id such as <c>inh-000004</c> (the UI shows "Inhabitant 4").</summary>
    public static int NumberOf(string inhabitantId)
    {
        ArgumentNullException.ThrowIfNull(inhabitantId);
        var digits = new string([.. inhabitantId.Reverse().TakeWhile(char.IsAsciiDigit).Reverse()]);
        return digits.Length == 0 ? 0 : int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static InhabitantRow Row(InhabitantSnapshot i) =>
        new(i.Id, NumberOf(i.Id), Percent(i.WillToLive), i.Fading, i.Action is null ? null : $"vocab.action.{i.Action}", i.OpenQuestionIds.Count);

    private static List<CardValue> Values(string kind, IReadOnlyDictionary<string, double> values) =>
        [.. values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => new CardValue($"vocab.{kind}.{v.Key}", Percent(v.Value)))];

    private static int Percent(double unit) => (int)Math.Round(Math.Clamp(unit, 0, 1) * 100, MidpointRounding.AwayFromZero);

    /// <summary>Vocabulary id of a day phase (time_of_day), e.g. <c>dawn</c>.</summary>
    public static string Phase(Simulation.Time.DayPhase phase) =>
#pragma warning disable CA1308 // Vocabulary ids are lower-case by definition (ADR-0007).
        phase.ToString().ToLowerInvariant();
#pragma warning restore CA1308
}
