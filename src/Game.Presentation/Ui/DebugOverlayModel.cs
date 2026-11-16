using System.Globalization;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Time;
using GlassWorld.Simulation.View;
using GlassWorld.Simulation.Weather;

namespace GlassWorld.Presentation.Ui;

/// <summary>What the debug view shows (B2-6 R31), read from the host and the screen.</summary>
/// <param name="Seed">World seed.</param>
/// <param name="WorldHash">Hash of the generated map (never changes), or null without a map.</param>
/// <param name="StateHash">State hash of the world at <paramref name="StateHashTick"/>.</param>
/// <param name="StateHashTick">Tick the state hash belongs to (it is not computed every frame).</param>
/// <param name="Time">World time.</param>
/// <param name="Weather">Weather now, or null without weather.</param>
/// <param name="LookEpoch">Look epoch id, or null.</param>
/// <param name="Fps">Frames per second.</param>
/// <param name="Figures">Number of figures (inhabitants) in the world.</param>
/// <param name="Biome">Biome id under the mouse pointer, or null (no map, pointer off the map).</param>
/// <param name="EpochKeys">Key names for the epoch step back and forward while debug jumps are on, else null.</param>
/// <param name="Knowledge">A1402: the chosen inhabitant's name and beliefs with their origin, newest first; null without one.</param>
/// <param name="Jobs">A1801: the AI's open jobs (kind, inhabitant's name, age in game minutes, waiting for the night); null = not read.</param>
public sealed record DebugOverlayData(
    ulong Seed,
    string? WorldHash,
    string StateHash,
    long StateHashTick,
    WorldTime Time,
    WeatherReading? Weather,
    string? LookEpoch,
    double Fps,
    int Figures,
    string? Biome,
    (string Back, string Forward)? EpochKeys = null,
    (string Name, int Count, IReadOnlyList<DebugBelief> Beliefs)? Knowledge = null,
    IReadOnlyList<(string Kind, string? Name, long Minutes, bool Waiting)>? Jobs = null);

/// <summary>One belief in the debug view (A1402, B4a-S7): what, whether, from where, how sure.</summary>
/// <param name="TypeId">The thing's type.</param>
/// <param name="PropertyId">The property.</param>
/// <param name="Value">Whether it has it, as the inhabitant believes.</param>
/// <param name="Source">experienced, heard, guessed, read or seen (knowledge sources).</param>
/// <param name="ToldBy">Name of the one who told it, or null.</param>
/// <param name="Confidence">How sure, 0..1.</param>
/// <param name="Tick">When it came.</param>
public sealed record DebugBelief(string TypeId, string PropertyId, bool Value, string Source, string? ToldBy, double Confidence, long Tick);

/// <summary>
/// The debug view without Godot (B2-6 R31, ADR-0008): its lines from the world, the biome under a ground
/// point and the epoch a debug step leads to. Every text is a key (KR4); ids from the data are shown by
/// their vocabulary name with the id behind it. The season is for the viewer only, never for the AI.
/// </summary>
public static class DebugOverlayModel
{
    /// <summary>InputMap action that shows and hides the debug view.</summary>
    public const string ToggleAction = "gw_debug_overlay";

    /// <summary>InputMap action: one look epoch back (debug jumps only).</summary>
    public const string EpochBackAction = "gw_debug_epoch_back";

    /// <summary>InputMap action: one look epoch forward (debug jumps only).</summary>
    public const string EpochForwardAction = "gw_debug_epoch_forward";

    /// <summary>Shown where the world has no value (no map, no weather); language-neutral.</summary>
    public const string None = "–";

    /// <summary>A1402: at most this many beliefs of the chosen inhabitant are listed (newest first); the count says how many it has.</summary>
    public const int MaxBeliefs = 12;

    /// <summary>Number of hash characters shown, as in the log lines of the game.</summary>
    public const int HashLength = 12;

