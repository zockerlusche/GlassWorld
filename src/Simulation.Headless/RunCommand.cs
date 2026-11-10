using System.Diagnostics;
using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Headless;

/// <summary>Exit codes of the headless runner.</summary>
internal static class ExitCodes
{
    /// <summary>Done (condition reached or all days run) and acceptance OK; also REPLAY_OK.</summary>
    public const int Ok = 0;

    /// <summary>Bad arguments.</summary>
    public const int Usage = 1;

    /// <summary>Acceptance failed.</summary>
    public const int AcceptanceFailed = 2;

    /// <summary><c>--until</c> not reached within <c>--days</c>.</summary>
    public const int UntilNotReached = 3;

    /// <summary>The AI service could not be started, or a game's AI service is running (<c>--ai live</c>).</summary>
    public const int AiUnavailable = 4;

    /// <summary>Replay: REPLAY_DIFF (the replay departed from the saved run).</summary>
    public const int ReplayDiff = 5;

    /// <summary>Replay: REPLAY_CONTENT_CHANGED (the save was made with other content data; nothing replayed).</summary>
    public const int ContentChanged = 6;

    /// <summary>Stopped with Ctrl+C (the AI service is stopped, FINAL is printed).</summary>
    public const int Interrupted = 130;
}

/// <summary>
/// The run command: builds the placeholder world or the island of a preset, advances it day by day and
/// prints the acceptance lines; optionally with the live AI, event lines, a stop condition, a JSON log and
/// an autosave. Always ends with <c>FINAL hash=.. tick=..</c>.
/// </summary>
internal sealed class RunCommand
{
    /// <summary>Opens the AI connection for the options (tests pass a fake).</summary>
    public Func<HeadlessOptions, TextWriter, IAiConnection> Connect { get; init; } = DefaultConnect;

    /// <summary>Sleeps for pacing, rounded up to whole milliseconds (tests record instead).</summary>
    public Action<TimeSpan> Sleep { get; init; } = wait => Thread.Sleep(TimeSpan.FromMilliseconds(Math.Ceiling(wait.TotalMilliseconds)));

    /// <summary>Real time per tick while an AI call is running (null = the game's fastest speed from the data).</summary>
    public TimeSpan? BusyTickInterval { get; init; }

    /// <summary>Monotonic clock for pacing (tests pass a fake that only moves when they sleep).</summary>
    public Func<TimeSpan> Clock { get; init; } = () => Stopwatch.GetElapsedTime(0);

    /// <summary>Runs; returns the exit code (<see cref="ExitCodes"/>).</summary>
    public int Run(HeadlessOptions options, TextWriter output, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        IAiConnection connection;
        try
        {
            connection = Connect(options, output);
        }
        catch (AiUnavailableException ex)
        {
            output.WriteLine($"AI_SERVICE_UNAVAILABLE {ex.Message.ReplaceLineEndings(" ")}");
            return ExitCodes.AiUnavailable;
        }

        SimulationHost? host = null;
        var finalPrinted = false;
        JsonLogWriter? json = null;
        try
        {
            var created = options.Preset is null
                ? SimulationHost.CreatePlaceholderWorld(options.ContentDirectory, options.Seed, connection.Decisions, "de", connection.Jobs)
                : SimulationHost.CreateGeneratedWorld(options.ContentDirectory, options.Seed, connection.Decisions, "de", options.Preset, connection.Jobs);
            CheckKinds(options, created);

            // From here on the run counts: FINAL is printed even if it ends with an exception.
            host = created;
            json = options.LogJsonPath is null ? null : new JsonLogWriter(options.LogJsonPath);
            var result = Loop(options, host, connection, json, output, stop);
            json?.WriteNew(host.Decisions.Log, host.Inventions.Log);
            output.WriteLine(Final(host));
            finalPrinted = true;
            return result;
        }
        finally
        {
            json?.Dispose();
            connection.Dispose();
            if (host is not null && !finalPrinted)
            {
                output.WriteLine(Final(host));
            }
        }
    }

