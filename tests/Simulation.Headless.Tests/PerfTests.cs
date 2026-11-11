using System.Globalization;
using System.Text.Json;

namespace GlassWorld.Simulation.Headless.Tests;

/// <summary>
/// The perf command (B2 acceptance 3: the step with 100 inhabitants). These tests check that the
/// measurement runs, reports and adds up; they judge no time: a wall-clock limit in the test suite would
/// fail on a busy machine (see the README of the runner for the budget check).
/// </summary>
public sealed class PerfTests
{
    private static readonly string Content = ContentFolder.Find();

    private static (int Code, string Text) Perf(params string[] args)
    {
        var output = new StringWriter();
        var code = PerfCommand.Run([.. args, "--content", Content], output);
        return (code, output.ToString());
    }

    private static string[] LinesOf(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static double Value(string line, string key) =>
        double.Parse(line.Split(' ').Single(p => p.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..], CultureInfo.InvariantCulture);

    [Fact]
    public void DefaultsAreTheAcceptanceRun()
    {
        var options = PerfOptions.Parse([], () => "content-dir");
        Assert.Equal(100, options.Inhabitants);
        Assert.Equal("start_island", options.Preset);
        Assert.Equal(1UL, options.Seed);
        Assert.Equal(1, options.Days);
        Assert.Null(options.Ticks);
        Assert.Equal(PerfDecider.Mock, options.Decider);
        Assert.Equal(PerfOptions.DefaultWarmupTicks, options.WarmupTicks);
        Assert.False(options.Json);
        Assert.Null(options.BudgetMs);
    }

    [Fact]
    public void EveryOptionIsRead()
    {
        var options = PerfOptions.Parse(
            ["--preset", "p", "--seed", "42", "--inhabitants", "12", "--days", "3", "--ticks", "500", "--ai", "silent", "--warmup-ticks", "7", "--budget-ms", "4.5", "--json", "--content", "c"]);
        Assert.Equal(new PerfOptions(42, 3, 500, 12, "p", "c", PerfDecider.Silent, 7, true, 4.5), options);
        Assert.Null(PerfOptions.Parse(["--inhabitants", "data"], () => "c").Inhabitants);
    }

    [Theory]
    [InlineData("--inhabitants", "0")]
    [InlineData("--days", "0")]
    [InlineData("--ticks", "0")]
    [InlineData("--budget-ms", "0")]
    [InlineData("--ai", "live")]
    [InlineData("--nonsense", "1")]
    public void BadArgumentsAreRefused(string option, string value) =>
        Assert.Throws<ArgumentException>(() => PerfOptions.Parse([option, value], () => "c"));

    [Fact]
    public void TheReportNamesTheRunTheStepAndWhereTheTimeGoes()
    {
        var (code, text) = Perf("--inhabitants", "12", "--ticks", "90", "--warmup-ticks", "30", "--ai", "silent");
        Assert.Equal(ExitCodes.Ok, code);
        var lines = LinesOf(text);
        Assert.StartsWith("PERF preset=start_island seed=1 inhabitants=12 decider=silent ticks=90 warmup_ticks=30 measured_ticks=60 build=", lines[0], StringComparison.Ordinal);
        var step = Assert.Single(lines, l => l.StartsWith("STEP ", StringComparison.Ordinal));
        Assert.Matches("^STEP mean_ms=[0-9.]+ median_ms=[0-9.]+ p95_ms=[0-9.]+ p99_ms=[0-9.]+ max_ms=[0-9.]+ max_tick=[0-9]+$", step);
        Assert.InRange(Value(step, "max_tick"), 30, 89);
        Assert.True(Value(step, "median_ms") <= Value(step, "p95_ms") && Value(step, "p95_ms") <= Value(step, "p99_ms") && Value(step, "p99_ms") <= Value(step, "max_ms"));

        // Every system of the world has its line, in the order the systems run; the inhabitants name their parts.
        var systems = lines.Where(l => l.StartsWith("SYSTEM ", StringComparison.Ordinal)).Select(l => l.Split(' ')[1]["name=".Length..]).ToList();
        Assert.Equal(["worldmap", "weather", "objects", "decisions", "inhabitants"], systems.Take(5));
        Assert.Equal(World.BoundariesPart, systems[^1]);
        Assert.Contains(lines, l => l.StartsWith("PART name=inhabitants/act ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("PART name=inhabitants/(rest) ", StringComparison.Ordinal));

        // The parts never overlap: together the systems take no more time than the step they are part of.
        var inSystems = lines.Where(l => l.StartsWith("SYSTEM ", StringComparison.Ordinal)).Sum(l => Value(l, "mean_ms"));
        Assert.True(inSystems <= Value(step, "mean_ms") + 0.001, $"systems {inSystems} ms, step {Value(step, "mean_ms")} ms");
        Assert.Contains(lines, l => l.StartsWith("REQUESTS sent=", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("ALLOC kb_per_step=", StringComparison.Ordinal));
        Assert.Matches("^FINAL hash=[0-9a-f]{64} tick=90$", lines[^1]);
        Assert.DoesNotContain(lines, l => l.StartsWith("PERF_", StringComparison.Ordinal));
    }

    [Fact]
    public void JsonCarriesTheSameReport()
    {
        var (code, text) = Perf("--inhabitants", "12", "--ticks", "90", "--warmup-ticks", "30", "--json");
        Assert.Equal(ExitCodes.Ok, code);
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        Assert.Equal("start_island", root.GetProperty("preset").GetString());
        Assert.Equal(12, root.GetProperty("inhabitants").GetInt32());
        Assert.Equal("mock", root.GetProperty("decider").GetString());
        Assert.Equal(60, root.GetProperty("measured_ticks").GetInt32());
        foreach (var key in new[] { "mean", "median", "p95", "p99", "max" })
        {
            Assert.True(root.GetProperty("step_ms").GetProperty(key).GetDouble() > 0);
            Assert.True(root.GetProperty("step_with_port_ms").GetProperty(key).GetDouble() > 0);
        }

        var systems = root.GetProperty("systems").EnumerateArray().ToList();
        var inhabitants = Assert.Single(systems, s => s.GetProperty("name").GetString() == "inhabitants");
        Assert.NotEmpty(inhabitants.GetProperty("parts").EnumerateArray());
        Assert.True(root.GetProperty("requests").GetProperty("sent").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("budget_ms").ValueKind);
        Assert.Matches("^[0-9a-f]{64}$", root.GetProperty("hash").GetString()!);
    }

    [Fact]
    public void MeasuringTheDatasWorldIsTheRunCommandsWorld()
    {
        // The probe and the timed port change nothing: after the same ticks the hash is the run command's.
        var (_, text) = Perf("--inhabitants", "data", "--days", "1", "--warmup-ticks", "0");
        var output = new StringWriter();
        new RunCommand().Run(HeadlessOptions.Parse(["--days", "1", "--quiet", "--preset", "start_island", "--content", Content]), output, CancellationToken.None);
        Assert.Equal(LinesOf(output.ToString())[^1].Trim(), LinesOf(text)[^1]);
    }

    [Fact]
    public void TheSameMeasurementEndsWithTheSameHash()
    {
        string Hash() => LinesOf(Perf("--inhabitants", "20", "--ticks", "120", "--seed", "7").Text)[^1];
        Assert.Equal(Hash(), Hash());
    }

    [Fact]
    public void ABudgetDecidesTheExitCode()
    {
        // Two budgets no machine can miss or meet: a million milliseconds, and a millionth of one.
        var (ok, kept) = Perf("--inhabitants", "8", "--ticks", "40", "--warmup-ticks", "10", "--budget-ms", "1000000");
        Assert.Equal(ExitCodes.Ok, ok);
        Assert.Contains(LinesOf(kept), l => l.StartsWith("PERF_OK budget_ms=1000000.000 ", StringComparison.Ordinal));

        var (over, missed) = Perf("--inhabitants", "8", "--ticks", "40", "--warmup-ticks", "10", "--budget-ms", "0.000001");
        Assert.Equal(ExitCodes.OverBudget, over);
        Assert.Contains(LinesOf(missed), l => l.StartsWith("PERF_OVER_BUDGET budget_ms=0.000 ", StringComparison.Ordinal));
    }

    [Fact]
    public void PartsCountTheirOwnTimeOnly()
    {
        // A clock the test moves: 10 in the system before its part, 5 in the part, 3 in the port inside the
        // part, 2 after the port, 7 after the part.
        long now = 0;
        var timer = new StepTimer(() => now);
        void Step()
        {
            timer.Enter("system");
            now += 10;
            timer.Enter("system/part");
            now += 5;
            timer.Enter(TimedDecisionPort.Part);
            now += 3;
            timer.Leave(TimedDecisionPort.Part);
            now += 2;
            timer.Leave("system/part");
            now += 7;
            timer.Leave("system");
        }

        Step();
        Assert.Equal(3, timer.InStep(TimedDecisionPort.Part));
        Assert.Equal(24, timer.InStep("system"));
        timer.Drop();
        Assert.Equal(0, timer.InStep("system"));
        Assert.Equal(0, timer.Steps);

        Step();
        timer.Keep();
        Step();
        timer.Keep();
        Assert.Equal(2, timer.Steps);
        Assert.Equal(["system", "system/part", TimedDecisionPort.Part], timer.Parts);
        var unit = timer.MeanMs(TimedDecisionPort.Part) / 3;
        Assert.Equal(17, timer.MeanMs("system") / unit, 6);
        Assert.Equal(7, timer.MeanMs("system/part") / unit, 6);
        Assert.Equal(24, timer.GroupMeanMs("system") / unit, 6);
        Assert.Equal(24, timer.GroupMaxMs("system") / unit, 6);
        Assert.Throws<InvalidOperationException>(() =>
        {
            timer.Enter("a");
            timer.Leave("b");
        });
    }

    [Fact]
    public void StatisticsUseTheNearestRank()
    {
        var stats = StepStats.Of([.. Enumerable.Range(1, 100).Select(n => (double)n).Reverse()], 500);
        Assert.Equal(50.5, stats.Mean);
        Assert.Equal(50, stats.Median);
        Assert.Equal(95, stats.P95);
        Assert.Equal(99, stats.P99);
        Assert.Equal(100, stats.Max);
        Assert.Equal(500, stats.MaxTick);
        Assert.Equal(new StepStats(0, 0, 0, 0, 0, 3), StepStats.Of([], 3));
    }
}