    /// <summary>Reads what the view shows from a running world.</summary>
    /// <param name="host">The world.</param>
    /// <param name="stateHash">The state hash (the caller decides how often it is computed).</param>
    /// <param name="stateHashTick">Tick the state hash was computed at.</param>
    /// <param name="fps">Frames per second.</param>
    /// <param name="underMouse">Ground point under the mouse pointer in metres, or null.</param>
    /// <param name="epochKeys">Key names for the epoch steps while debug jumps are on, else null.</param>
    /// <param name="chosen">A1402: the inhabitant the player chose, whose knowledge and its origin are listed; null for none.</param>
    public static DebugOverlayData Read(SimulationHost host, string stateHash, long stateHashTick, double fps, (double X, double Z)? underMouse, (string Back, string Forward)? epochKeys = null, string? chosen = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(stateHash);
        var snapshot = host.Snapshot();
        return new DebugOverlayData(
            host.World.Seed,
            host.Map?.IslandHash,
            stateHash,
            stateHashTick,
            snapshot.Time,
            snapshot.Weather,
            snapshot.LookEpoch,
            fps,
            snapshot.Inhabitants.Count,
            underMouse is { } p ? BiomeAt(host.Map, p.X, p.Z) : null,
            host.Options.DebugCommands ? epochKeys : null,
            KnowledgeOf(host, snapshot, chosen),
            JobsOf(host, snapshot));
    }

