using System.Globalization;
using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>Where a benchmark run is (B2-7): warming up a pass, measuring it, or done.</summary>
public enum BenchmarkStage
{
    /// <summary>The look of the pass is shown, frames are not recorded yet.</summary>
    Warmup,

    /// <summary>Frames are recorded; the camera runs its path.</summary>
    Measure,

    /// <summary>Every pass is measured.</summary>
    Done,
}

/// <summary>One moment of a benchmark run.</summary>
/// <param name="Pass">Index of the pass in the schedule (the pass count when done).</param>
/// <param name="Stage">Warm-up, measuring or done.</param>
/// <param name="MeasureS">Seconds since the measuring of this pass began (0 while warming up).</param>
public readonly record struct BenchmarkMoment(int Pass, BenchmarkStage Stage, double MeasureS);

/// <summary>
/// The pass schedule of the FPS benchmark (B2-7, acceptance 5): each pass is warmed up for
/// <see cref="WarmupS"/> real seconds and then measured for <see cref="DurationS"/>, one after the other in
/// the order of content/look/benchmark.json. Presentation only.
/// </summary>
public sealed class BenchmarkSchedule
{
    /// <summary>Selection that runs every pass of the data.</summary>
    public const string All = "all";

    /// <summary>Creates a schedule.</summary>
    public BenchmarkSchedule(IReadOnlyList<BenchmarkPass> passes, double warmupS, double durationS)
    {
        ArgumentNullException.ThrowIfNull(passes);
        if (passes.Count == 0 || warmupS < 0 || durationS <= 0)
        {
            throw new ArgumentException("A benchmark needs at least one pass, a warm-up >= 0 and a duration > 0.");
        }

        (Passes, WarmupS, DurationS) = (passes, warmupS, durationS);
    }

    /// <summary>The passes in run order.</summary>
    public IReadOnlyList<BenchmarkPass> Passes { get; }

    /// <summary>Warm-up seconds per pass.</summary>
    public double WarmupS { get; }

    /// <summary>Measured seconds per pass.</summary>
    public double DurationS { get; }

    /// <summary>Length of the whole run in seconds.</summary>
    public double TotalS => Passes.Count * (WarmupS + DurationS);

    /// <summary>
    /// The schedule of the data: every pass (<paramref name="selection"/> null or <see cref="All"/>) or the one
    /// named; the warm-up and duration of the data unless overridden (short headless checks).
    /// </summary>
    public static BenchmarkSchedule From(LookBenchmark data, string? selection, double? durationS = null, double? warmupS = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var ids = data.Passes.Select(p => p.Id).ToList();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
        {
            throw new InvalidDataException("Benchmark pass ids must be unique (content/look/benchmark.json).");
        }

        var passes = selection is null || selection == All
            ? data.Passes.ToList()
            : [data.Passes.FirstOrDefault(p => p.Id == selection) ?? throw new InvalidDataException($"Unknown benchmark pass '{selection}' (content/look/benchmark.json: {string.Join(", ", ids)}, or {All}).")];
        return new BenchmarkSchedule(passes, warmupS ?? data.WarmupS, durationS ?? data.DurationS);
    }

    /// <summary>Where the run is after <paramref name="elapsedS"/> real seconds.</summary>
    public BenchmarkMoment At(double elapsedS)
    {
        if (elapsedS >= TotalS)
        {
            return new BenchmarkMoment(Passes.Count, BenchmarkStage.Done, 0);
        }

        var slot = WarmupS + DurationS;
        var pass = Math.Clamp((int)Math.Floor(Math.Max(0, elapsedS) / slot), 0, Passes.Count - 1);
        var inPass = Math.Max(0, elapsedS - (pass * slot));
        return inPass < WarmupS
            ? new BenchmarkMoment(pass, BenchmarkStage.Warmup, 0)
            : new BenchmarkMoment(pass, BenchmarkStage.Measure, inPass - WarmupS);
    }

    /// <summary>Reads <c>WIDTHxHEIGHT</c> (e.g. <c>3840x1600</c>) of the <c>--resolution</c> argument.</summary>
    public static (int Width, int Height) ParseSize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Split('x', 'X');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var height)
            && width > 0 && height > 0)
        {
            return (width, height);
        }

        throw new FormatException($"Resolution '{text}' is not WIDTHxHEIGHT (e.g. 3840x1600).");
    }
}

/// <summary>A camera pose of the benchmark path: target on the ground (metres), yaw, pitch and distance.</summary>
public readonly record struct BenchmarkCameraPose(double X, double Z, double YawDeg, double PitchDeg, double DistanceM);

