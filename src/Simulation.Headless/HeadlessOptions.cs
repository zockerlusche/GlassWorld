using System.Globalization;

namespace GlassWorld.Simulation.Headless;

/// <summary>Which decider the run uses.</summary>
internal enum AiMode
{
    /// <summary>The deterministic mock decider; generation jobs stay dormant (like the game without the service).</summary>
    Mock,

    /// <summary>The local AI service, started like the game starts it (ADR-0006).</summary>
    Live,
}

/// <summary><c>--until KIND[:COUNT]</c>: stop after the tick in which the COUNT-th core event of KIND happened.</summary>
/// <param name="Kind">Core event kind (content/vocabulary/events.json).</param>
/// <param name="Count">How many events of the kind end the run (at least 1).</param>
internal sealed record UntilCondition(string Kind, int Count)
{
    /// <summary>Parses <c>KIND</c> or <c>KIND:COUNT</c>.</summary>
    public static UntilCondition Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        var kind = colon < 0 ? value : value[..colon];
        if (kind.Length == 0)
        {
            throw new ArgumentException($"--until needs an event kind: '{value}'.");
        }

        if (colon < 0)
        {
            return new UntilCondition(kind, 1);
        }

        if (!int.TryParse(value[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
        {
            throw new ArgumentException($"--until count must be a whole number of at least 1: '{value}'.");
        }

        return new UntilCondition(kind, count);
    }
}

/// <summary><c>--events KIND,KIND,...</c> or <c>--events all</c>: which core events are printed as lines.</summary>
/// <param name="All">True for <c>all</c>.</param>
/// <param name="Kinds">The named kinds (empty for <c>all</c>).</param>
internal sealed record EventFilter(bool All, IReadOnlySet<string> Kinds)
{
    /// <summary>Parses the option value.</summary>
    public static EventFilter Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var kinds = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (kinds.Length == 0)
        {
            throw new ArgumentException("--events needs 'all' or a comma-separated list of event kinds.");
        }

        return kinds.Contains("all", StringComparer.Ordinal)
            ? new EventFilter(true, new HashSet<string>(StringComparer.Ordinal))
            : new EventFilter(false, kinds.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>True if events of <paramref name="kind"/> are printed.</summary>
    public bool Includes(string kind) => All || Kinds.Contains(kind);
}

/// <summary>Options of the run command (the default command of the headless runner).</summary>
internal sealed record HeadlessOptions(ulong Seed, long Days, string ContentDirectory, string? SavePath, bool Quiet, string? Trace, long TraceDays, string? Preset)
{
    /// <summary>Usage line for errors.</summary>
    public const string Usage = "Use --seed N --days N --content DIR --save FILE --preset P --quiet --trace ID --trace-days N --ai live|mock --tick-ms N --until KIND[:COUNT] --events KIND,...|all --log-json FILE.";

    /// <summary>Decider: mock (default) or the live AI service.</summary>
    public AiMode Ai { get; init; } = AiMode.Mock;

    /// <summary>Fixed real milliseconds per tick; 0 = as fast as possible (see the README of the runner for live pacing).</summary>
    public int TickMs { get; init; }

    /// <summary>Stop condition, or null to run all days.</summary>
    public UntilCondition? Until { get; init; }

    /// <summary>Which core events are printed, or null for none.</summary>
    public EventFilter? Events { get; init; }

    /// <summary>File for the decision and job log as JSON lines, or null.</summary>
    public string? LogJsonPath { get; init; }

    /// <summary>Parses the arguments; throws <see cref="ArgumentException"/> on unknown or bad arguments.</summary>
    /// <param name="args">Command line arguments.</param>
    /// <param name="findContent">Finds the content folder when <c>--content</c> is missing.</param>
    public static HeadlessOptions Parse(string[] args, Func<string>? findContent = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ulong seed = 1;
        long days = 30;
        string? content = null;
        string? save = null;
        var quiet = false;
        string? trace = null;
        long traceDays = 1;
        string? preset = null;
        var ai = AiMode.Mock;
        var tickMs = 0;
        UntilCondition? until = null;
        EventFilter? events = null;
        string? logJson = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seed":
                    seed = ulong.Parse(Value(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--days":
                    days = long.Parse(Value(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--content":
                    content = Value(args, ref i);
                    break;
                case "--save":
                    save = Value(args, ref i);
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                case "--trace":
                    trace = Value(args, ref i);
                    break;
                case "--preset":
                    preset = Value(args, ref i);
                    break;
                case "--trace-days":
                    traceDays = long.Parse(Value(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--ai":
                    ai = Value(args, ref i) switch
                    {
                        "mock" => AiMode.Mock,
                        "live" => AiMode.Live,
                        var other => throw new ArgumentException($"--ai must be 'live' or 'mock', not '{other}'."),
                    };
                    break;
                case "--tick-ms":
                    tickMs = int.Parse(Value(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture);
                    break;
                case "--until":
                    until = UntilCondition.Parse(Value(args, ref i));
                    break;
                case "--events":
                    events = EventFilter.Parse(Value(args, ref i));
                    break;
                case "--log-json":
                    logJson = Value(args, ref i);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. {Usage}");
            }
        }

        if (days < 0)
        {
            throw new ArgumentException("--days must not be negative.");
        }

        return new HeadlessOptions(seed, days, content ?? (findContent ?? ContentFolder.Find)(), save, quiet, trace, traceDays, preset)
        {
            Ai = ai,
            TickMs = tickMs,
            Until = until,
            Events = events,
            LogJsonPath = logJson,
        };
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

/// <summary>Finds the repository's content folder from the working directory or the program folder.</summary>
internal static class ContentFolder
{
    /// <summary>The nearest <c>content</c> folder that holds <c>world/time.json</c>.</summary>
    public static string Find()
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
