using System.Diagnostics;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Simulation.Headless;

/// <summary>
/// Times the parts of the world's steps (<see cref="IStepProbe"/>) with the monotonic clock. Every part
/// counts its own time only: a part that begins inside another one (the decider port inside the decisions
/// system, a part <c>system/part</c> inside its system) is taken out of the enclosing one, so the parts of a
/// step never overlap and add up to the time the step spent in them. Parts are grouped by the name before
/// the first slash (a system with the parts it names). A step is kept with <see cref="Keep"/> or dropped
/// with <see cref="Drop"/> (warm-up).
/// </summary>
/// <param name="clock">Monotonic clock in stopwatch ticks (tests pass their own); null = the stopwatch.</param>
internal sealed class StepTimer(Func<long>? clock = null) : IStepProbe
{
    private readonly Func<long> _clock = clock ?? Stopwatch.GetTimestamp;
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<string> _names = [];
    private readonly List<Times> _parts = [];
    private readonly Dictionary<string, Times> _groups = new(StringComparer.Ordinal);
    private readonly Stack<(int Part, long Started, long Inner)> _open = new();

    /// <summary>Part names in the order they first began.</summary>
    public IReadOnlyList<string> Parts => _names;

    /// <summary>Steps kept so far.</summary>
    public long Steps { get; private set; }

    /// <summary>The group of a part: its name up to the first slash.</summary>
    public static string GroupOf(string part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var slash = part.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? part : part[..slash];
    }

    /// <inheritdoc/>
    public void Enter(string part)
    {
        if (!_index.TryGetValue(part, out var index))
        {
            _index[part] = index = _names.Count;
            _names.Add(part);
            var group = GroupOf(part);
            if (!_groups.TryGetValue(group, out var times))
            {
                _groups[group] = times = new Times();
            }

            _parts.Add(new Times { Group = times });
        }

        _open.Push((index, _clock(), 0));
    }

    /// <inheritdoc/>
    public void Leave(string part)
    {
        var now = _clock();
        var (index, started, inner) = _open.Pop();
        if (!string.Equals(_names[index], part, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Step part '{part}' ends, but '{_names[index]}' began last.");
        }

        var whole = now - started;
        _parts[index].StepCalls++;
        _parts[index].Step += whole - inner;
        _parts[index].Group!.Step += whole - inner;
        if (_open.Count > 0)
        {
            var outer = _open.Pop();
            _open.Push((outer.Part, outer.Started, outer.Inner + whole));
        }
    }

    /// <summary>Milliseconds every part took alone in the step that just ended, in the order of <see cref="Parts"/>.</summary>
    public double[] StepMs()
    {
        var ms = new double[_parts.Count];
        for (var i = 0; i < ms.Length; i++)
        {
            ms[i] = Ms(_parts[i].Step);
        }

        return ms;
    }

    /// <summary>Time of a group in the step that is running or just ended (stopwatch ticks).</summary>
    public long InStep(string group) => _groups.TryGetValue(group, out var times) ? times.Step : 0;

    /// <summary>Keeps the step that just ended in the sums and starts the next one.</summary>
    public void Keep()
    {
        foreach (var times in _parts.Concat(_groups.Values))
        {
            times.Sum += times.Step;
            times.Max = Math.Max(times.Max, times.Step);
            times.Step = 0;
            times.Calls += times.StepCalls;
            times.StepCalls = 0;
        }

        Steps++;
    }

    /// <summary>Forgets the step that just ended (warm-up).</summary>
    public void Drop()
    {
        foreach (var times in _parts.Concat(_groups.Values))
        {
            times.Step = 0;
            times.StepCalls = 0;
        }
    }

    /// <summary>Mean milliseconds per kept step in a part alone.</summary>
    public double MeanMs(string part) => _index.TryGetValue(part, out var index) ? Mean(_parts[index]) : 0;

    /// <summary>How often a part began per kept step, on average.</summary>
    public double MeanCalls(string part) => Steps == 0 || !_index.TryGetValue(part, out var index) ? 0 : (double)_parts[index].Calls / Steps;

    /// <summary>Most milliseconds a kept step spent in a part alone.</summary>
    public double MaxMs(string part) => _index.TryGetValue(part, out var index) ? Ms(_parts[index].Max) : 0;

    /// <summary>Mean milliseconds per kept step in all parts of a group together.</summary>
    public double GroupMeanMs(string group) => _groups.TryGetValue(group, out var times) ? Mean(times) : 0;

    /// <summary>Most milliseconds a kept step spent in all parts of a group together.</summary>
    public double GroupMaxMs(string group) => _groups.TryGetValue(group, out var times) ? Ms(times.Max) : 0;

    private double Mean(Times times) => Steps == 0 ? 0 : Ms(times.Sum) / Steps;

    private static double Ms(long stopwatchTicks) => Stopwatch.GetElapsedTime(0, stopwatchTicks).TotalMilliseconds;

    private sealed class Times
    {
        public Times? Group { get; init; }

        public long Step { get; set; }

        public long Sum { get; set; }

        public long Max { get; set; }

        public long StepCalls { get; set; }

        public long Calls { get; set; }
    }
}

/// <summary>
/// A decider port whose own work is timed as the part <see cref="Part"/>: with the mock that is the mock
/// computing its answers inside the step, which is not the core's work (in the game the AI service does it
/// in its own process).
/// </summary>
internal sealed class TimedDecisionPort(IDecisionPort inner, IStepProbe probe) : IDecisionPort
{
    /// <summary>Part name of the time inside the port.</summary>
    public const string Part = "decider_port";

    /// <summary>Requests sent so far.</summary>
    public long Sent { get; private set; }

    /// <summary>Messages (answers and errors) received so far.</summary>
    public long Received { get; private set; }

    /// <inheritdoc/>
    public void Send(string requestId, string requestJson)
    {
        Sent++;
        probe.Enter(Part);
        inner.Send(requestId, requestJson);
        probe.Leave(Part);
    }

    /// <inheritdoc/>
    public IReadOnlyList<PortMessage> Receive(long tick)
    {
        probe.Enter(Part);
        var messages = inner.Receive(tick);
        probe.Leave(Part);
        Received += messages.Count;
        return messages;
    }
}

/// <summary>A decider that never answers: every question it is asked stays open.</summary>
internal sealed class SilentDecisionPort : IDecisionPort
{
    /// <inheritdoc/>
    public void Send(string requestId, string requestJson)
    {
    }

    /// <inheritdoc/>
    public IReadOnlyList<PortMessage> Receive(long tick) => [];
}