/// <summary>
/// The camera path of the benchmark (overview, turning, zoom, following): between two keys of the data every
/// value runs linearly, the yaw unwrapped (0 → 360 is one full turn). A key's target is the crowd centre or a
/// walking crowd figure (<c>follow</c>); between keys the target blends from one to the other.
/// </summary>
public static class BenchmarkCameraPath
{
    /// <summary>Checks the keys: in time order and every followed figure exists.</summary>
    public static void Check(IReadOnlyList<BenchmarkCameraKey> keys, int walkers)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            throw new InvalidDataException("The benchmark camera path has no keys (content/look/benchmark.json).");
        }

        for (var i = 1; i < keys.Count; i++)
        {
            if (keys[i].AtS < keys[i - 1].AtS)
            {
                throw new InvalidDataException($"Benchmark camera keys must be in time order (key {i} at {keys[i].AtS} s).");
            }
        }

        if (keys.FirstOrDefault(k => k.Follow is { } f && f >= walkers) is { } bad)
        {
            throw new InvalidDataException($"Benchmark camera key at {bad.AtS} s follows walking figure {bad.Follow}, but the crowd has {walkers} walking figures.");
        }
    }

    /// <summary>
    /// The pose at <paramref name="timeS"/> seconds of a pass; <paramref name="scale"/> = pass duration ÷ the
    /// data's duration stretches the key times; <paramref name="target"/> gives the target of a key (null =
    /// the crowd centre, else the index of the walking figure followed) at this moment.
    /// </summary>
    public static BenchmarkCameraPose At(IReadOnlyList<BenchmarkCameraKey> keys, double timeS, double scale, Func<int?, (double X, double Z)> target)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(target);
        var t = scale > 0 ? timeS / scale : 0;
        var next = 0;
        while (next < keys.Count && keys[next].AtS <= t)
        {
            next++;
        }

        if (next == 0 || next == keys.Count)
        {
            return Pose(keys[next == 0 ? 0 : keys.Count - 1], target);
        }

        var (a, b) = (keys[next - 1], keys[next]);
        var u = b.AtS > a.AtS ? (t - a.AtS) / (b.AtS - a.AtS) : 1;
        var (from, to) = (target(a.Follow), target(b.Follow));
        return new BenchmarkCameraPose(
            Lerp(from.X, to.X, u),
            Lerp(from.Z, to.Z, u),
            Lerp(a.YawDeg, b.YawDeg, u),
            Lerp(a.PitchDeg, b.PitchDeg, u),
            Lerp(a.DistanceM, b.DistanceM, u));
    }

    private static BenchmarkCameraPose Pose(BenchmarkCameraKey key, Func<int?, (double X, double Z)> target)
    {
        var (x, z) = target(key.Follow);
        return new BenchmarkCameraPose(x, z, key.YawDeg, key.PitchDeg, key.DistanceM);
    }

    private static double Lerp(double a, double b, double u) => a + ((b - a) * u);
}

/// <summary>One placeholder figure of the benchmark crowd.</summary>
/// <param name="Id">Figure id (<c>bench_000</c> …), never an inhabitant id.</param>
/// <param name="Group">Index of its group in the data.</param>
/// <param name="Template">Index of the inhabitant whose look it borrows.</param>
/// <param name="HomeX">Home spot X in metres.</param>
/// <param name="HomeZ">Home spot Z in metres.</param>
/// <param name="PhaseRad">Start angle on the walking circle.</param>
/// <param name="Direction">+1 or -1: the way round the circle.</param>
/// <param name="OffsetS">Shift of its action changes, so the crowd does not change all at once.</param>
public sealed record CrowdFigure(string Id, int Group, int Template, double HomeX, double HomeZ, double PhaseRad, int Direction, double OffsetS);

/// <summary>
/// The crowd of the benchmark (B2-7): placeholder figures for the presentation only (they never enter the core
/// or the state hash). Home spots lie in a sunflower pattern on land around the centre; the groups of the data
/// are spread evenly over the crowd by weight; walking figures go round a small circle at their home, standing
/// ones take the actions of their group in turn. Deterministic: the same data and seed give the same crowd.
/// </summary>
public sealed class BenchmarkCrowdPlan
{
    /// <summary>Prefix of every crowd figure id.</summary>
    public const string IdPrefix = "bench_";

    private const double GoldenAngle = 2.399963229728653;
    private readonly BenchmarkCrowd _data;
    private readonly List<BenchmarkCrowdGroup> _groups;

