using System.Diagnostics;
using System.Text.Json;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Simulation.Headless.Tests;

public sealed class RunTests : IDisposable
{
    private static readonly string Content = ContentFolder.Find();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gw-headless-" + Guid.NewGuid().ToString("N"));

    public RunTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        // Temporary files only; SQLite may still hold a handle for a moment on Windows.
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static (int Code, string[] Lines) Run(RunCommand command, params string[] args)
    {
        var output = new StringWriter();
        var code = command.Run(HeadlessOptions.Parse([.. args, "--content", Content]), output, CancellationToken.None);
        return (code, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static (int Code, string[] Lines) Run(params string[] args) => Run(new RunCommand(), args);

    [Fact]
    public void UntilStopsAtTheCountedEventAndEndsWithFinal()
    {
        var (code, lines) = Run("--days", "1", "--quiet", "--until", "question_answered:3", "--events", "question_answered");
        Assert.Equal(ExitCodes.Ok, code);
        var answered = lines.Where(l => l.StartsWith("EVENT ", StringComparison.Ordinal)).ToList();
        // A tick is never cut short: the run stops after the tick of the third answer, which may hold more.
        Assert.True(answered.Count >= 3);
        Assert.All(answered, l => Assert.Contains(" kind=question_answered ", l, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith($"UNTIL kind=question_answered count={answered.Count}/3 reached", StringComparison.Ordinal));
        Assert.Contains("ACCEPTANCE_OK", lines);
        Assert.Matches("^FINAL hash=[0-9a-f]{64} tick=[0-9]+$", lines[^1]);
        var finalTick = long.Parse(lines[^1].Split("tick=")[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(finalTick < 1440);
        Assert.StartsWith($"EVENT tick={finalTick - 1} ", answered[2], StringComparison.Ordinal);
    }

    [Fact]
    public void UntilNotReachedWithinTheDaysGivesExitCodeThree()
    {
        // The mock decider never sends a thought.
        var (code, lines) = Run("--days", "1", "--quiet", "--until", "thought");
        Assert.Equal(ExitCodes.UntilNotReached, code);
        Assert.Contains(lines, l => l.StartsWith("UNTIL kind=thought count=0/1 not_reached tick=1440", StringComparison.Ordinal));
        Assert.StartsWith("FINAL hash=", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void EventLinesHaveTheAgreedShape()
    {
        var (_, lines) = Run("--days", "1", "--quiet", "--events", "all", "--until", "action_finished:5");
        var events = lines.Where(l => l.StartsWith("EVENT ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(events);
        Assert.All(events, l => Assert.Matches("^EVENT tick=[0-9]+ kind=[a-z_]+ inhabitant=\\S+ action=\\S+ target=\\S+ detail=\\S+$", l));
    }

    [Fact]
    public void UnknownEventKindsAreRefused() =>
        Assert.Throws<ArgumentException>(() => Run("--days", "1", "--events", "no_such_event"));

    [Fact]
    public void TheJsonLogHasOneLinePerLogEntry()
    {
        var path = Path.Combine(_folder, "log.jsonl");
        var (code, _) = Run("--days", "1", "--quiet", "--until", "question_answered:2", "--log-json", path);
        Assert.Equal(ExitCodes.Ok, code);
        var lines = File.ReadAllLines(path);
        Assert.NotEmpty(lines);
        var parsed = lines.Select(l => JsonDocument.Parse(l).RootElement).ToList();
        var request = parsed.First(e => e.GetProperty("kind").GetString() == "request");
        Assert.Equal("decision", request.GetProperty("log").GetString());
        Assert.True(request.GetProperty("request").TryGetProperty("seed", out _));
        var response = parsed.First(e => e.GetProperty("kind").GetString() == "response");
        Assert.Equal("mock", response.GetProperty("meta").GetProperty("model").GetString());
        Assert.True(response.GetProperty("meta").GetProperty("wait_ticks").GetInt64() >= 0);
        Assert.Equal(response.GetProperty("request_id").GetString(), response.GetProperty("response").GetProperty("request_id").GetString());
    }

    [Fact]
    public void AReplayOfASaveGivesTheSameHash()
    {
        var save = Path.Combine(_folder, "run.db");
        var (code, lines) = Run("--days", "1", "--quiet", "--save", save);
        Assert.Equal(ExitCodes.Ok, code);
        var final = lines[^1].Split(' ')[1];

        var output = new StringWriter();
        Assert.Equal(ExitCodes.Ok, ReplayCommand.Run(["--save", save, "--content", Content], output));
        Assert.Equal($"REPLAY_OK {final} ticks=1440", output.ToString().Trim());
    }

    [Fact]
    public void AReplayOfARunStoppedMidDayCoversTheWholeRun()
    {
        var save = Path.Combine(_folder, "partial.db");
        var (_, lines) = Run("--days", "1", "--quiet", "--save", save, "--until", "question_answered:4");
        var final = lines[^1];
        var result = ReplayCommand.Replay(Content, SaveGameStore.Load(save));
        Assert.True(result.Ok, result.Line);
        Assert.Equal(final.Replace("FINAL", "REPLAY_OK", StringComparison.Ordinal).Replace("tick=", "ticks=", StringComparison.Ordinal), result.Line);
    }

    [Fact]
    public void ATamperedAnswerIsFoundAtItsTick()
    {
        var save = Path.Combine(_folder, "tamper.db");
        Run("--days", "1", "--quiet", "--save", save);
        var state = SaveGameStore.Load(save);
        var entries = state.DecisionLog.ToList();
        var index = entries.FindIndex(e => e.Kind == DecisionLogKind.Response && e.Tick > 100);
        Assert.True(index >= 0);
        var original = entries[index];
        var answer = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(original.Json)!;
        var option = answer["option_id"].GetString()!;
        entries[index] = original with { Json = original.Json.Replace($"\"option_id\":\"{option}\"", "\"option_id\":\"o_tampered\"", StringComparison.Ordinal) };
        Assert.NotEqual(original.Json, entries[index].Json);

        var result = ReplayCommand.Replay(Content, state with { DecisionLog = entries });
        Assert.False(result.Ok);
        Assert.Equal($"REPLAY_DIFF tick={original.Tick}", result.Line);
    }

    [Fact]
    public void AnUnavailableServiceGivesExitCodeFour()
    {
        var command = new RunCommand { Connect = (_, _) => throw new AiUnavailableException("no uv", new TimeoutException()) };
        var (code, lines) = Run(command, "--days", "1", "--ai", "live");
        Assert.Equal(ExitCodes.AiUnavailable, code);
        Assert.StartsWith("AI_SERVICE_UNAVAILABLE ", lines.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void LiveStyleAnswersArriveWhileTheWorldRunsOnAndThePortIsClosedAtTheEnd()
    {
        // A fake of the live connection: answers come in real time on another thread, like the HTTP port.
        // Not the real service: CI has no GPU and never starts it (the live path is checked by hand).
        var fake = new DelayedConnection(TimeSpan.FromMilliseconds(30));
        var sleeps = new List<TimeSpan>();
        var command = new RunCommand
        {
            Connect = (_, _) => fake,
            BusyTickInterval = TimeSpan.FromMilliseconds(2),
            Sleep = w =>
            {
                sleeps.Add(w);
                Thread.Sleep(w);
            },
        };
        var path = Path.Combine(_folder, "live.jsonl");
        var (code, lines) = Run(command, "--days", "1", "--quiet", "--ai", "live", "--until", "question_answered:2", "--log-json", path);
        Assert.Equal(ExitCodes.Ok, code);
        Assert.True(fake.Disposed);
        Assert.NotEmpty(sleeps);
        Assert.All(sleeps, s => Assert.True(s <= TimeSpan.FromMilliseconds(2)));

        // The world ran on while the answer was out: it took effect several ticks after the question.
        var waits = File.ReadAllLines(path)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("kind").GetString() == "response")
            .Select(e => e.GetProperty("meta").GetProperty("wait_ticks").GetInt64())
            .ToList();
        Assert.NotEmpty(waits);
        Assert.All(waits, w => Assert.True(w > 1, $"answer took effect after {w} ticks"));
        Assert.StartsWith("FINAL hash=", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AnExceptionStillClosesThePortAndPrintsFinal()
    {
        var fake = new DelayedConnection(TimeSpan.Zero) { FailAfterReceives = 50 };
        var output = new StringWriter();
        var command = new RunCommand { Connect = (_, _) => fake };
        Assert.Throws<InvalidOperationException>(() => command.Run(HeadlessOptions.Parse(["--days", "1", "--quiet", "--content", Content]), output, CancellationToken.None));
        Assert.True(fake.Disposed);
        Assert.Contains("FINAL hash=", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CtrlCStopsTheRunPrintsFinalAndClosesThePort()
    {
        var fake = new DelayedConnection(TimeSpan.Zero);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var output = new StringWriter();
        var code = new RunCommand { Connect = (_, _) => fake }.Run(HeadlessOptions.Parse(["--days", "1", "--quiet", "--content", Content]), output, stop.Token);
        Assert.Equal(ExitCodes.Interrupted, code);
        Assert.True(fake.Disposed);
        Assert.EndsWith("tick=0", output.ToString().Trim(), StringComparison.Ordinal);
    }

    /// <summary>Answers like the mock, but only after a real delay and from the pending queue, as the HTTP port does.</summary>
    private sealed class DelayedConnection(TimeSpan delay) : IAiConnection, IDecisionPort
    {
        private readonly MockDecisionPort _mock = new();
        private readonly List<(TimeSpan Due, PortMessage Message)> _pending = [];
        private readonly Lock _lock = new();
        private int _receives;

        public int FailAfterReceives { get; init; } = int.MaxValue;

        public bool Disposed { get; private set; }

        public IDecisionPort Decisions => this;

        public IJobPort? Jobs => null;

        public bool Busy
        {
            get
            {
                lock (_lock)
                {
                    return _pending.Count > 0;
                }
            }
        }

        public void Send(string requestId, string requestJson)
        {
            _mock.Send(requestId, requestJson);
            lock (_lock)
            {
                foreach (var message in _mock.Receive(0))
                {
                    _pending.Add((Stopwatch.GetElapsedTime(0) + delay, message));
                }
            }
        }

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            if (++_receives > FailAfterReceives)
            {
                throw new InvalidOperationException("port broke");
            }

            var now = Stopwatch.GetElapsedTime(0);
            lock (_lock)
            {
                var ready = _pending.Where(p => p.Due <= now).Select(p => p.Message).ToList();
                _pending.RemoveAll(p => p.Due <= now);
                return ready;
            }
        }

        public void Dispose() => Disposed = true;
    }
}
