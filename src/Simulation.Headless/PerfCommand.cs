using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Simulation.Headless;

/// <summary>Which decider a measurement runs with.</summary>
internal enum PerfDecider
{
    /// <summary>The deterministic mock: every question is answered at the next tick (what CI runs).</summary>
    Mock,

    /// <summary>Nobody answers: every question runs into its deadline.</summary>
    Silent,
}

/// <summary>Options of the perf command.</summary>
/// <param name="Seed">World seed.</param>
/// <param name="Days">Game days the world runs (warm-up included).</param>
/// <param name="Ticks">Ticks the world runs instead of whole days (warm-up included), or null.</param>
/// <param name="Inhabitants">How many inhabitants the world starts with; null = those the data lists.</param>
/// <param name="Preset">Island preset.</param>
/// <param name="ContentDirectory">Content folder.</param>
/// <param name="Decider">Decider of the run.</param>
/// <param name="WarmupTicks">Ticks at the start that run but are not measured.</param>
/// <param name="Json">One JSON object instead of the lines.</param>
/// <param name="BudgetMs">Budget for mean and p95 of the step in milliseconds, or null (report only).</param>
internal sealed record PerfOptions(ulong Seed, long Days, long? Ticks, int? Inhabitants, string Preset, string ContentDirectory, PerfDecider Decider, long WarmupTicks, bool Json, double? BudgetMs)
{
    /// <summary>Usage line for errors.</summary>
    public const string Usage = "Use: perf [--preset P] [--seed N] [--inhabitants N|data] [--days N | --ticks N] [--ai mock|silent] [--answer-every N] [--warmup-ticks N] [--runtime-warmup-seconds X] [--budget-ms X] [--json] [--csv FILE] [--content DIR].";

    /// <summary>
    /// Seconds a throwaway world of the same kind runs before the measured world is built, so the runtime has
    /// compiled the hot code to its final form (0 = none).
    /// </summary>
    public double RuntimeWarmupSeconds { get; init; } = DefaultRuntimeWarmupSeconds;

    /// <summary>
    /// With the mock: at most one answer every this many ticks, oldest question first (one model that needs
    /// that long per answer); 0 = every question is answered at the next tick.
    /// </summary>
    public int AnswerEvery { get; init; }

    /// <summary>Default of <see cref="RuntimeWarmupSeconds"/>.</summary>
    public const double DefaultRuntimeWarmupSeconds = 6;

    /// <summary>File for one line per measured step (tick, step, every part), or null.</summary>
    public string? CsvPath { get; init; }

    /// <summary>Preset measured by default (the start island of the game).</summary>
    public const string DefaultPreset = "start_island";

    /// <summary>Inhabitants measured by default: the number the step budget is set for (B2 acceptance 3, D334).</summary>
    public const int DefaultInhabitants = 100;

    /// <summary>Ticks left out at the start by default (two game hours): the runtime compiles the hot code in them.</summary>
    public const long DefaultWarmupTicks = 120;

