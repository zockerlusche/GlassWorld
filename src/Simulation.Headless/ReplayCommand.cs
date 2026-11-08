using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Headless;

/// <summary>
/// <c>replay --save FILE [--content DIR]</c>: rebuilds the world from the save game's seed (and preset)
/// alone, plays the saved decision and job logs back with the replay ports (answers, job outcomes and
/// player commands at the ticks they took effect), runs to the saved tick and compares the state hash with
/// the saved world's hash (the hash right after loading, ADR-0003).
/// <list type="bullet">
/// <item><c>REPLAY_OK hash=.. ticks=..</c>, exit 0: same hash.</item>
/// <item><c>REPLAY_DIFF tick=T</c>, exit 1: the replay's own decision or job log departed from the saved
/// log at tick T (first entry that differs, or one that is missing or extra); <c>tick=end</c> when the
/// logs agree all the way and only the final hash differs.</item>
/// </list>
/// Generation jobs are replayed only if the saved run had them (its invention state is not empty), because
/// a world without a job port keeps inventions dormant (mock runs, the game without the service).
/// </summary>
internal static class ReplayCommand
{
    /// <summary>Runs the command; returns 0 (REPLAY_OK) or 1 (REPLAY_DIFF or bad arguments).</summary>
    public static int Run(string[] args, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        string? save = null;
        string? content = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--save" when i + 1 < args.Length:
                    save = args[++i];
                    break;
                case "--content" when i + 1 < args.Length:
                    content = args[++i];
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete argument '{args[i]}'. Use: replay --save FILE [--content DIR].");
            }
        }

        if (save is null)
        {
            throw new ArgumentException("replay needs --save FILE.");
        }

        var result = Replay(content ?? ContentFolder.Find(), SaveGameStore.Load(save));
        output.WriteLine(result.Line);
        return result.Ok ? ExitCodes.Ok : ExitCodes.Usage;
    }

    /// <summary>Replays a saved state from its seed and logs.</summary>
    public static ReplayResult Replay(string contentDirectory, SaveState saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var content = ContentSet.Load(contentDirectory);
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(contentDirectory, "decider", "settings.json")));

        // The saved world's hash: restoring gives exactly the state that was saved (ADR-0003).
        var savedHash = StateCodec.Restore(new SimulationHostFactory(content, settings, new MockDecisionPort(), "de"), saved).World.StateHash();

        var decisionLog = new DecisionLog();
        foreach (var entry in saved.DecisionLog)
        {
            decisionLog.Add(entry);
        }

        var jobLog = new DecisionLog();
        foreach (var entry in saved.Inventions?.Log ?? [])
        {
            jobLog.Add(entry);
        }

        var jobs = HadJobs(saved.Inventions) ? new ReplayJobPort(jobLog) : null;
        var port = new ReplayDecisionPort(decisionLog);
        var host = saved.WorldPreset is null
            ? SimulationHost.CreatePlaceholderWorld(contentDirectory, saved.Seed, port, "de", jobs)
            : SimulationHost.CreateGeneratedWorld(contentDirectory, saved.Seed, port, "de", saved.WorldPreset, jobs);
        if (saved.IslandHash is not null && host.Map?.IslandHash != saved.IslandHash)
        {
            // The data generate another island than the saved one: the replay cannot start alike.
            return new ReplayResult(false, "REPLAY_DIFF tick=0");
        }

        host.Decisions.LoadReplay(decisionLog);
        var decisionCheck = new LogCheck(decisionLog.Entries);
        var jobCheck = new LogCheck(jobLog.Entries);
        while (host.World.Tick < saved.Tick)
        {
            var tick = host.World.Tick;
            host.World.Step();
            if (decisionCheck.FirstDifference(host.Decisions.Log.Entries, tick) is { } differs)
            {
                return Diff(differs, jobCheck.FirstDifference(host.Inventions.Log.Entries, tick));
            }

            if (jobCheck.FirstDifference(host.Inventions.Log.Entries, tick) is { } jobDiffers)
            {
                return Diff(jobDiffers, null);
            }
        }

        var hash = host.World.StateHash();
        return hash == savedHash
            ? new ReplayResult(true, string.Create(CultureInfo.InvariantCulture, $"REPLAY_OK hash={hash} ticks={host.World.Tick}"))
            : new ReplayResult(false, "REPLAY_DIFF tick=end");
    }

    /// <summary>True if the saved run had a job port: only then does the invention system leave a trace.</summary>
    public static bool HadJobs(InventionState? state) =>
        state is not null && (state.Counter > 0 || state.Below.Count > 0 || state.Cooldowns.Count > 0 || state.Wants.Count > 0
            || state.Inventions.Count > 0 || state.Log.Count > 0 || (state.Builds?.Count ?? 0) > 0 || (state.FirstUsed?.Count ?? 0) > 0 || (state.Sounds?.Count ?? 0) > 0);

    private static ReplayResult Diff(long tick, long? other) =>
        new(false, string.Create(CultureInfo.InvariantCulture, $"REPLAY_DIFF tick={Math.Min(tick, other ?? tick)}"));

    /// <summary>Compares a growing replay log with the saved one, entry by entry.</summary>
    private sealed class LogCheck(IReadOnlyList<DecisionLogEntry> saved)
    {
        private int _checked;

        /// <summary>
        /// After the step of <paramref name="tick"/>: the tick of the first entry that differs, or of an
        /// entry the replay wrote too much or failed to write by now; null while both agree.
        /// </summary>
        public long? FirstDifference(IReadOnlyList<DecisionLogEntry> replayed, long tick)
        {
            for (; _checked < replayed.Count; _checked++)
            {
                var now = replayed[_checked];
                if (_checked >= saved.Count)
                {
                    return now.Tick;
                }

                var then = saved[_checked];
                if (now != then)
                {
                    return Math.Min(now.Tick, then.Tick);
                }
            }

            return _checked < saved.Count && saved[_checked].Tick <= tick ? saved[_checked].Tick : null;
        }
    }
}

/// <summary>Outcome of a replay.</summary>
/// <param name="Ok">True if the hashes are equal.</param>
/// <param name="Line">The result line.</param>
internal sealed record ReplayResult(bool Ok, string Line);
