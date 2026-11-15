using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Presentation.Ui;

/// <summary>
/// Who had an aha lately (A1752, B2792): an inhabitant's own event of the data's kinds (invented, built, a new word -
/// GW1 #27065) marks its portrait; the mark stays until the player clicks the portrait or the data's game hours have
/// passed (about one game day). Presentation only: it remembers the tick, the core is not asked.
/// </summary>
/// <param name="data">The portrait states (tokens): which events are an aha, and how long one stays.</param>
/// <param name="ticksPerHour">Ticks of one game hour (world calendar).</param>
public sealed class AhaMarks(UiPortraitStates data, int ticksPerHour)
{
    private readonly UiPortraitStates _data = data ?? throw new ArgumentNullException(nameof(data));
    private readonly int _ticksPerHour = Math.Max(1, ticksPerHour);
    private readonly Dictionary<string, long> _since = new(StringComparer.Ordinal);

    /// <summary>Takes new events: each of an aha kind marks its inhabitant (a later one starts the time anew).</summary>
    public void Take(IEnumerable<WorldEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var e in events)
        {
            if (_data.AhaEvents.Contains(e.Kind) && e.InhabitantId.Length > 0)
            {
                _since[e.InhabitantId] = e.Tick;
            }
        }
    }

    /// <summary>Whether the inhabitant's aha still shows at <paramref name="tick"/>; one that ran out is forgotten.</summary>
    public bool Shows(string inhabitantId, long tick)
    {
        ArgumentNullException.ThrowIfNull(inhabitantId);
        if (!_since.TryGetValue(inhabitantId, out var since))
        {
            return false;
        }

        if ((double)(tick - since) / _ticksPerHour < _data.AhaHours)
        {
            return true;
        }

        _since.Remove(inhabitantId);
        return false;
    }

    /// <summary>The player clicked the portrait: the aha has been seen.</summary>
    public void Seen(string inhabitantId)
    {
        ArgumentNullException.ThrowIfNull(inhabitantId);
        _since.Remove(inhabitantId);
    }
}