    /// <summary>Parses the arguments; throws <see cref="ArgumentException"/> on unknown or bad arguments.</summary>
    public static PerfOptions Parse(string[] args, Func<string>? findContent = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ulong seed = 1;
        long days = 1;
        long? ticks = null;
        int? inhabitants = DefaultInhabitants;
        var preset = DefaultPreset;
        string? content = null;
        var decider = PerfDecider.Mock;
        var warmup = DefaultWarmupTicks;
        var json = false;
        double? budget = null;
        string? csv = null;
        var runtimeWarmup = DefaultRuntimeWarmupSeconds;
        var answerEvery = 0;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seed":
                    seed = ulong.Parse(Value(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--days":
                    days = long.Parse(Value(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture);
                    break;
                case "--ticks":
                    ticks = long.Parse(Value(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture);
                    break;
                case "--inhabitants":
                    // "data" = the start inhabitants the data lists (the world the run command builds).
                    inhabitants = Value(args, ref i) is var n && n == "data" ? null : int.Parse(n, NumberStyles.None, CultureInfo.InvariantCulture);
                    break;
                case "--preset":
                    preset = Value(args, ref i);
                    break;
                case "--content":
                    content = Value(args, ref i);
                    break;
                case "--ai":
                    decider = Value(args, ref i) switch
                    {
                        "mock" => PerfDecider.Mock,
                        "silent" => PerfDecider.Silent,
                        var other => throw new ArgumentException($"--ai must be 'mock' or 'silent' for perf, not '{other}'."),
                    };
                    break;
                case "--warmup-ticks":
                    warmup = long.Parse(Value(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture);
                    break;
                case "--budget-ms":
                    budget = double.Parse(Value(args, ref i), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                    break;
                case "--json":
                    json = true;
                    break;
                case "--csv":
                    csv = Value(args, ref i);
                    break;
                case "--answer-every":
                    answerEvery = int.Parse(Value(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture);
                    break;
                case "--runtime-warmup-seconds":
                    runtimeWarmup = double.Parse(Value(args, ref i), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. {Usage}");
            }
        }

        if (days < 1 || ticks is < 1)
        {
            throw new ArgumentException("--days and --ticks must be at least 1.");
        }

        if (inhabitants is < 1)
        {
            throw new ArgumentException("--inhabitants must be at least 1 (or 'data').");
        }

        if (budget is <= 0)
        {
            throw new ArgumentException("--budget-ms must be greater than 0.");
        }

        if (answerEvery > 0 && decider != PerfDecider.Mock)
        {
            throw new ArgumentException("--answer-every needs --ai mock (the silent decider never answers).");
        }

        return new PerfOptions(seed, days, ticks, inhabitants, preset, content ?? (findContent ?? ContentFolder.Find)(), decider, warmup, json, budget) { CsvPath = csv, RuntimeWarmupSeconds = runtimeWarmup, AnswerEvery = answerEvery };
    }

    private static string Value(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"{args[i]} needs a value. {Usage}");
        }

        return args[++i];
    }
}

/// <summary>Statistics over the measured steps, in milliseconds.</summary>
/// <param name="Mean">Arithmetic mean.</param>
/// <param name="Median">Median (nearest rank).</param>
/// <param name="P95">95th percentile (nearest rank).</param>
/// <param name="P99">99th percentile (nearest rank).</param>
/// <param name="Max">Slowest step.</param>
/// <param name="MaxTick">World tick the slowest step advanced.</param>
internal sealed record StepStats(double Mean, double Median, double P95, double P99, double Max, long MaxTick)
{
    /// <summary>Statistics of the steps <paramref name="ms"/>, the first of which advanced <paramref name="firstTick"/>.</summary>
    public static StepStats Of(IReadOnlyList<double> ms, long firstTick)
    {
        ArgumentNullException.ThrowIfNull(ms);
        if (ms.Count == 0)
        {
            return new StepStats(0, 0, 0, 0, 0, firstTick);
        }

        var slowest = 0;
        for (var i = 1; i < ms.Count; i++)
        {
            slowest = ms[i] > ms[slowest] ? i : slowest;
        }

        var sorted = ms.ToArray();
        Array.Sort(sorted);
        double At(double q) => sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new StepStats(sorted.Average(), At(0.5), At(0.95), At(0.99), sorted[^1], firstTick + slowest);
    }
}

/// <summary>A part a system names inside its own work (or what is left of the system beside them).</summary>
/// <param name="Name"><c>system/part</c>; <c>system/(rest)</c> for the system's work outside its named parts.</param>
/// <param name="MeanMs">Mean milliseconds per step in this part alone.</param>
/// <param name="Share">Its share of the step (0..1).</param>
/// <param name="MaxMs">Most milliseconds one step spent in it.</param>
/// <param name="Calls">How often it began per step, on average (per inhabitant, per request ...).</param>
internal sealed record PartTime(string Name, double MeanMs, double Share, double MaxMs, double Calls);

/// <summary>One world system in the breakdown (or <see cref="World.BoundariesPart"/>), with the parts it names.</summary>
/// <param name="Name">System name.</param>
/// <param name="MeanMs">Mean milliseconds per step in the system, its parts included, the decider port left out.</param>
/// <param name="Share">Its share of the step (0..1).</param>
/// <param name="MaxMs">Most milliseconds one step spent in it.</param>
/// <param name="Parts">Its named parts; empty when it names none.</param>
internal sealed record SystemTime(string Name, double MeanMs, double Share, double MaxMs, IReadOnlyList<PartTime> Parts);

/// <summary>What a measurement found.</summary>
internal sealed record PerfResult(
    PerfOptions Options,
    int Inhabitants,
    long Ticks,
    long WarmupTicks,
    long RuntimeWarmupTicks,
    long MeasuredTicks,
    bool Optimized,
    StepStats Step,
    StepStats StepWithPort,
    double CpuMeanMs,
    double PortMeanMs,
    double PortMaxMs,
    IReadOnlyList<SystemTime> Systems,
    long Requests,
    long Answers,
    int OpenQuestions,
    double AllocatedBytesPerStep,
    int Gen0,
    int Gen1,
    int Gen2,
    double OnTheWay,
    double Acting,
    double Idle,
    string Hash)
{
    /// <summary>True when a budget is set and mean or p95 of the step is above it.</summary>
    public bool OverBudget => Options.BudgetMs is { } budget && (Step.Mean > budget || Step.P95 > budget);
}

/// <summary>
/// <c>perf</c>: how long the simulation step takes (B2 acceptance 3: "the simulation step with 100
/// inhabitants stays under 4 ms", D334). Builds the island of a preset and seed with N inhabitants (the
/// data's start inhabitants, repeated as further groups around the start places), runs it and times every
/// <see cref="World.Step"/>: mean, median, p95, p99 and maximum per step, and where the time goes (per
/// world system and the parts the systems name). See the README of the runner for what "the step" means.
/// </summary>
internal static class PerfCommand
{
    /// <summary>Name of what a system does outside the parts it names.</summary>
    public const string Rest = "(rest)";

    /// <summary>Runs the command and prints the report; returns 0, or <see cref="ExitCodes.OverBudget"/>.</summary>
    public static int Run(string[] args, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var result = Measure(PerfOptions.Parse(args));
        output.Write(result.Options.Json ? Json(result) : Lines(result));
        return result.OverBudget ? ExitCodes.OverBudget : ExitCodes.Ok;
    }

    /// <summary>The world of a measurement with its timer and its timed decider port.</summary>
    private static (SimulationHost Host, StepTimer Timer, TimedDecisionPort Port) Build(PerfOptions options)
    {
        var timer = new StepTimer();
        IDecisionPort decider = options.Decider == PerfDecider.Silent ? new SilentDecisionPort() : MockDecisionPort.FromContent(options.ContentDirectory);
        if (options.AnswerEvery > 0)
        {
            decider = new PacedDecisionPort(decider, options.AnswerEvery);
        }

        var port = new TimedDecisionPort(decider, timer);
        var host = SimulationHost.CreateGeneratedWorld(options.ContentDirectory, options.Seed, port, "de", options.Preset, inhabitants: options.Inhabitants);
        host.World.Probe = timer;
        return (host, timer, port);
    }

    /// <summary>
    /// Runs a throwaway world of the same kind for the given seconds and returns its ticks. The runtime
    /// compiles a method quickly at first and for good only after it has run for a while; a measurement
    /// right after the start of the process would time that first code (several times slower), which no
    /// game sees after its first seconds. The measured world is built afterwards, so its run - and its hash -
    /// does not depend on how long this took.
    /// </summary>
    private static long WarmRuntimeUp(PerfOptions options)
    {
        if (options.RuntimeWarmupSeconds <= 0)
        {
            return 0;
        }

        var world = Build(options).Host.World;
        var ticks = 0L;
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalSeconds < options.RuntimeWarmupSeconds)
        {
            world.Step();
            ticks++;
        }

        return ticks;
    }

    /// <summary>Builds the world and measures its steps.</summary>
    public static PerfResult Measure(PerfOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var runtimeWarmupTicks = WarmRuntimeUp(options);

        // The throwaway world is garbage now: collected here, not during the measured steps.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var (host, timer, port) = Build(options);
        var world = host.World;
        var everyone = host.Inhabitants.All.ToList();
        var ticks = options.Ticks ?? (options.Days * world.Calendar.TicksPerDay);
        var warmup = Math.Min(options.WarmupTicks, ticks);
        for (var t = 0L; t < warmup; t++)
        {
            world.Step();
            timer.Drop();
        }

        var measured = (int)(ticks - warmup);
        var step = new double[measured];
        var withPort = new double[measured];
        var firstTick = world.Tick;
        long onTheWay = 0;
        long acting = 0;
        var perStep = options.CsvPath is null ? null : new List<double[]>(measured);
        var (sent, received) = (port.Sent, port.Received);
        var gen = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var t = 0; t < measured; t++)
        {
            var started = Stopwatch.GetTimestamp();
            world.Step();
            var whole = Stopwatch.GetTimestamp() - started;
            withPort[t] = Stopwatch.GetElapsedTime(0, whole).TotalMilliseconds;
            step[t] = Stopwatch.GetElapsedTime(0, whole - timer.InStep(TimedDecisionPort.Part)).TotalMilliseconds;
            perStep?.Add(timer.StepMs());
            timer.Keep();
            foreach (var inhabitant in everyone)
            {
                onTheWay += inhabitant.Activity is { RemainingTicks: null } ? 1 : 0;
                acting += inhabitant.Activity is { RemainingTicks: not null } ? 1 : 0;
            }
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        process.Refresh();
        cpu = process.TotalProcessorTime - cpu;
        if (perStep is not null)
        {
            WriteCsv(options.CsvPath!, firstTick, step, withPort, timer.Parts, perStep);
        }

        var stats = StepStats.Of(step, firstTick);
        double Share(double mean) => stats.Mean <= 0 ? 0 : mean / stats.Mean;
        var systems = new List<SystemTime>();
        foreach (var name in world.SystemNames.Append(World.BoundariesPart))
        {
            var named = timer.Parts.Where(p => p != name && StepTimer.GroupOf(p) == name).ToList();
            var parts = named.Select(p => new PartTime(p, timer.MeanMs(p), Share(timer.MeanMs(p)), timer.MaxMs(p), timer.MeanCalls(p))).ToList();
            if (parts.Count > 0)
            {
                parts.Add(new PartTime(name + "/" + Rest, timer.MeanMs(name), Share(timer.MeanMs(name)), timer.MaxMs(name), timer.MeanCalls(name)));
            }

            systems.Add(new SystemTime(name, timer.GroupMeanMs(name), Share(timer.GroupMeanMs(name)), timer.GroupMaxMs(name), parts));
        }

        var steps = Math.Max(1, measured);
        return new PerfResult(
            options,
            everyone.Count,
            world.Tick,
            warmup,
            runtimeWarmupTicks,
            measured,
            typeof(World).Assembly.GetCustomAttribute<DebuggableAttribute>() is not { IsJITOptimizerDisabled: true },
            stats,
            StepStats.Of(withPort, firstTick),
            cpu.TotalMilliseconds / steps,
            timer.GroupMeanMs(TimedDecisionPort.Part),
            timer.GroupMaxMs(TimedDecisionPort.Part),
            systems,
            port.Sent - sent,
            port.Received - received,
            host.Decisions.Open.Count(),
            (double)allocated / steps,
            GC.CollectionCount(0) - gen.Item1,
            GC.CollectionCount(1) - gen.Item2,
            GC.CollectionCount(2) - gen.Item3,
            (double)onTheWay / steps,
            (double)acting / steps,
            everyone.Count - ((double)(onTheWay + acting) / steps),
            world.StateHash());
    }

    /// <summary>The report as lines (each splits on single spaces into <c>key=value</c> after its first word).</summary>
    public static string Lines(PerfResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var o = result.Options;
        var text = new StringBuilder();
        void Line(FormattableString line) => text.Append(FormattableString.Invariant(line)).Append('\n');
        Line($"PERF preset={o.Preset} seed={o.Seed} inhabitants={result.Inhabitants} decider={Name(o.Decider)} answer_every={o.AnswerEvery} ticks={result.Ticks} warmup_ticks={result.WarmupTicks} measured_ticks={result.MeasuredTicks} runtime_warmup_s={o.RuntimeWarmupSeconds:0.###} runtime_warmup_ticks={result.RuntimeWarmupTicks} build={(result.Optimized ? "release" : "debug")}");
        if (!result.Optimized)
        {
            Line($"WARNING the core is a debug build: these times say nothing about the budget (run with -c Release)");
        }

        Line($"STEP mean_ms={result.Step.Mean:F3} median_ms={result.Step.Median:F3} p95_ms={result.Step.P95:F3} p99_ms={result.Step.P99:F3} max_ms={result.Step.Max:F3} max_tick={result.Step.MaxTick}");
        Line($"STEP_WITH_PORT mean_ms={result.StepWithPort.Mean:F3} median_ms={result.StepWithPort.Median:F3} p95_ms={result.StepWithPort.P95:F3} p99_ms={result.StepWithPort.P99:F3} max_ms={result.StepWithPort.Max:F3} max_tick={result.StepWithPort.MaxTick}");
        Line($"CPU mean_ms={result.CpuMeanMs:F3}");
        Line($"PORT name={TimedDecisionPort.Part} mean_ms={result.PortMeanMs:F4} max_ms={result.PortMaxMs:F3}");
        foreach (var system in result.Systems)
        {
            Line($"SYSTEM name={system.Name} mean_ms={system.MeanMs:F4} share={100 * system.Share:F1} max_ms={system.MaxMs:F3}");
            foreach (var part in system.Parts)
            {
                Line($"PART name={part.Name} mean_ms={part.MeanMs:F4} share={100 * part.Share:F1} max_ms={part.MaxMs:F3} calls={part.Calls:F1}");
            }
        }

        Line($"REQUESTS sent={result.Requests} per_step={(double)result.Requests / Math.Max(1, result.MeasuredTicks):F3} answers={result.Answers} open_at_end={result.OpenQuestions}");
        Line($"ALLOC kb_per_step={result.AllocatedBytesPerStep / 1024:F1} gc0={result.Gen0} gc1={result.Gen1} gc2={result.Gen2}");
        Line($"DOING on_the_way={result.OnTheWay:F1} acting={result.Acting:F1} idle={result.Idle:F1}");
        if (o.BudgetMs is { } budget)
        {
            Line($"{(result.OverBudget ? "PERF_OVER_BUDGET" : "PERF_OK")} budget_ms={budget:F3} mean_ms={result.Step.Mean:F3} p95_ms={result.Step.P95:F3}");
        }

        Line($"FINAL hash={result.Hash} tick={result.Ticks}");
        return text.ToString();
    }

    /// <summary>The report as one JSON object (the same values as the lines, numbers unrounded).</summary>
    public static string Json(PerfResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var o = result.Options;
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("preset", o.Preset);
            json.WriteNumber("seed", o.Seed);
            json.WriteNumber("inhabitants", result.Inhabitants);
            json.WriteString("decider", Name(o.Decider));
            json.WriteNumber("answer_every", o.AnswerEvery);
            json.WriteNumber("ticks", result.Ticks);
            json.WriteNumber("warmup_ticks", result.WarmupTicks);
            json.WriteNumber("runtime_warmup_seconds", o.RuntimeWarmupSeconds);
            json.WriteNumber("runtime_warmup_ticks", result.RuntimeWarmupTicks);
            json.WriteNumber("measured_ticks", result.MeasuredTicks);
            json.WriteString("build", result.Optimized ? "release" : "debug");
            Write(json, "step_ms", result.Step);
            Write(json, "step_with_port_ms", result.StepWithPort);
            json.WriteNumber("cpu_mean_ms", result.CpuMeanMs);
            json.WriteStartObject("decider_port_ms");
            json.WriteNumber("mean", result.PortMeanMs);
            json.WriteNumber("max", result.PortMaxMs);
            json.WriteEndObject();
            json.WriteStartArray("systems");
            foreach (var system in result.Systems)
            {
                json.WriteStartObject();
                json.WriteString("name", system.Name);
                json.WriteNumber("mean_ms", system.MeanMs);
                json.WriteNumber("share", system.Share);
                json.WriteNumber("max_ms", system.MaxMs);
                json.WriteStartArray("parts");
                foreach (var part in system.Parts)
                {
                    json.WriteStartObject();
                    json.WriteString("name", part.Name);
                    json.WriteNumber("mean_ms", part.MeanMs);
                    json.WriteNumber("share", part.Share);
                    json.WriteNumber("max_ms", part.MaxMs);
                    json.WriteNumber("calls_per_step", part.Calls);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartObject("requests");
            json.WriteNumber("sent", result.Requests);
            json.WriteNumber("answers", result.Answers);
            json.WriteNumber("open_at_end", result.OpenQuestions);
            json.WriteEndObject();
            json.WriteNumber("allocated_bytes_per_step", result.AllocatedBytesPerStep);
            json.WriteStartObject("gc");
            json.WriteNumber("gen0", result.Gen0);
            json.WriteNumber("gen1", result.Gen1);
            json.WriteNumber("gen2", result.Gen2);
            json.WriteEndObject();
            json.WriteStartObject("doing");
            json.WriteNumber("on_the_way", result.OnTheWay);
            json.WriteNumber("acting", result.Acting);
            json.WriteNumber("idle", result.Idle);
            json.WriteEndObject();
            if (o.BudgetMs is { } budget)
            {
                json.WriteNumber("budget_ms", budget);
                json.WriteBoolean("over_budget", result.OverBudget);
            }
            else
            {
                json.WriteNull("budget_ms");
                json.WriteNull("over_budget");
            }

            json.WriteString("hash", result.Hash);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>
    /// One line per measured step: the world tick it advanced, the step without and with the decider port,
    /// then every part alone (the columns add up to the step with the port, but for the loop around them).
    /// </summary>
    private static void WriteCsv(string path, long firstTick, double[] step, double[] withPort, IReadOnlyList<string> parts, List<double[]> perStep)
    {
        using var csv = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        csv.Write("tick,step_ms,step_with_port_ms");
        foreach (var part in parts)
        {
            csv.Write(',');
            csv.Write(part);
        }

        csv.Write('\n');
        for (var t = 0; t < perStep.Count; t++)
        {
            csv.Write(string.Create(CultureInfo.InvariantCulture, $"{firstTick + t},{step[t]:F4},{withPort[t]:F4}"));
            for (var p = 0; p < parts.Count; p++)
            {
                // A part that first ran in a later step has no value in the earlier ones.
                csv.Write(string.Create(CultureInfo.InvariantCulture, $",{(p < perStep[t].Length ? perStep[t][p] : 0):F4}"));
            }

            csv.Write('\n');
        }
    }

    private static void Write(Utf8JsonWriter json, string name, StepStats stats)
    {
        json.WriteStartObject(name);
        json.WriteNumber("mean", stats.Mean);
        json.WriteNumber("median", stats.Median);
        json.WriteNumber("p95", stats.P95);
        json.WriteNumber("p99", stats.P99);
        json.WriteNumber("max", stats.Max);
        json.WriteNumber("max_tick", stats.MaxTick);
        json.WriteEndObject();
    }

    private static string Name(PerfDecider decider) => decider == PerfDecider.Silent ? "silent" : "mock";
}
