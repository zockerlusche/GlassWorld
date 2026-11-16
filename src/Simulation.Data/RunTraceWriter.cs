using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Data;

/// <summary>
/// The technical run trace (A1719, B2767/B2768): one JSON line per thing that happened, one file per game day next to
/// the save (<c>trace/day-NNNN.jsonl</c>), so the TL and the agents can follow a game afterwards: where was obj-X when,
/// what did inh-Y do and why. It reads along only - no world state, no hash, nothing the core decides on - and writes
/// outside the tick's logic (buffered, flushed every game hour). Lines (field <c>t</c>):
/// <list type="bullet">
/// <item><c>event</c>: every core event - kind, actor, action, target id and type, detail, outcome, the shown text, the
/// actor's position, what it holds and wears.</item>
/// <item><c>ai</c>: every decider and job log entry - request_id, log kind, note; for a decision request the offered
/// options, for a response the chosen option, rationale and thought, for jobs the answer JSON.</item>
/// <item><c>snapshot</c>: every <c>snapshot_every_hours</c> game hours all inhabitants (position, activity, held, worn,
/// needs, life phase) and all things that are held or portable (id, type, position, held by).</item>
/// <item>Anything a caller adds through <see cref="Note"/> (the game: sounds with their source).</item>
/// </list>
/// Days before yesterday are packed to <c>.jsonl.gz</c>. A world loaded again appends to its day files.
/// </summary>
public sealed class RunTraceWriter : IDisposable
{
    private const string Portable = "portable";
    private readonly SimulationHost _host;
    private readonly TraceSettings _settings;
    private readonly string _folder;
    private readonly long _ticksPerDay;
    private readonly long _ticksPerHour;
    private readonly ArrayBufferWriter _buffer = new();
    private StreamWriter? _writer;
    private long _day = -1;
    private int _decisionsWritten;
    private int _jobsWritten;
    private long _lastSnapshotHour = -1;
    private bool _disposed;
    private Task _packing = Task.CompletedTask;

    private RunTraceWriter(SimulationHost host, TraceSettings settings, string folder)
    {
        _host = host;
        _settings = settings;
        _folder = folder;
        _ticksPerDay = Math.Max(1, host.World.Calendar.TicksPerDay);
        _ticksPerHour = Math.Max(1, _ticksPerDay / 24);
        Directory.CreateDirectory(folder);

        // Only what happens from now on: a loaded world's logs already went into its earlier files.
        _decisionsWritten = host.Decisions.Log.Entries.Count;
        _jobsWritten = host.Inventions.Log.Entries.Count;
        host.Inhabitants.Reported += OnEvent;
        host.World.BoundaryReached += OnBoundary;
    }

    /// <summary>
    /// Raised once when writing failed (full disk, a file held by another program): the trace switched itself off,
    /// the world goes on (GW3 #25998, B2712). The game shows it as a warning.
    /// </summary>
    public event Action<Exception>? Failed;

    /// <summary>Why the trace switched itself off, or null while it writes.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>The folder the day files go to.</summary>
    public string Folder => _folder;

    /// <summary>
    /// Starts the trace for <paramref name="host"/> in <paramref name="worldFolder"/>/folder when the data switch it on
    /// (<c>content/trace/settings.json</c>), else null. <paramref name="force"/> writes it whatever the data say (a night
    /// run's <c>--run-trace</c>).
    /// </summary>
    public static RunTraceWriter? Start(SimulationHost host, string worldFolder, string contentDirectory, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrEmpty(worldFolder);
        var settings = LoadSettings(contentDirectory);
        if (settings is null || (!settings.Enabled && !force))
        {
            return null;
        }

        return new RunTraceWriter(host, settings, Path.Combine(worldFolder, settings.Folder));
    }

