using System.Text.Json;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Night;

namespace GlassWorld.Presentation.Ui;

/// <summary>One AI job the developer view lists: its kind, whom it is about, since when, and whether it waits for the night.</summary>
/// <param name="RequestId">The job's request id.</param>
/// <param name="Kind">Job kind (invention, image, sound, dialogue, chronicle ...), or "?" when the log keeps only its fingerprint.</param>
/// <param name="InhabitantId">The inhabitant it is about, or null.</param>
/// <param name="Tick">When it was sent (running) or held (waiting).</param>
/// <param name="Waiting">True when it waits for the compute night (held), false when the AI works on it now.</param>
public sealed record AiJob(string RequestId, string Kind, string? InhabitantId, long Tick, bool Waiting);

/// <summary>
/// A1801 (Marcel W2243): what the AI is working on - from the core's job log (a request without a final state yet) and the
/// night queue (held jobs). Read only; the core is not asked to change anything.
/// </summary>
public static class AiJobs
{
    /// <summary>The open jobs: running ones (newest first), then the waiting ones (in the order they came).</summary>
    public static IReadOnlyList<AiJob> Open(IReadOnlyList<DecisionLogEntry> log, IEnumerable<HeldJob> held)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(held);
        var waiting = held.Select(h => new AiJob(h.RequestId, h.Kind, h.InhabitantId, h.Tick, true)).ToList();
        var heldIds = waiting.Select(w => w.RequestId).ToHashSet(StringComparer.Ordinal);
        var finished = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in log)
        {
            if (entry.Kind is DecisionLogKind.JobResult or DecisionLogKind.JobError or DecisionLogKind.JobDiscarded)
            {
                finished.Add(entry.RequestId);
            }
        }

        var running = new List<AiJob>();
        for (var i = log.Count - 1; i >= 0; i--)
        {
            var entry = log[i];
            if (entry.Kind == DecisionLogKind.JobRequest && !finished.Contains(entry.RequestId) && !heldIds.Contains(entry.RequestId)
                && running.All(r => r.RequestId != entry.RequestId))
            {
                var (kind, inhabitant) = KindAndInhabitant(entry.Json);
                running.Add(new AiJob(entry.RequestId, kind, inhabitant, entry.Tick, false));
            }
        }

        return [.. running, .. waiting];
    }

    /// <summary>The kind and the inhabitant of a job request (generation-job), or ("?", null) when only a fingerprint is kept.</summary>
    public static (string Kind, string? InhabitantId) KindAndInhabitant(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var kind = root.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "?" : "?";
            var who = root.TryGetProperty("inhabitant", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
            return (kind, who);
        }
        catch (JsonException)
        {
            return ("?", null);
        }
    }
}