    /// <summary>A1801 (Marcel W2243): the AI's open jobs - running ones, then the ones waiting for the night - with age in game minutes.</summary>
    private static IReadOnlyList<(string Kind, string? Name, long Minutes, bool Waiting)> JobsOf(SimulationHost host, WorldSnapshot snapshot)
    {
        string? NameOf(string? id) => id is null ? null : snapshot.Inhabitants.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal))?.Name ?? id;
        var perMinute = Math.Max(1.0, host.World.Calendar.Settings.TicksPerHour / 60.0);
        return [.. AiJobs.Open(host.Inventions.Log.Entries, host.NightQueue?.Held ?? [])
            .Select(j => (j.Kind, NameOf(j.InhabitantId), (long)Math.Max(0, (host.World.Tick - j.Tick) / perMinute), j.Waiting))];
    }

    /// <summary>A1402 (B4a-S7): the chosen inhabitant's beliefs with their origin, newest first (read only).</summary>
    private static (string Name, int Count, IReadOnlyList<DebugBelief> Beliefs)? KnowledgeOf(SimulationHost host, WorldSnapshot snapshot, string? chosen)
    {
        if (chosen is null || host.Inhabitants.All.FirstOrDefault(i => string.Equals(i.Id, chosen, StringComparison.Ordinal)) is not { } who)
        {
            return null;
        }

        string NameOf(string id) => snapshot.Inhabitants.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal))?.Name ?? id;
        var beliefs = who.Beliefs.All
            .OrderByDescending(b => b.Belief.Tick).ThenBy(b => b.TypeId, StringComparer.Ordinal).ThenBy(b => b.PropertyId, StringComparer.Ordinal)
            .Take(MaxBeliefs)
            .Select(b => new DebugBelief(b.TypeId, b.PropertyId, b.Belief.Value, b.Belief.Source, b.Belief.ToldBy is { } by ? NameOf(by) : null, b.Belief.Confidence, b.Belief.Tick))
            .ToList();
        return (NameOf(who.Id), who.Beliefs.Count, beliefs);
    }

    /// <summary>The lines of the view, top to bottom, in the language of <paramref name="translate"/>.</summary>
    /// <param name="data">What to show.</param>
    /// <param name="translate">Text of a key in the player's language.</param>
    public static IReadOnlyList<string> Lines(DebugOverlayData data, Func<string, string> translate)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(translate);
        var time = data.Time;
        string Line(string key, params (string Name, string Value)[] args) =>
            TextFill.Fill(translate(key), args.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal));
        string Named(string prefix, string? id) => id is null ? None : $"{translate(prefix + id)} ({id})";
        var lines = new List<string>
        {
            Line("ui.debug.seed", ("seed", data.Seed.ToString(CultureInfo.InvariantCulture))),
            Line("ui.debug.world_hash", ("hash", Short(data.WorldHash))),
            Line("ui.debug.state_hash", ("hash", Short(data.StateHash)), ("tick", data.StateHashTick.ToString(CultureInfo.InvariantCulture))),
            Line(
                "ui.debug.time",
                ("time", string.Create(CultureInfo.InvariantCulture, $"{time.Hour:00}:{time.Minute:00}")),
                ("phase", translate("time.phase." + WorldFeed.Phase(time.Phase))),
                ("day", time.DayOfSeason.ToString(CultureInfo.InvariantCulture)),
                ("year", time.Year.ToString(CultureInfo.InvariantCulture)),
                ("tick", time.Tick.ToString(CultureInfo.InvariantCulture))),
            Line("ui.debug.season", ("season", translate("time.season." + time.SeasonId))),
            Line(
                "ui.debug.weather",
                ("weather", Named("vocab.weather.", data.Weather?.Weather)),
                ("conditions", data.Weather is { Conditions.Count: > 0 } weather ? string.Join(", ", weather.Conditions.Select(c => translate("vocab.weather." + c))) : None)),
            Line("ui.debug.epoch", ("epoch", Named("vocab.epoch.", data.LookEpoch))),
            Line("ui.debug.fps", ("fps", Math.Round(data.Fps).ToString("0", CultureInfo.InvariantCulture))),
            Line("ui.debug.figures", ("count", data.Figures.ToString(CultureInfo.InvariantCulture))),
            Line("ui.debug.biome", ("biome", Named("vocab.biome.", data.Biome))),
        };
        if (data.EpochKeys is { } keys)
        {
            lines.Add(Line("ui.debug.jumps", ("back", keys.Back), ("forward", keys.Forward)));
        }

        if (data.Jobs is { } jobs)
        {
            // A1801 (Marcel W2243): what the AI works on now, and what waits for the night.
            lines.Add(Line("ui.debug.jobs", ("running", jobs.Count(j => !j.Waiting).ToString(CultureInfo.InvariantCulture)), ("waiting", jobs.Count(j => j.Waiting).ToString(CultureInfo.InvariantCulture))));
            foreach (var job in jobs.Take(MaxJobs))
            {
                lines.Add(Line(
                    job.Waiting ? "ui.debug.job_waiting" : "ui.debug.job",
                    ("kind", translate("ui.debug.job_kind." + job.Kind) is var text && text != "ui.debug.job_kind." + job.Kind ? text : job.Kind),
                    ("name", job.Name ?? None),
                    ("minutes", job.Minutes.ToString(CultureInfo.InvariantCulture))));
            }
        }

        if (data.Knowledge is { } knowledge)
        {
            // A1402 (B4a-S7): what the chosen inhabitant believes, and from where - experienced, heard from someone, seen.
            lines.Add(Line("ui.debug.knowledge", ("name", knowledge.Name), ("count", knowledge.Count.ToString(CultureInfo.InvariantCulture))));
            foreach (var belief in knowledge.Beliefs)
            {
                var source = belief.ToldBy is { } by ? Line("ui.debug.source." + belief.Source + ".by", ("name", by)) : translate("ui.debug.source." + belief.Source);
                lines.Add(Line(
                    "ui.debug.belief",
                    ("thing", Named("vocab.object.", belief.TypeId)),
                    ("property", translate("vocab.property." + belief.PropertyId)),
                    ("value", translate(belief.Value ? "ui.debug.yes" : "ui.debug.no")),
                    ("source", source),
                    ("percent", Math.Round(belief.Confidence * 100).ToString("0", CultureInfo.InvariantCulture))));
            }
        }

        return lines;
    }

    /// <summary>A1801: at most this many job lines (the rest is in the count).</summary>
    public const int MaxJobs = 8;

    /// <summary>Biome id of the map cell under a ground point (metres), or null without a map or outside it.</summary>
    public static string? BiomeAt(WorldMapSystem? map, double x, double z) =>
        map?.CellOf(new Position(x, z)) is { } cell ? map.Map.BiomeAt(cell.X, cell.Z) : null;

    /// <summary>
    /// The epoch a debug step leads to: the neighbour of the current one in the order of the data
    /// (content/look/epochs.json), or null at either end and when there is nothing to step from.
    /// </summary>
    /// <param name="epochs">Epoch ids in the order of the data.</param>
    /// <param name="current">Current epoch id.</param>
    /// <param name="step">+1 forward, -1 back.</param>
    public static string? StepEpoch(IReadOnlyList<string> epochs, string? current, int step)
    {
        ArgumentNullException.ThrowIfNull(epochs);
        var index = current is null ? -1 : epochs.ToList().IndexOf(current);
        var next = index + step;
        return index < 0 || step == 0 || next < 0 || next >= epochs.Count ? null : epochs[next];
    }

    /// <summary>
    /// Parts of the lines that still have the form of a text key or an unfilled placeholder (smoke check:
    /// a missing text shows its key).
    /// </summary>
    public static IReadOnlyList<string> RawKeys(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return [.. lines.SelectMany(l => l.Split(' ', ',', '(', ')', ':')).Where(part => TextKeys.LooksLikeKey(part) || part.Contains('{', StringComparison.Ordinal)).Distinct(StringComparer.Ordinal)];
    }

    private static string Short(string? hash) => hash is null ? None : hash.Length > HashLength ? hash[..HashLength] : hash;
}
