using GlassWorld.Simulation.Headless;

// Usage: dotnet run --project src/Simulation.Headless -- [--seed N] [--days N] [--content DIR] [--save FILE] [--preset P] [--climate C] [--quiet]
//        [--trace ID] [--trace-days N] [--ai live|mock] [--tick-ms N] [--until KIND[:COUNT]] [--events KIND,...|all] [--log-json FILE]
//        dotnet run --project src/Simulation.Headless -- replay --save FILE [--content DIR] [--force]
//        dotnet run --project src/Simulation.Headless -- worldgen ...
//        dotnet run --project src/Simulation.Headless -c Release -- perf [--inhabitants N] [--days N] [--ai mock|silent] [--json] ...
// Runs the placeholder start world, or with --preset the island generated from seed and preset (B2-2),
// with the mock decider (or the live AI service with --ai live) and prints one
// line per game day: lowest needs, will to live and neglect per inhabitant, and the state hash.
// --save writes an autosave after every game day and checks at the end that loading gives the same hash.
// The last line is always FINAL hash=<state hash> tick=<tick>. Exit codes: 0 done and acceptance OK,
// 1 bad arguments, 2 acceptance failed, 3 --until not reached within --days, 4 AI service unavailable,
// 130 stopped with Ctrl+C; replay: 0 REPLAY_OK, 5 REPLAY_DIFF, 6 REPLAY_CONTENT_CHANGED; perf: 0, 7 PERF_OVER_BUDGET.
// See src/Simulation.Headless/README.md.
if (args.Length > 0 && args[0] == "worldgen")
{
    return WorldGenCommand.Run(args[1..]);
}

try
{
    if (args.Length > 0 && args[0] == "replay")
    {
        return ReplayCommand.Run(args[1..], Console.Out);
    }

    if (args.Length > 0 && args[0] == "perf")
    {
        return PerfCommand.Run(args[1..], Console.Out);
    }

    var options = HeadlessOptions.Parse(args);
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        // First Ctrl+C: finish the tick, stop the AI service, print FINAL. A second one ends the process
        // at once (the Job Object still takes the service with it).
        if (!stop.IsCancellationRequested)
        {
            e.Cancel = true;
            stop.Cancel();
        }
    };
    return new RunCommand().Run(options, Console.Out, stop.Token);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return ExitCodes.Usage;
}
