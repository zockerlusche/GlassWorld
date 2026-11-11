using System.Text.Json;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;

namespace GlassWorld.AiClient;

/// <summary>
/// The <c>ServiceError</c> a port writes itself when the service gave no usable one (not reachable, a
/// crash page, an invalid job status). It names the request it is about, read from the request's own
/// JSON - leniently: the running game does not validate what it sends (ADR-0009), and a port must be able
/// to report a failure also for a request that is not valid, or the question would wait for an answer
/// that never comes.
/// </summary>
internal static class ServiceErrors
{
    /// <summary>Stands in for a request id that cannot be read; the port hands the real one on beside the body.</summary>
    public const string UnreadableRequestId = "00000000-0000-4000-8000-000000000000";

    /// <summary>A ServiceError for the request or job in <paramref name="requestJson"/> (contract version, request id and tick from it where readable).</summary>
    public static string For(string requestJson, ServiceErrorCode code, string message, bool retryable)
    {
        var (version, requestId, tick) = Read(requestJson);
        var error = new ServiceError
        {
            ContractVersion = version,
            RequestId = requestId,
            Tick = tick,
            Code = code,
            Message = message,
            Retryable = retryable,
        };
        try
        {
            return ContractSerializer.Serialize(error);
        }
        catch (ContractViolationException)
        {
            // What the request called itself does not fit a ServiceError: report the failure all the same.
            error.ContractVersion = ContractInfo.Version;
            error.RequestId = UnreadableRequestId;
            error.Tick = 0;
            return ContractSerializer.Serialize(error);
        }
    }

    private static (string Version, string RequestId, long Tick) Read(string requestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(requestJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (ContractInfo.Version, UnreadableRequestId, 0);
            }

            return (
                root.TryGetProperty("contract_version", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString()! : ContractInfo.Version,
                root.TryGetProperty("request_id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : UnreadableRequestId,
                root.TryGetProperty("tick", out var tick) && tick.ValueKind == JsonValueKind.Number && tick.TryGetInt64(out var value) ? value : 0);
        }
        catch (JsonException)
        {
            return (ContractInfo.Version, UnreadableRequestId, 0);
        }
    }
}
