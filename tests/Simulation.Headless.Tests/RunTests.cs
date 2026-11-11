using System.Diagnostics;
using System.Text.Json;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.State;

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
    public void TheSummaryCountsTheRequestsAndTheOnesTheServiceRefused()
    {
        // ADR-0009: a check run validates what it sends, and its summary names the requests the service refused.
        var (code, lines) = Run("--days", "1", "--quiet");
        Assert.Equal(ExitCodes.Ok, code);
        var requests = Assert.Single(lines, l => l.StartsWith("requests  ", StringComparison.Ordinal));
        Assert.Matches("^requests  decisions [1-9][0-9]*  jobs 0  conversations 0  rejected 0$", requests);
    }

    [Fact]
    public void ARefusedRequestIsNamedInTheSummaryWithWhatTheServiceSaid()
    {
        // A decider that refuses every request as the service refuses an invalid one.
        var (_, lines) = Run(new RunCommand { Connect = (_, _) => new RefusingConnection("Invalid DecisionRequest: options is empty") }, "--days", "1", "--quiet");
        var requests = Assert.Single(lines, l => l.StartsWith("requests  ", StringComparison.Ordinal));
        var rejected = int.Parse(requests.Split("rejected ")[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(rejected > 0);
        var named = lines.Where(l => l.StartsWith("REQUEST_REJECTED ", StringComparison.Ordinal)).ToList();
        Assert.Equal(rejected, named.Count);
        Assert.All(named, l => Assert.Matches("^REQUEST_REJECTED tick=[0-9]+ request=[0-9a-f-]{36} message=Invalid%20DecisionRequest:%20options%20is%20empty$", l));
        Assert.StartsWith("FINAL hash=", lines[^1], StringComparison.Ordinal);
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
        Assert.Equal($"REPLAY_OK {final} ticks=1440 content={ContentFingerprint.Compute(Content)}", output.ToString().Trim());
    }

    [Fact]
    public void AReplayOfARunStoppedMidDayCoversTheWholeRun()
    {
        var save = Path.Combine(_folder, "partial.db");
        var (_, lines) = Run("--days", "1", "--quiet", "--save", save, "--until", "question_answered:4");
        var final = lines[^1];
        var result = ReplayCommand.Replay(Content, SaveGameStore.Load(save));
        Assert.True(result.Ok, result.Line);
        Assert.StartsWith(final.Replace("FINAL", "REPLAY_OK", StringComparison.Ordinal).Replace("tick=", "ticks=", StringComparison.Ordinal) + " content=", result.Line, StringComparison.Ordinal);
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
        Assert.Equal(ExitCodes.ReplayDiff, result.Code);
        Assert.Equal($"REPLAY_DIFF tick={original.Tick} content={state.ContentFingerprint}", result.Line);
    }

    [Fact]
    public void ASaveWithAQuestionThatIsHeldBackReplaysWithoutAJobPort()
    {
        // A1487: a question held back while its inhabitant is busy is unsent, like one that waits for the
        // night - but it says nothing about a job port. The replay of a run without one must not start the
        // night's jobs (they would be in the replay's job log and not in the saved one).
        var host = SimulationHost.CreateGeneratedWorld(Content, 1, MockDecisionPort.FromContent(Content), DecisionsSystem.DefaultDisplayLocale, "start_island");
        var day = host.World.Calendar.TicksPerDay;
        while (host.World.Tick < 3 * day && !(host.World.Tick >= day && host.Decisions.Open.Any(q => q.RequestId.Length == 0)))
        {
            host.World.Step();
        }

        var state = StateCodec.Capture(host);
        Assert.Contains(state.Questions, q => q.RequestId.Length == 0);
        Assert.False(ReplayCommand.HadJobs(state.Inventions));
        var result = ReplayCommand.Replay(Content, state);
        Assert.True(result.Ok, result.Line);
        Assert.StartsWith($"REPLAY_OK hash={host.World.StateHash()} ticks={host.World.Tick}", result.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void ASaveMadeWithOtherContentIsNotReplayedUnlessForced()
    {
        var save = Path.Combine(_folder, "content.db");
        Run("--days", "1", "--quiet", "--save", save, "--until", "question_answered:2");
        var state = SaveGameStore.Load(save);
        var now = ContentFingerprint.Compute(Content);
        Assert.Equal(now, state.ContentFingerprint);
        Assert.StartsWith("sha256:", now, StringComparison.Ordinal);

        var other = state with { ContentFingerprint = "sha256:0000000000000000" };
        var refused = ReplayCommand.Replay(Content, other);
        Assert.Equal(ExitCodes.ContentChanged, refused.Code);
        Assert.Equal($"REPLAY_CONTENT_CHANGED saved=sha256:0000000000000000 now={now}", refused.Line);

        var forced = ReplayCommand.Replay(Content, other, force: true);
        Assert.True(forced.Ok, forced.Line);
        Assert.EndsWith($" content={now} saved_content=sha256:0000000000000000", forced.Line, StringComparison.Ordinal);

        // Old saves have no fingerprint and replay as before.
        Assert.True(ReplayCommand.Replay(Content, state with { ContentFingerprint = null }).Ok);
    }

    [Fact]
    public void TheReplayCommandHasItsOwnExitCodes()
    {
        var save = Path.Combine(_folder, "codes.db");
        Run("--days", "1", "--quiet", "--save", save, "--until", "question_answered:2");
        SaveGameStore.Save(save, SaveGameStore.Load(save) with { ContentFingerprint = "sha256:1111111111111111" });
        var output = new StringWriter();
        Assert.Equal(ExitCodes.ContentChanged, ReplayCommand.Run(["--save", save, "--content", Content], output));
        Assert.Equal(ExitCodes.Ok, ReplayCommand.Run(["--save", save, "--content", Content, "--force"], output));
        Assert.Equal(5, ExitCodes.ReplayDiff);
        Assert.Equal(6, ExitCodes.ContentChanged);
    }

    [Fact]
    public void ARunningGameServiceIsNotJoinedByASecondOne()
    {
        // A fake game lock that names this (living) test process; nothing is started.
        using var self = Process.GetCurrentProcess();
        var gameLock = Path.Combine(_folder, "ai-service.lock");
        File.WriteAllText(gameLock, string.Create(System.Globalization.CultureInfo.InvariantCulture, $$"""{"pid": {{Environment.ProcessId}}, "port": 51234, "started_utc": "x", "start_marker": {{self.StartTime.ToFileTimeUtc()}}}"""));
        var command = new RunCommand { Connect = (o, w) => LiveConnection.Start(o.ContentDirectory, 1000, w, gameLock) };
        var (code, lines) = Run(command, "--days", "1", "--ai", "live");
        Assert.Equal(ExitCodes.AiUnavailable, code);
        Assert.StartsWith($"AI_SERVICE_UNAVAILABLE {LiveConnection.GameServiceRunning} (pid {Environment.ProcessId}", lines.Single(), StringComparison.Ordinal);
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
        // A fake of the live connection: an answer is out for a fixed number of ticks, like an HTTP answer
        // that arrives later. Clock and sleep are fakes too (the clock only moves when the runner sleeps), so
        // nothing depends on machine speed. Not the real service: CI has no GPU and never starts it.
        var fake = new DelayedConnection(delayTicks: 5);
        var sleeps = new List<TimeSpan>();
        var now = TimeSpan.Zero;
        var command = new RunCommand
        {
            Connect = (_, _) => fake,
            BusyTickInterval = TimeSpan.FromMilliseconds(2),
            Clock = () => now,
            Sleep = w =>
            {
                sleeps.Add(w);
                now += w;
            },
        };
        var path = Path.Combine(_folder, "live.jsonl");
        var (code, lines) = Run(command, "--days", "1", "--quiet", "--ai", "live", "--until", "question_answered:2", "--log-json", path);
        Assert.Equal(ExitCodes.Ok, code);
        Assert.True(fake.Disposed);
        // Paced at the busy interval while an answer was out, and only then (one wait per busy tick).
        Assert.NotEmpty(sleeps);
        Assert.All(sleeps, s => Assert.Equal(TimeSpan.FromMilliseconds(2), s));
        Assert.Equal(fake.BusyTicks, sleeps.Count);

        // The world ran on while the answer was out: it took effect exactly when the port handed it over.
        var waits = File.ReadAllLines(path)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("kind").GetString() == "response")
            .Select(e => e.GetProperty("meta").GetProperty("wait_ticks").GetInt64())
            .ToList();
        Assert.NotEmpty(waits);
        Assert.All(waits, w => Assert.Equal(5, w));
        Assert.StartsWith("FINAL hash=", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AnExceptionStillClosesThePortAndPrintsFinal()
    {
        var fake = new DelayedConnection(delayTicks: 1) { FailAfterReceives = 50 };
        var output = new StringWriter();
        var command = new RunCommand { Connect = (_, _) => fake };
        Assert.Throws<InvalidOperationException>(() => command.Run(HeadlessOptions.Parse(["--days", "1", "--quiet", "--content", Content]), output, CancellationToken.None));
        Assert.True(fake.Disposed);
        Assert.Contains("FINAL hash=", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CtrlCStopsTheRunPrintsFinalAndClosesThePort()
    {
        var fake = new DelayedConnection(delayTicks: 1);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var output = new StringWriter();
        var code = new RunCommand { Connect = (_, _) => fake }.Run(HeadlessOptions.Parse(["--days", "1", "--quiet", "--content", Content]), output, stop.Token);
        Assert.Equal(ExitCodes.Interrupted, code);
        Assert.True(fake.Disposed);
        Assert.EndsWith("tick=0", output.ToString().Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Answers like the mock, but only <c>delayTicks</c> ticks after the question, from the pending queue (as the
    /// HTTP port hands over what has arrived). Busy while an answer is out; counts the busy ticks the runner saw.
    /// </summary>
    /// <summary>A decider that refuses every request with a ServiceError bad_request and the given words.</summary>
    private sealed class RefusingConnection(string message) : IAiConnection, IDecisionPort
    {
        private readonly Queue<PortMessage> _ready = new();

        public IDecisionPort Decisions => this;

        public IJobPort? Jobs => null;

        public bool Busy => false;

        public void Send(string requestId, string requestJson)
        {
            var request = GlassWorld.Contracts.Validation.ContractSerializer.Deserialize<GlassWorld.Contracts.DecisionRequest>(requestJson);
            _ready.Enqueue(new PortMessage(requestId, GlassWorld.Contracts.Validation.ContractSerializer.Serialize(new GlassWorld.Contracts.ServiceError
            {
                ContractVersion = request.ContractVersion,
                RequestId = request.RequestId,
                Tick = request.Tick,
                Code = GlassWorld.Contracts.ServiceErrorCode.Bad_request,
                Message = message,
                Retryable = false,
            }), IsError: true));
        }

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            var messages = _ready.ToList();
            _ready.Clear();
            return messages;
        }

        public void Dispose()
        {
        }
    }

    private sealed class DelayedConnection(int delayTicks) : IAiConnection, IDecisionPort
    {
        private readonly MockDecisionPort _mock = new();
        private readonly List<(long Due, PortMessage Message)> _pending = [];
        private long _tick;
        private int _receives;

        public int FailAfterReceives { get; init; } = int.MaxValue;

        public bool Disposed { get; private set; }

        /// <summary>Ticks at which the runner asked and an answer was out.</summary>
        public int BusyTicks { get; private set; }

        public IDecisionPort Decisions => this;

        public IJobPort? Jobs => null;

        public bool Busy
        {
            get
            {
                var busy = _pending.Count > 0;
                BusyTicks += busy ? 1 : 0;
                return busy;
            }
        }

        public void Send(string requestId, string requestJson)
        {
            _mock.Send(requestId, requestJson);
            foreach (var message in _mock.Receive(_tick))
            {
                _pending.Add((_tick + delayTicks, message));
            }
        }

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            if (++_receives > FailAfterReceives)
            {
                throw new InvalidOperationException("port broke");
            }

            _tick = tick;
            var ready = _pending.Where(p => p.Due <= tick).Select(p => p.Message).ToList();
            _pending.RemoveAll(p => p.Due <= tick);
            return ready;
        }

        public void Dispose() => Disposed = true;
    }
}