    private BenchmarkCrowdPlan(BenchmarkCrowd data, IReadOnlyList<CrowdFigure> figures)
    {
        (_data, _groups, Figures) = (data, [.. data.Groups], figures);
        Walkers = [.. figures.Where(f => _groups[f.Group].Walks)];
    }

    /// <summary>All figures in id order.</summary>
    public IReadOnlyList<CrowdFigure> Figures { get; }

    /// <summary>The walking figures in id order (the camera's <c>follow</c> index points into this list).</summary>
    public IReadOnlyList<CrowdFigure> Walkers { get; }

    /// <summary>The group of a figure.</summary>
    public BenchmarkCrowdGroup GroupOf(CrowdFigure figure) => _groups[(figure ?? throw new ArgumentNullException(nameof(figure))).Group];

    /// <summary>
    /// Plans the crowd around <paramref name="centre"/>: <paramref name="templates"/> inhabitants lend their
    /// look in turn, <paramref name="onLand"/> says whether a spot is dry ground, <paramref name="knownActions"/>
    /// are the action ids the animation library maps.
    /// </summary>
    public static BenchmarkCrowdPlan Build(BenchmarkCrowd data, ulong seed, (double X, double Z) centre, int templates, Func<double, double, bool> onLand, IReadOnlySet<string> knownActions)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(onLand);
        ArgumentNullException.ThrowIfNull(knownActions);
        if (data.Count > 0 && templates <= 0)
        {
            throw new InvalidDataException("The benchmark crowd needs at least one inhabitant with an appearance to borrow its look.");
        }

        var groups = data.Groups.ToList();
        if (groups.SelectMany(g => g.Actions).FirstOrDefault(a => !knownActions.Contains(a)) is { } unknown)
        {
            throw new InvalidDataException($"Benchmark crowd action '{unknown}' has no action mapping in content/animation/library.json.");
        }

        var spots = Spots(data, seed, centre, onLand);
        var assigned = new int[groups.Count];
        var total = groups.Sum(g => (double)g.Weight);
        var figures = new List<CrowdFigure>(data.Count);
        for (var i = 0; i < data.Count; i++)
        {
            // Even spread by weight: the group furthest behind its share takes the next figure.
            var group = 0;
            var behind = double.NegativeInfinity;
            for (var g = 0; g < groups.Count; g++)
            {
                var gap = (groups[g].Weight * (i + 1) / total) - assigned[g];
                if (gap > behind)
                {
                    (group, behind) = (g, gap);
                }
            }

            assigned[group]++;
            var hash = Hash(seed, (ulong)i);
            figures.Add(new CrowdFigure(
                string.Create(CultureInfo.InvariantCulture, $"{IdPrefix}{i:000}"),
                group,
                i % templates,
                spots[i].X,
                spots[i].Z,
                Unit(hash) * 2 * Math.PI,
                (hash & 1) == 0 ? 1 : -1,
                Unit(hash >> 11) * data.ActionChangeS));
        }

