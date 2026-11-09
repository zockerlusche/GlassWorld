using System.Text;
using System.Text.Json;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.Simulation.Headless;

/// <summary>
/// Writes the core's decision log and job log as JSON lines (<c>--log-json</c>), one line per log entry,
/// as the entries appear (a run that is stopped keeps what it logged). Each line:
/// <c>log</c> (decision|job), <c>kind</c> (request, response, error, discarded, command, job_request,
/// job_result, job_error, job_discarded), <c>request_id</c>, <c>tick</c> (sent, or took effect),
/// <c>note</c>, <c>request</c> (the request JSON, for requests) or <c>response</c> (the received JSON,
/// for outcomes), and <c>meta</c> for outcomes: <c>request_tick</c>, <c>wait_ticks</c>, the request's
/// <c>seed</c>, and source/model/prompt_version/latency_ms/attempts from the answer's meta or provenance.
/// </summary>
internal sealed class JsonLogWriter : IDisposable
{
    private static readonly string[] MetaFields = ["source", "model", "prompt_version", "seed", "latency_ms", "attempts"];

    private readonly StreamWriter _writer;
    private readonly Dictionary<string, (long Tick, long? Seed)> _requests = new(StringComparer.Ordinal);
    private int _decisionsWritten;
    private int _jobsWritten;

    /// <summary>Creates (or replaces) the file.</summary>
    public JsonLogWriter(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder)
        {
            Directory.CreateDirectory(folder);
        }

        _writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" };
    }

    /// <summary>Writes the entries added to the two logs since the last call.</summary>
    public void WriteNew(DecisionLog decisions, DecisionLog jobs)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(jobs);
        _decisionsWritten = WriteFrom("decision", decisions, _decisionsWritten);
        _jobsWritten = WriteFrom("job", jobs, _jobsWritten);
    }

    /// <summary>Snake-case name of a log kind.</summary>
    public static string KindName(DecisionLogKind kind) => kind switch
    {
        DecisionLogKind.Request => "request",
        DecisionLogKind.Response => "response",
        DecisionLogKind.Error => "error",
        DecisionLogKind.Discarded => "discarded",
        DecisionLogKind.Command => "command",
        DecisionLogKind.JobRequest => "job_request",
        DecisionLogKind.JobResult => "job_result",
        DecisionLogKind.JobError => "job_error",
        DecisionLogKind.JobDiscarded => "job_discarded",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>One JSON line for an entry (no trailing newline).</summary>
    public string Line(string log, DecisionLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var isRequest = entry.Kind is DecisionLogKind.Request or DecisionLogKind.JobRequest;
        if (isRequest)
        {
            _requests[entry.RequestId] = (entry.Tick, SeedOf(entry.Json));
        }

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("log", log);
            json.WriteString("kind", KindName(entry.Kind));
            json.WriteString("request_id", entry.RequestId);
            json.WriteNumber("tick", entry.Tick);
            if (entry.Note is null)
            {
                json.WriteNull("note");
            }
            else
            {
                json.WriteString("note", entry.Note);
            }

            json.WritePropertyName(isRequest || entry.Kind == DecisionLogKind.Command ? "request" : "response");
            WriteRaw(json, entry.Json);
            if (!isRequest && entry.Kind != DecisionLogKind.Command)
            {
                WriteMeta(json, entry);
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <inheritdoc/>
    public void Dispose() => _writer.Dispose();

    private int WriteFrom(string log, DecisionLog source, int written)
    {
        var entries = source.Entries;
        for (var i = written; i < entries.Count; i++)
        {
            _writer.WriteLine(Line(log, entries[i]));
        }

        if (entries.Count > written)
        {
            _writer.Flush();
        }

        return entries.Count;
    }

    private void WriteMeta(Utf8JsonWriter json, DecisionLogEntry entry)
    {
        json.WriteStartObject("meta");
        if (_requests.TryGetValue(entry.RequestId, out var request))
        {
            json.WriteNumber("request_tick", request.Tick);
            json.WriteNumber("wait_ticks", entry.Tick - request.Tick);
            if (request.Seed is { } seed)
            {
                json.WriteNumber("request_seed", seed);
            }
        }

        if (TryParse(entry.Json) is { } document)
        {
            using (document)
            {
                var root = document.RootElement;
                foreach (var name in new[] { "meta", "provenance" })
                {
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var meta) && meta.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var field in MetaFields)
                        {
                            if (meta.TryGetProperty(field, out var value))
                            {
                                json.WritePropertyName(field);
                                value.WriteTo(json);
                            }
                        }
                    }
                }
            }
        }

        json.WriteEndObject();
    }

    private static void WriteRaw(Utf8JsonWriter json, string raw)
    {
        if (TryParse(raw) is { } document)
        {
            using (document)
            {
                document.RootElement.WriteTo(json);
            }

            return;
        }

        // Not JSON (a contract violation the core logged as received): kept as a string.
        json.WriteStringValue(raw);
    }

    private static long? SeedOf(string requestJson)
    {
        using var document = TryParse(requestJson);
        return document is not null
            && document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("seed", out var seed)
            && seed.TryGetInt64(out var value) ? value : null;
    }

    private static JsonDocument? TryParse(string raw)
    {
        try
        {
            return JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
