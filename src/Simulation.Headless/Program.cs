using System.Diagnostics;
using System.Globalization;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.State;

// Usage: dotnet run --project src/Simulation.Headless -- [--seed N] [--days N] [--content DIR] [--save FILE] [--preset P] [--quiet]
// Runs the placeholder start world, or with --preset the island generated from seed and preset (B2-2),
// with the mock decider and prints one
// line per game day: lowest needs, will to live and neglect per inhabitant, and the state hash.
// --save writes an autosave after every game day and checks at the end that loading gives the same hash.
if (args.Length > 0 && args[0] == "worldgen")
{
    Environment.Exit(GlassWorld.Simulation.Headless.WorldGenCommand.Run(args[1..]));
}

var options = Options.Parse(args);
var host = options.Preset is null
    ? SimulationHost.CreatePlaceholderWorld(options.ContentDirectory, options.Seed, new MockDecisionPort(), "de")
    : SimulationHost.CreateGeneratedWorld(options.ContentDirectory, options.Seed, new MockDecisionPort(), "de", options.Preset);
var ticksPerDay = host.World.Calendar.TicksPerDay;
var ticksPerHour = host.World.Calendar.Settings.TicksPerHour;
if (options.SavePath is not null)
{
    _ = new AutoSaver(host, options.SavePath);
}

var stopwatch = Stopwatch.StartNew();
var longestNeglect = new Dictionary<string, long>(StringComparer.Ordinal);
for (var day = 1; day <= options.Days; day++)
{
    for (var t = 0; t < ticksPerDay; t++)
    {
        host.World.Step();
        if (options.Trace is not null && day <= options.TraceDays)
        {
            foreach (var e in host.Inhabitants.Events.Where(e => e.InhabitantId == options.Trace && e.Tick == host.World.Tick - 1))
            {
                var tr = host.Inhabitants.Get(options.Trace);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"   t{e.Tick,6} {e.Kind,-17} {e.Action,-8} {e.TargetId,-11} hunger {tr.Needs["hunger"]:F2} thirst {tr.Needs["thirst"]:F2} sleep {tr.Needs["sleep"]:F2}"));
            }
        }
        foreach (var i in host.Inhabitants.All)
        {
            longestNeglect[i.Id] = Math.Max(longestNeglect.GetValueOrDefault(i.Id), i.NeglectTicks);
        }
    }

    if (!options.Quiet)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"day {day,4}  hash {host.World.StateHash()[..12]}  objects {host.Objects.Count,4}"));
        foreach (var i in host.Inhabitants.All)
        {
            var lowest = i.Needs.OrderBy(n => n.Value).ThenBy(n => n.Key, StringComparer.Ordinal).Take(2).Select(n => string.Create(CultureInfo.InvariantCulture, $"{n.Key}={n.Value:F2}"));
            var food = host.Objects.All.Where(o => o.TypeId == "apple").Select(o => Math.Sqrt(((o.Position.X - i.Position.X) * (o.Position.X - i.Position.X)) + ((o.Position.Z - i.Position.Z) * (o.Position.Z - i.Position.Z)))).DefaultIfEmpty(-1).Min();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"      pos ({i.Position.X:F0},{i.Position.Z:F0}) apple {food:F0} m  edible-belief {(i.Beliefs.TryGet("apple", "edible", out var b) ? b.Value + "/" + b.Confidence.ToString("F2", CultureInfo.InvariantCulture) : "none")}"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"      {i.Id} {i.Sex,-6} will {i.WillToLive:F2}  neglect {i.NeglectTicks / ticksPerHour,4} h  low: {string.Join(", ", lowest)}  doing {i.Activity?.Action ?? "-"}"));
        }
    }
}

stopwatch.Stop();
var ticks = host.World.Tick;
var limitHours = host.Content.InhabitantModel.WillToLive.NeglectHoursBeforeDecline;
var worstHours = longestNeglect.Count == 0 ? 0 : longestNeglect.Values.Max() / ticksPerHour;
var fading = host.Inhabitants.All.Count(host.Inhabitants.IsFading);
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"done  seed {options.Seed}  days {options.Days}  inhabitants {host.Inhabitants.All.Count()}  ticks {ticks}  hash {host.World.StateHash()}"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"neglect  longest {worstHours} h (limit {limitHours} h)  fading {fading}"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"time  {stopwatch.Elapsed.TotalSeconds:F2} s  ({stopwatch.Elapsed.TotalMicroseconds / Math.Max(1, ticks):F1} us/tick)"));
var ok = worstHours <= limitHours && fading == 0;
if (options.SavePath is not null)
{
    var loaded = StateCodec.Restore(
        new SimulationHostFactory(host.Content, host.DeciderSettings, new MockDecisionPort(), "de"),
        SaveGameStore.Load(options.SavePath));
    var same = loaded.World.StateHash() == host.World.StateHash();
    Console.WriteLine($"save  {options.SavePath}  hash after load {(same ? "identical" : "DIFFERENT")}");
    ok &= same;
}

Console.WriteLine(ok ? "ACCEPTANCE_OK" : "ACCEPTANCE_FAIL");
return ok ? 0 : 1;

internal sealed record Options(ulong Seed, long Days, string ContentDirectory, string? SavePath, bool Quiet, string? Trace, long TraceDays, string? Preset)
{
    public static Options Parse(string[] args)
    {
        ulong seed = 1;
        long days = 30;
        string? content = null;
        string? save = null;
        var quiet = false;
        string? trace = null;
        long traceDays = 1;
        string? preset = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seed":
                    seed = ulong.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--days":
                    days = long.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--content":
                    content = args[++i];
                    break;
                case "--save":
                    save = args[++i];
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                case "--trace":
                    trace = args[++i];
                    break;
                case "--preset":
                    preset = args[++i];
                    break;
                case "--trace-days":
                    traceDays = long.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. Use --seed N --days N --content DIR --save FILE --preset P --quiet.");
            }
        }

        return new Options(seed, days, content ?? FindContent(), save, quiet, trace, traceDays, preset);
    }

    private static string FindContent()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "content");
                if (File.Exists(Path.Combine(candidate, "world", "time.json")))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException("content/world/time.json not found; pass --content DIR.");
    }
}
