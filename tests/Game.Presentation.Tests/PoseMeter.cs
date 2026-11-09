using GlassWorld.Simulation;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.View;

namespace GlassWorld.Presentation.Tests;

/// <summary>Where a pose change came from.</summary>
public enum PoseSource
{
    /// <summary>The figure starts or stops walking.</summary>
    Walk,

    /// <summary>The core's action (or its target) changed.</summary>
    Action,

    /// <summary>Same action, another idle behaviour (idle variety).</summary>
    Idle,
}

/// <summary>
/// Counts pose changes of figures (W1697) without Godot: drives the core like the island presenter does
/// (frames at 60 fps, ticks from the speed, max ticks per frame) and asks a pose function per figure every
/// frame (<c>applied</c>: the frame had ticks, FigureView.Apply ran; else only its per-frame update). A pose is the clip asset (and mirroring) a figure plays,
/// or "walk"; a change is counted with its source.
/// </summary>
public static class PoseMeter
{
    /// <summary>Frames per real second of the measurement.</summary>
    public const double Fps = 60;

    /// <summary>One figure's pose after a frame: what it plays as a key (asset/mirror or "walk").</summary>
    public delegate string Pose(InhabitantSnapshot inhabitant, IReadOnlyList<InhabitantSnapshot> all, long tick, bool moving, double realS, long frame, bool applied);

    /// <summary>Result: changes per inhabitant and source, the real seconds, frames with two or more changes.</summary>
    public sealed record Result(Dictionary<string, Dictionary<PoseSource, int>> Changes, double RealS, int SharedFrames, Dictionary<string, int> PeakPerMinute);

    /// <summary>Runs <paramref name="ticks"/> core ticks at <paramref name="speed"/>; each figure's pose comes from <paramref name="factory"/>.</summary>
    public static Result Run(SimulationHost host, double speed, long ticks, double walkMinStepM, IReadOnlySet<string> idleClips, Func<string, Pose> factory)
    {
        ArgumentNullException.ThrowIfNull(idleClips);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(factory);
        var calendar = host.World.Calendar;
        var perFrame = speed / Fps / calendar.RealSecondsPerTickAtNormalSpeed;
        var max = calendar.Settings.MaxTicksPerFrame;
        var poses = new Dictionary<string, Pose>(StringComparer.Ordinal);
        var last = new Dictionary<string, (string Pose, string? Action, string? Target, Position Position)>(StringComparer.Ordinal);
        var changes = new Dictionary<string, Dictionary<PoseSource, int>>(StringComparer.Ordinal);
        var minutes = new Dictionary<(string Id, long Minute), int>();
        var shared = 0;
        var accumulator = 0.0;
        var start = host.World.Tick;
        var moving = new Dictionary<string, bool>(StringComparer.Ordinal);
        GlassWorld.Simulation.View.WorldSnapshot? snapshot = null;
        for (long frame = 0; host.World.Tick - start < ticks; frame++)
        {
            accumulator += perFrame;
            var run = Math.Min((long)accumulator, max);
            accumulator = Math.Min(accumulator - run, max);
            var applied = run > 0;
            if (applied)
            {
                host.World.Run(run);
                snapshot = host.Snapshot();
            }

            if (snapshot is null)
            {
                continue;
            }

            var realS = frame / Fps;
            var changed = 0;
            foreach (var inhabitant in snapshot.Inhabitants)
            {
                if (!poses.TryGetValue(inhabitant.Id, out var pose))
                {
                    pose = poses[inhabitant.Id] = factory(inhabitant.Id);
                }

                var before = last.GetValueOrDefault(inhabitant.Id);
                if (applied)
                {
                    moving[inhabitant.Id] = before.Pose is not null && Distance(before.Position, inhabitant.Position) >= walkMinStepM;
                }

                var now = pose(inhabitant, snapshot.Inhabitants, snapshot.Time.Tick, moving.GetValueOrDefault(inhabitant.Id), realS, frame, applied);
                if (before.Pose is not null && now != before.Pose)
                {
                    var source = now == "walk" || before.Pose == "walk" ? PoseSource.Walk
                        : before.Action != inhabitant.Action || before.Target != inhabitant.TargetId || !idleClips.Contains(now) ? PoseSource.Action
                        : PoseSource.Idle;
                    var counts = changes.TryGetValue(inhabitant.Id, out var c) ? c : changes[inhabitant.Id] = [];
                    counts[source] = counts.GetValueOrDefault(source) + 1;
                    var key = (inhabitant.Id, (long)(realS / 60));
                    minutes[key] = minutes.GetValueOrDefault(key) + 1;
                    changed++;
                }

                last[inhabitant.Id] = (now, inhabitant.Action, inhabitant.TargetId, inhabitant.Position);
            }

            if (changed > 1)
            {
                shared++;
            }
        }

        var realTotal = ticks * calendar.RealSecondsPerTickAtNormalSpeed / speed;
        var peak = minutes.GroupBy(m => m.Key.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Max(m => m.Value), StringComparer.Ordinal);
        return new Result(changes, realTotal, shared, peak);
    }

    /// <summary>A table line per inhabitant: changes per real minute by source and the peak minute.</summary>
    public static IEnumerable<string> Table(string label, Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var minutes = result.RealS / 60;
        foreach (var (id, counts) in result.Changes.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var total = counts.Values.Sum();
            yield return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"| {label} | {id} | {total / minutes:0.0} | {counts.GetValueOrDefault(PoseSource.Idle) / minutes:0.0} | {counts.GetValueOrDefault(PoseSource.Action) / minutes:0.0} | {counts.GetValueOrDefault(PoseSource.Walk) / minutes:0.0} | {result.PeakPerMinute.GetValueOrDefault(id)} |");
        }

        yield return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"| {label} | frames with 2+ figures changing | {result.SharedFrames} | | | | |");
    }

    private static double Distance(Position a, Position b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));
}