    /// <summary>The data of the trace, or null without the file.</summary>
    public static TraceSettings? LoadSettings(string contentDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentDirectory);
        var path = Path.Combine(contentDirectory, "trace", "settings.json");
        return File.Exists(path) ? ContractSerializer.Deserialize<TraceSettings>(File.ReadAllText(path)) : null;
    }

    /// <summary>
    /// Adds a line of the caller's own (kind <paramref name="kind"/>, e.g. 'sound') at <paramref name="tick"/> with the
    /// given language-neutral fields.
    /// </summary>
    public void Note(long tick, string kind, IReadOnlyDictionary<string, string?> fields)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(fields);

        // The game calls this from its own code (the sound director): a write error switches the trace off there too.
        Guarded(() => Write(tick, kind, json =>
        {
            foreach (var (name, value) in fields.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                WriteOptional(json, name, value);
            }
        }));
    }

    /// <summary>Writes what the logs gained since the last call and flushes (call it after a step or before closing).</summary>
    public void Flush()
    {
        if (_disposed)
        {
            return;
        }

        Guarded(() =>
        {
            WriteLogs();
            _writer?.Flush();
        });
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Flush();
        _host.Inhabitants.Reported -= OnEvent;
        _host.World.BoundaryReached -= OnBoundary;
        try
        {
            _writer?.Dispose();
            _packing.Wait();
        }
        catch (Exception e) when (IsWriteFailure(e))
        {
            // Closing a trace that could not write: nothing more to lose.
        }

        _writer = null;
        _disposed = true;
    }

    // The handlers run inside World.Step: a write error must never reach it (GW3 #25998). The first one switches the
    // trace off for the rest of the run and is reported once.
    private void Guarded(Action write)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            write();
        }
        catch (Exception e) when (IsWriteFailure(e))
        {
            Failure = e;
            _host.Inhabitants.Reported -= OnEvent;
            _host.World.BoundaryReached -= OnBoundary;
            try
            {
                _writer?.Dispose();
            }
            catch (Exception again) when (IsWriteFailure(again))
            {
                // The file is gone with the disk; the trace is off anyway.
            }

            _writer = null;
            _disposed = true;
            Failed?.Invoke(e);
        }
    }

    private static bool IsWriteFailure(Exception e) =>
        e is IOException or UnauthorizedAccessException or System.Security.SecurityException or AggregateException;

    private void OnEvent(WorldEvent e) => Guarded(() => WriteEvent(e));

    private void WriteEvent(WorldEvent e)
    {
        // The AI lines of this tick come first: the request that led to an event is older than the event.
        WriteLogs();
        Write(e.Tick, "event", json =>
        {
            json.WriteString("kind", e.Kind);
            json.WriteString("who", e.InhabitantId);
            WriteOptional(json, "action", e.Action);
            WriteOptional(json, "target", e.TargetId);
            WriteOptional(json, "target_type", e.TargetType);
            WriteOptional(json, "detail", e.Detail);
            WriteOptional(json, "outcome", e.Outcome);
            WriteOptional(json, "text", e.Text);
            WriteOptional(json, "conversation", e.Conversation);
            if (e.Turn is { } turn && e.Turns is { } turns)
            {
                json.WriteNumber("turn", turn);
                json.WriteNumber("turns", turns);
            }

            // Where the target thing is (A1373, Marcel's stone cup 15a): a built thing stands at its site, which can lie
            // metres from where its builder finished - the line says both.
            if (e.TargetId is { } targetId && _host.Objects.TryGet(targetId, out var target))
            {
                WritePosition(json, "target_at", target.Position);
            }

            if (_host.Inhabitants.TryGet(e.InhabitantId, out var who))
            {
                WritePosition(json, "at", who.Position);
                WriteOptional(json, "held", who.Held);
                WriteOptional(json, "worn", who.Worn);
            }
        });
    }

    private void OnBoundary(World world, TimeBoundary boundary)
    {
        if (boundary == TimeBoundary.Hour)
        {
            Guarded(() => WriteHour(world));
        }
    }

    private void WriteHour(World world)
    {
        WriteLogs();
        var hour = world.Tick / _ticksPerHour;
        if (_lastSnapshotHour < 0 || hour - _lastSnapshotHour >= Math.Max(1, _settings.SnapshotEveryHours))
        {
            _lastSnapshotHour = hour;
            WriteSnapshot(world.Tick);
        }

        _writer?.Flush();
    }

    private void WriteSnapshot(long tick)
    {
        Write(tick, "snapshot", json =>
        {
            json.WriteStartArray("inhabitants");
            foreach (var who in _host.Inhabitants.All)
            {
                json.WriteStartObject();
                json.WriteString("id", who.Id);
                WriteOptional(json, "name", who.Name);
                WritePosition(json, "at", who.Position);
                WriteOptional(json, "action", who.Activity?.Action);
                WriteOptional(json, "target", who.Activity?.TargetId);
                WriteOptional(json, "held", who.Held);
                WriteOptional(json, "worn", who.Worn);
                WriteOptional(json, "life_stage", who.LifeStage);
                json.WriteStartObject("needs");
                foreach (var (need, value) in who.Needs)
                {
                    json.WriteNumber(need, Math.Round(value, 3));
                }

                json.WriteEndObject();

                // A1808 measuring net (review GW4 #540): moods, traits and the will to live make invariants 5, 13, 18
                // and 19 measurable on the trace (feelings and their decay, the character fixed after birth, the will).
                json.WriteStartObject("moods");
                foreach (var (mood, value) in who.Moods)
                {
                    json.WriteNumber(mood, Math.Round(value, 3));
                }

                json.WriteEndObject();
                json.WriteStartObject("traits");
                foreach (var (trait, value) in who.Traits)
                {
                    json.WriteNumber(trait, Math.Round(value, 3));
                }

                json.WriteEndObject();
                json.WriteNumber("will_to_live", Math.Round(who.WillToLive, 3));
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartArray("things");
            foreach (var thing in _host.Objects.All)
            {
                var heldBy = _host.Objects.HeldBy(thing.Id);
                if (heldBy is null && !(_host.Types.Contains(thing.TypeId) && _host.Types.HasProperty(thing.TypeId, Portable)))
                {
                    continue;
                }

                json.WriteStartObject();
                json.WriteString("id", thing.Id);
                json.WriteString("type", thing.TypeId);
                WritePosition(json, "at", thing.Position);
                WriteOptional(json, "held_by", heldBy);
                json.WriteEndObject();
            }

            json.WriteEndArray();
        });
    }

    private void WriteLogs()
    {
        _decisionsWritten = WriteLog("decision", _host.Decisions.Log, _decisionsWritten);
        _jobsWritten = WriteLog("job", _host.Inventions.Log, _jobsWritten);
    }

    private int WriteLog(string log, DecisionLog source, int written)
    {
        var entries = source.Entries;
        for (var i = written; i < entries.Count; i++)
        {
            var entry = entries[i];
            Write(entry.Tick, "ai", json =>
            {
                json.WriteString("log", log);
                json.WriteString("kind", entry.Kind.ToString());
                json.WriteString("request_id", entry.RequestId);
                WriteOptional(json, "note", entry.Note);
                WriteEntry(json, log, entry);
            });
        }

        return entries.Count;
    }

    // A decision request: who asks and the offered options (the whole context is in the save's decision log).
    // A decision response: the choice and why. Jobs: the request's kind and the answer as it came.
    private static void WriteEntry(Utf8JsonWriter json, string log, DecisionLogEntry entry)
    {
        JsonDocument? document;
        try
        {
            document = JsonDocument.Parse(entry.Json);
        }
        catch (JsonException)
        {
            json.WriteString("raw", entry.Json.Length > 2000 ? entry.Json[..2000] : entry.Json);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (log == "decision" && entry.Kind == DecisionLogKind.Request)
            {
                if (root.TryGetProperty("inhabitant", out var who) && who.ValueKind == JsonValueKind.Object && who.TryGetProperty("id", out var id))
                {
                    json.WritePropertyName("who");
                    id.WriteTo(json);
                }

                Copy(json, root, "occasion", "options");
            }
            else if (log == "decision")
            {
                Copy(json, root, "option_id", "rationale", "thought", "expression", "name_text");
            }
            else if (entry.Kind == DecisionLogKind.JobRequest)
            {
                Copy(json, root, "kind", "inhabitant", "object", "payload");
            }
            else
            {
                json.WritePropertyName("answer");
                root.WriteTo(json);
            }
        }
    }

    private static void Copy(Utf8JsonWriter json, JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
            {
                json.WritePropertyName(name);
                value.WriteTo(json);
            }
        }
    }

    private void Write(long tick, string type, Action<Utf8JsonWriter> body)
    {
        if (_disposed)
        {
            return;
        }

        var writer = WriterFor(tick);
        _buffer.Clear();
        using (var json = new Utf8JsonWriter(_buffer.Stream))
        {
            json.WriteStartObject();
            json.WriteString("t", type);
            json.WriteNumber("tick", tick);
            json.WriteString("clock", Clock(tick));
            body(json);
            json.WriteEndObject();
        }

        writer.WriteLine(Encoding.UTF8.GetString(_buffer.Stream.GetBuffer(), 0, (int)_buffer.Stream.Length));
    }

    private StreamWriter WriterFor(long tick)
    {
        // A line of an older tick (an AI answer logged late) goes into the open day's file: a day is never reopened.
        var day = Math.Max((tick / _ticksPerDay) + 1, _day);
        if (_writer is not null && day == _day)
        {
            return _writer;
        }

        _writer?.Dispose();
        _day = day;

        // Shared for reading: the query tool may read today's file while the game writes it.
        var stream = new FileStream(DayPath(day), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n" };
        // Packing reads only finished files: off the game thread, so midnight shows no hitch (GW3 #25998).
        // Chained, never skipped: a slow pack (or a thread pool that starts late) only delays the next one.
        if (_settings.PackOldDays)
        {
            var keep = day - 1;
            _packing = _packing.ContinueWith(_ => PackBefore(keep), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
        return _writer;
    }

    private string DayPath(long day) => Path.Combine(_folder, string.Create(CultureInfo.InvariantCulture, $"day-{day:0000}.jsonl"));

    // Days before 'keep' are packed (B2767: old days packed); a failure leaves the plain file.
    private void PackBefore(long keep)
    {
        foreach (var path in Directory.EnumerateFiles(_folder, "day-*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!long.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var day) || day >= keep)
            {
                continue;
            }

            try
            {
                var packed = path + ".gz";
                using (var source = File.OpenRead(path))
                using (var target = File.Create(packed))
                using (var zip = new GZipStream(target, CompressionLevel.Optimal))
                {
                    source.CopyTo(zip);
                }

                File.Delete(path);
            }
            catch (Exception e) when (IsWriteFailure(e))
            {
                // Kept plain; the next day tries again.
            }
        }
    }

    private string Clock(long tick)
    {
        var day = (tick / _ticksPerDay) + 1;
        var minutes = tick % _ticksPerDay * 24 * 60 / _ticksPerDay;
        return string.Create(CultureInfo.InvariantCulture, $"{day}/{minutes / 60:00}:{minutes % 60:00}");
    }

    private static void WritePosition(Utf8JsonWriter json, string name, Position at)
    {
        json.WriteStartArray(name);
        json.WriteNumberValue(Math.Round(at.X, 2));
        json.WriteNumberValue(Math.Round(at.Z, 2));
        json.WriteEndArray();
    }

    private static void WriteOptional(Utf8JsonWriter json, string name, string? value)
    {
        if (value is not null)
        {
            json.WriteString(name, value);
        }
    }

    // One reusable buffer for the lines (a game writes many thousand a day).
    private sealed class ArrayBufferWriter
    {
        public MemoryStream Stream { get; } = new();

        public void Clear() => Stream.SetLength(0);
    }
}