    /// <summary><c>FINAL hash=.. tick=..</c></summary>
    public static string Final(SimulationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return string.Create(CultureInfo.InvariantCulture, $"FINAL hash={host.World.StateHash()} tick={host.World.Tick}");
    }

    private static IAiConnection DefaultConnect(HeadlessOptions options, TextWriter output)
    {
        if (options.Ai == AiMode.Mock)
        {
            return new MockConnection(options.ContentDirectory);
        }

        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(options.ContentDirectory, "decider", "settings.json")));
        var live = LiveConnection.Start(options.ContentDirectory, settings.DeadlineMs, output);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"AI_SERVICE live on port {live.Port}"));
        return live;
    }

    private static void CheckKinds(HeadlessOptions options, SimulationHost host)
    {
        var known = host.Content.EventKinds;
        var named = new List<string>(options.Events?.Kinds ?? Enumerable.Empty<string>());
        if (options.Until is { } until)
        {
            named.Add(until.Kind);
        }

        foreach (var kind in named)
        {
            if (known.Count > 0 && !known.Contains(kind))
            {
                throw new ArgumentException($"Unknown event kind '{kind}' (content/vocabulary/events.json).");
            }
        }
    }

    private int Loop(HeadlessOptions options, SimulationHost host, IAiConnection connection, JsonLogWriter? json, TextWriter output, CancellationToken stop)
    {
        var ticksPerDay = host.World.Calendar.TicksPerDay;
        var ticksPerHour = host.World.Calendar.Settings.TicksPerHour;
        var fingerprint = options.SavePath is null ? null : ContentFingerprint.Compute(options.ContentDirectory);
        if (options.SavePath is not null)
        {
            _ = new AutoSaver(host, options.SavePath, fingerprint);
        }

        var calendar = host.World.Calendar;
        var fastest = calendar.Settings.FastForwardFactors.DefaultIfEmpty(1).Max();
        var pacer = new Pacer(
            TimeSpan.FromMilliseconds(options.TickMs),
            BusyTickInterval ?? TimeSpan.FromSeconds(calendar.RealSecondsPerTickAtNormalSpeed / Math.Max(1, fastest)),
            Clock,
            Sleep);

        // Event lines and the stop condition read every reported event in report order (no buffer limit);
        // the chronicle (read through IChronicle, like the game's book) observes the same events first, so its
        // new entries follow their event line.
        var chronicle = host.Chronicle;
        var chronicleSeen = chronicle.LastSeq;
        var untilSeen = 0;
        host.Inhabitants.Reported += e =>
        {
            if (options.Events is { } events)
            {
                if (events.Includes(e.Kind))
                {
                    output.WriteLine(EventLines.Event(e));
                }

                foreach (var record in ((IChronicle)chronicle).Since(chronicleSeen))
                {
                    output.WriteLine(EventLines.Chronicle(record));
                    chronicleSeen = record.Seq;
                }
            }

            if (options.Until is { } until && e.Kind == until.Kind)
            {
                untilSeen++;
            }
        };

        var stopwatch = Stopwatch.StartNew();
        var longestNeglect = new Dictionary<string, long>(StringComparer.Ordinal);
        var reached = false;
        var interrupted = false;
        for (var day = 1; day <= options.Days && !reached && !interrupted; day++)
        {
            for (var t = 0; t < ticksPerDay; t++)
            {
                if (stop.IsCancellationRequested)
                {
                    interrupted = true;
                    break;
                }

                pacer.BeforeTick(connection.Busy);
                host.World.Step();
                if (options.Trace is not null && day <= options.TraceDays)
                {
                    foreach (var e in host.Inhabitants.Events.Where(e => e.InhabitantId == options.Trace && e.Tick == host.World.Tick - 1))
                    {
                        var tr = host.Inhabitants.Get(options.Trace);
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"   t{e.Tick,6} {e.Kind,-17} {e.Action,-8} {e.TargetId,-11} hunger {tr.Needs["hunger"]:F2} thirst {tr.Needs["thirst"]:F2} sleep {tr.Needs["sleep"]:F2}"));
                    }
                }

                foreach (var i in host.Inhabitants.All)
                {
                    longestNeglect[i.Id] = Math.Max(longestNeglect.GetValueOrDefault(i.Id), i.NeglectTicks);
                }

                json?.WriteNew(host.Decisions.Log, host.Inventions.Log);
                if (options.Until is { } until && untilSeen >= until.Count)
                {
                    reached = true;
                    break;
                }
            }

            if (!options.Quiet && !reached && !interrupted)
            {
                WriteDay(host, day, output);
            }
        }

        stopwatch.Stop();
        var ticks = host.World.Tick;
        var limitHours = host.Content.InhabitantModel.WillToLive.NeglectHoursBeforeDecline;
        var worstHours = longestNeglect.Count == 0 ? 0 : longestNeglect.Values.Max() / ticksPerHour;
        var fading = host.Inhabitants.All.Count(host.Inhabitants.IsFading);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"done  seed {options.Seed}  days {options.Days}  inhabitants {host.Inhabitants.All.Count()}  ticks {ticks}  hash {host.World.StateHash()}"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"neglect  longest {worstHours} h (limit {limitHours} h)  fading {fading}"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"time  {stopwatch.Elapsed.TotalSeconds:F2} s  ({stopwatch.Elapsed.TotalMicroseconds / Math.Max(1, ticks):F1} us/tick)"));
        if (options.Until is { } wanted)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"UNTIL kind={wanted.Kind} count={untilSeen}/{wanted.Count} {(reached ? "reached" : "not_reached")} tick={ticks}"));
        }

        var ok = worstHours <= limitHours && fading == 0;
        if (options.SavePath is not null)
        {
            // Save also a partial last day (stopped by --until or Ctrl+C), so a replay covers the whole run.
            if (ticks % ticksPerDay != 0)
            {
                SaveGameStore.Save(options.SavePath, StateCodec.Capture(host) with { ContentFingerprint = fingerprint });
            }

            var loaded = StateCodec.Restore(
                new SimulationHostFactory(host.Content, host.DeciderSettings, MockDecisionPort.FromContent(options.ContentDirectory), "de"),
                SaveGameStore.Load(options.SavePath));
            var same = loaded.World.StateHash() == host.World.StateHash();
            output.WriteLine($"save  {options.SavePath}  hash after load {(same ? "identical" : "DIFFERENT")}");
            ok &= same;
        }

        output.WriteLine(ok ? "ACCEPTANCE_OK" : "ACCEPTANCE_FAIL");
        return interrupted ? ExitCodes.Interrupted
            : !ok ? ExitCodes.AcceptanceFailed
            : options.Until is not null && !reached ? ExitCodes.UntilNotReached
            : ExitCodes.Ok;
    }

    private static void WriteDay(SimulationHost host, int day, TextWriter output)
    {
        var ticksPerHour = host.World.Calendar.Settings.TicksPerHour;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"day {day,4}  hash {host.World.StateHash()[..12]}  objects {host.Objects.Count,4}"));
        foreach (var i in host.Inhabitants.All)
        {
            var lowest = i.Needs.OrderBy(n => n.Value).ThenBy(n => n.Key, StringComparer.Ordinal).Take(2).Select(n => string.Create(CultureInfo.InvariantCulture, $"{n.Key}={n.Value:F2}"));
            var food = host.Objects.All.Where(o => o.TypeId == "apple").Select(o => Math.Sqrt(((o.Position.X - i.Position.X) * (o.Position.X - i.Position.X)) + ((o.Position.Z - i.Position.Z) * (o.Position.Z - i.Position.Z)))).DefaultIfEmpty(-1).Min();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"      pos ({i.Position.X:F0},{i.Position.Z:F0}) apple {food:F0} m  edible-belief {(i.Beliefs.TryGet("apple", "edible", out var b) ? b.Value + "/" + b.Confidence.ToString("F2", CultureInfo.InvariantCulture) : "none")}"));
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"      {i.Id} {i.Sex,-6} will {i.WillToLive:F2}  neglect {i.NeglectTicks / ticksPerHour,4} h  low: {string.Join(", ", lowest)}  doing {i.Activity?.Action ?? "-"}"));
        }
    }
}
