using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Presentation.World;

/// <summary>How a symbol moves.</summary>
public enum SymbolMotion
{
    /// <summary>A short feeling: rises and fades once.</summary>
    Rise,

    /// <summary>A lasting feeling or state: circles while it lasts.</summary>
    Orbit,
}

/// <summary>A symbol above an inhabitant right now.</summary>
/// <param name="InhabitantId">Above whom.</param>
/// <param name="Symbol">Symbol id (data).</param>
/// <param name="Motion">How it moves.</param>
/// <param name="StartedAt">Real seconds when it began (the motion's phase).</param>
public sealed record SymbolView(string InhabitantId, string Symbol, SymbolMotion Motion, double StartedAt);

/// <summary>What the planner reads about one inhabitant each update.</summary>
/// <param name="Id">Inhabitant id.</param>
/// <param name="Moods">Mood values (0..1) by mood id.</param>
/// <param name="Conditions">Active condition ids (B2543; empty until the core has conditions).</param>
public sealed record SymbolSubject(string Id, IReadOnlyDictionary<string, double> Moods, IReadOnlyCollection<string> Conditions);

/// <summary>
/// Feeling symbols above the head (B2541 part 4, B2543): which symbols show now, from the data's
/// triggers. A core event or a mood crossing its threshold upward gives a short rising symbol (then a
/// cooldown); a mood staying above its threshold or an active condition gives a circling symbol while it
/// lasts. At most the data's number per inhabitant (lasting ones first). Presentation only: the symbols
/// show what the core already knows and never change it. Real seconds drive it.
/// </summary>
public sealed class EmotionSymbolPlanner
{
    private readonly EmotionSymbols _data;
    private readonly List<EmotionTrigger> _triggers;
    private readonly List<(SymbolView View, double Ends)> _short = [];
    private readonly Dictionary<(string Inhabitant, string Symbol), double> _cooldownUntil = [];
    private readonly HashSet<(string Inhabitant, int Trigger)> _above = [];
    private readonly Dictionary<(string Inhabitant, string Symbol), double> _lastingSince = [];

    /// <summary>Creates the planner; throws <see cref="InvalidDataException"/> for inconsistent data.</summary>
    /// <param name="data">The symbol set (content/ui/emotion_symbols.json).</param>
    /// <param name="moods">Mood vocabulary ids (a trigger naming another mood is refused).</param>
    public EmotionSymbolPlanner(EmotionSymbols data, IReadOnlyCollection<string> moods)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        ArgumentNullException.ThrowIfNull(moods);
        _triggers = [.. data.Triggers];
        var symbols = data.Symbols.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        problems.AddRange(data.Symbols.GroupBy(s => s.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"symbol '{g.Key}' twice"));
        foreach (var t in data.Triggers)
        {
            var sources = (t.Event is null ? 0 : 1) + (t.Mood is null ? 0 : 1) + (t.Condition is null ? 0 : 1);
            if (!symbols.Contains(t.Symbol))
            {
                problems.Add($"trigger names unknown symbol '{t.Symbol}'");
            }

            if (sources != 1)
            {
                problems.Add($"trigger for '{t.Symbol}' needs exactly one of event, mood, condition");
            }
            else if ((t.Event is not null && t.Mode != EmotionTriggerMode.Once)
                || (t.Mood is not null && t.Mode == EmotionTriggerMode.Once)
                || (t.Condition is not null && t.Mode != EmotionTriggerMode.Holds))
            {
                problems.Add($"trigger for '{t.Symbol}': mode {t.Mode} does not fit its source");
            }

            if (t.Mood is { } mood && !moods.Contains(mood))
            {
                problems.Add($"trigger names unknown mood '{mood}'");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException("Emotion symbols: " + string.Join("; ", problems) + ".");
        }
    }

    /// <summary>Advances to <paramref name="now"/> (real seconds) and returns the symbols to show, per inhabitant in a stable order.</summary>
    /// <param name="now">Real seconds.</param>
    /// <param name="events">Core events since the last update.</param>
    /// <param name="subjects">The inhabitants with their moods and conditions.</param>
    public IReadOnlyList<SymbolView> Update(double now, IEnumerable<WorldEvent> events, IReadOnlyList<SymbolSubject> subjects)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(subjects);
        _short.RemoveAll(s => s.Ends <= now);

        foreach (var e in events)
        {
            foreach (var t in _triggers.Where(t => t.Event == e.Kind))
            {
                StartShort(e.InhabitantId, t, now);
            }
        }

        var lasting = new List<SymbolView>();
        foreach (var subject in subjects)
        {
            for (var i = 0; i < _triggers.Count; i++)
            {
                var t = _triggers[i];
                var on = t.Mood is { } mood
                    ? subject.Moods.GetValueOrDefault(mood) >= t.Threshold
                    : t.Condition is { } condition && subject.Conditions.Contains(condition);
                var key = (subject.Id, i);
                var was = _above.Contains(key);
                if (on && !was && t.Mode == EmotionTriggerMode.Rises)
                {
                    StartShort(subject.Id, t, now);
                }

                if (on)
                {
                    _above.Add(key);
                }
                else
                {
                    _above.Remove(key);
                }

                if (on && t.Mode == EmotionTriggerMode.Holds && !lasting.Any(l => l.InhabitantId == subject.Id && l.Symbol == t.Symbol))
                {
                    var since = _lastingSince.TryGetValue((subject.Id, t.Symbol), out var s) ? s : now;
                    lasting.Add(new SymbolView(subject.Id, t.Symbol, SymbolMotion.Orbit, since));
                }
            }
        }

        // Lasting symbols keep their phase while they last.
        foreach (var gone in _lastingSince.Keys.Where(k => !lasting.Any(l => l.InhabitantId == k.Inhabitant && l.Symbol == k.Symbol)).ToList())
        {
            _lastingSince.Remove(gone);
        }

        foreach (var l in lasting)
        {
            _lastingSince.TryAdd((l.InhabitantId, l.Symbol), l.StartedAt);
        }

        // At most max_per_inhabitant: lasting first, then the newest short ones.
        return [.. lasting.Concat(_short.OrderByDescending(s => s.View.StartedAt).Select(s => s.View))
            .GroupBy(v => v.InhabitantId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .SelectMany(g => g.Take(_data.MaxPerInhabitant))];
    }

    private void StartShort(string inhabitantId, EmotionTrigger trigger, double now)
    {
        var key = (inhabitantId, trigger.Symbol);
        if (_cooldownUntil.TryGetValue(key, out var until) && now < until)
        {
            return;
        }

        _cooldownUntil[key] = now + Math.Max(trigger.CooldownS, _data.Rise.DurationS);
        _short.Add((new SymbolView(inhabitantId, trigger.Symbol, SymbolMotion.Rise, now), now + _data.Rise.DurationS));
    }
}