        return new BenchmarkCrowdPlan(data, figures);
    }

    /// <summary>Where a figure is <paramref name="timeS"/> real seconds after the start.</summary>
    public (double X, double Z) Position(CrowdFigure figure, double timeS)
    {
        ArgumentNullException.ThrowIfNull(figure);
        if (!_groups[figure.Group].Walks)
        {
            return (figure.HomeX, figure.HomeZ);
        }

        var angle = figure.PhaseRad + (figure.Direction * _data.WalkSpeedMS / _data.WalkRadiusM * timeS);
        return (figure.HomeX + (_data.WalkRadiusM * Math.Cos(angle)), figure.HomeZ + (_data.WalkRadiusM * Math.Sin(angle)));
    }

    /// <summary>How many action changes a figure has had at <paramref name="timeS"/> (a new number = a new action).</summary>
    public long ActionStep(CrowdFigure figure, double timeS)
    {
        ArgumentNullException.ThrowIfNull(figure);
        return (long)Math.Floor(Math.Max(0, timeS + figure.OffsetS) / _data.ActionChangeS);
    }

    /// <summary>The core action id a figure shows at <paramref name="timeS"/>.</summary>
    public string Action(CrowdFigure figure, double timeS)
    {
        var actions = _groups[(figure ?? throw new ArgumentNullException(nameof(figure))).Group].Actions.ToList();
        return actions[(int)(ActionStep(figure, timeS) % actions.Count)];
    }

    private static List<(double X, double Z)> Spots(BenchmarkCrowd data, ulong seed, (double X, double Z) centre, Func<double, double, bool> onLand)
    {
        // Sunflower pattern: one spot per spacing² of area, turned by the seed; water spots are skipped.
        var turn = Unit(Hash(seed, ulong.MaxValue)) * 2 * Math.PI;
        var spots = new List<(double X, double Z)>(data.Count);
        var limit = Math.Max(64, data.Count * 50);
        for (var k = 0; spots.Count < data.Count; k++)
        {
            if (k >= limit)
            {
                throw new InvalidDataException($"Only {spots.Count} of {data.Count} benchmark crowd spots lie on land; lower crowd.spacing_m or crowd.count (content/look/benchmark.json).");
            }

            var r = data.SpacingM * Math.Sqrt((k + 0.5) / Math.PI);
            var a = turn + (k * GoldenAngle);
            var (x, z) = (centre.X + (r * Math.Cos(a)), centre.Z + (r * Math.Sin(a)));
            if (onLand(x, z))
            {
                spots.Add((x, z));
            }
        }

        return spots;
    }

    private static ulong Hash(ulong seed, ulong index)
    {
        // SplitMix64 of seed and index: the same crowd on every machine.
        var z = seed + ((index + 1) * 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static double Unit(ulong hash) => (hash >> 11) / (double)(1UL << 53);
}

/// <summary>Mean, 95th percentile and maximum of one measured pass.</summary>
/// <param name="Pass">Pass id.</param>
/// <param name="Frames">Frames recorded.</param>
/// <param name="MeanFrameMs">Mean frame time.</param>
/// <param name="P95FrameMs">95th percentile of the frame time (nearest rank).</param>
/// <param name="MaxFrameMs">Longest frame.</param>
public sealed record BenchmarkSummary(string Pass, int Frames, double MeanFrameMs, double P95FrameMs, double MaxFrameMs)
{
    /// <summary>Mean frames per second: frames ÷ measured time.</summary>
    public double MeanFps => MeanFrameMs > 0 ? 1000.0 / MeanFrameMs : 0;

    /// <summary>Whether the pass meets the marks (mean FPS at least, p95 frame time at most).</summary>
    public bool Meets(double minMeanFps, double maxP95FrameMs) => Frames > 0 && MeanFps >= minMeanFps && P95FrameMs <= maxP95FrameMs;

    /// <summary>The summary line of the log (one per pass, for a quick read).</summary>
    public string Line(double minMeanFps, double maxP95FrameMs) => string.Create(
        CultureInfo.InvariantCulture,
        $"BENCH_PASS pass={Pass} frames={Frames} mean_fps={MeanFps:0.0} p95_ms={P95FrameMs:0.00} max_ms={MaxFrameMs:0.00} ok={(Meets(minMeanFps, maxP95FrameMs) ? "yes" : "no")}");
}

/// <summary>
/// Frame times of a benchmark run, one row per measured frame (pass, seconds since the pass's measuring began,
/// frame time in ms), written as CSV for the evaluation (GW2: mean, p95, max) and summed up per pass.
/// </summary>
public sealed class BenchmarkRecorder
{
    /// <summary>Header line of the CSV.</summary>
    public const string Header = "pass,time_s,frame_ms";

    private readonly List<(string Pass, double TimeS, double FrameMs)> _rows = [];

    /// <summary>Rows recorded so far.</summary>
    public int Count => _rows.Count;

    /// <summary>Records one frame.</summary>
    public void Add(string pass, double timeS, double frameMs)
    {
        ArgumentNullException.ThrowIfNull(pass);
        _rows.Add((pass, timeS, frameMs));
    }

    /// <summary>Mean, p95 and max of one pass (zeros without frames).</summary>
    public BenchmarkSummary Summary(string pass)
    {
        var times = _rows.Where(r => r.Pass == pass).Select(r => r.FrameMs).ToList();
        return times.Count == 0
            ? new BenchmarkSummary(pass, 0, 0, 0, 0)
            : new BenchmarkSummary(pass, times.Count, times.Average(), Percentile(times, 0.95), times.Max());
    }

    /// <summary>Writes the CSV: header, then one row per frame in the order recorded.</summary>
    public void WriteCsv(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(Header);
        writer.Write('\n');
        foreach (var (pass, time, frame) in _rows)
        {
            writer.Write(string.Create(CultureInfo.InvariantCulture, $"{pass},{time:0.0000},{frame:0.000}\n"));
        }
    }

    /// <summary>Nearest-rank percentile (<paramref name="share"/> 0..1) of the values.</summary>
    public static double Percentile(IReadOnlyCollection<double> values, double share)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        var rank = (int)Math.Ceiling(Math.Clamp(share, 0, 1) * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
