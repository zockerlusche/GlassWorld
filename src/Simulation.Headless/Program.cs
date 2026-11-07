using System.Diagnostics;
using System.Globalization;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Time;

// Usage: dotnet run --project src/Simulation.Headless -- [--seed N] [--days N] [--content DIR]
// Prints one line per world day (day, season, hash) and a summary. Exit code 0 on success.
var options = Options.Parse(args);
var calendar = WorldCalendar.FromJson(File.ReadAllText(Path.Combine(options.ContentDirectory, "world", "time.json")));
var world = new World(options.Seed, calendar);

var daysDone = 0L;
world.BoundaryReached += (w, boundary) =>
{
    if (boundary == TimeBoundary.Day)
    {
        daysDone++;
        var now = w.Now;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"day {now.Day - 1,6} done  now year {now.Year,4} {now.SeasonId,-7} day {now.DayOfSeason}  hash {w.StateHash()[..16]}"));
    }
};

var stopwatch = Stopwatch.StartNew();
world.Run(options.Days * calendar.TicksPerDay);
stopwatch.Stop();

var ticks = world.Tick;
var microsPerTick = ticks == 0 ? 0 : stopwatch.Elapsed.TotalMicroseconds / ticks;
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"done  seed {options.Seed}  days {daysDone}  ticks {ticks}  hash {world.StateHash()}"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"time  {stopwatch.Elapsed.TotalSeconds:F3} s  ({microsPerTick:F3} us/tick)"));
return 0;

internal sealed record Options(ulong Seed, long Days, string ContentDirectory)
{
    public static Options Parse(string[] args)
    {
        ulong seed = 1;
        long days = 30;
        string? content = null;
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
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. Use --seed N --days N --content DIR.");
            }
        }

        return new Options(seed, days, content ?? FindContent());
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
