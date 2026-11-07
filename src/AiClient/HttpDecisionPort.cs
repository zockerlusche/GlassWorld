using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.AiClient;

/// <summary>
/// Decider port over HTTP to the local AI service (ADR-0006): <c>POST /decide</c> on 127.0.0.1 with the
/// session token in <c>X-GW-Token</c>. Requests run in the background; answers are queued and handed to
/// the core at its next tick (B2487: no fixed time limit). If the service cannot be reached, the port
/// answers itself with a valid, retryable <c>ServiceError</c> (<c>model_unavailable</c>): the question
/// stays open and the inhabitants keep living in routine mode (B3 acceptance A3/A6). The port never makes
/// a decision of its own.
/// </summary>
public sealed class HttpDecisionPort : IDecisionPort, IDisposable
{
    /// <summary>Header that carries the session token.</summary>
    public const string TokenHeader = "X-GW-Token";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly ConcurrentQueue<PortMessage> _ready = new();
    private int _inFlight;

    /// <summary>Creates the port for a running service.</summary>
    /// <param name="baseAddress">Service address, e.g. <c>http://127.0.0.1:51234/</c>.</param>
    /// <param name="token">Session token (from <c>GW_AI_TOKEN</c>), never logged.</param>
    /// <param name="clientTimeout">Safety limit per HTTP call; the service itself answers within the request's deadline.</param>
    /// <param name="handler">Optional message handler (tests).</param>
    public HttpDecisionPort(Uri baseAddress, string token, TimeSpan clientTimeout, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrEmpty(token);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsClient = true;
        _http.BaseAddress = baseAddress;
        _http.Timeout = clientTimeout;
        _http.DefaultRequestHeaders.Add(TokenHeader, token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Requests that are still running.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <inheritdoc/>
    public void Send(string requestId, string requestJson)
    {
        Interlocked.Increment(ref _inFlight);
        _ = Task.Run(() => PostAsync(requestId, requestJson));
    }

    /// <inheritdoc/>
    public IReadOnlyList<PortMessage> Receive(long tick)
    {
        var messages = new List<PortMessage>();
        while (_ready.TryDequeue(out var message))
        {
            messages.Add(message);
        }

        return messages;
    }

    /// <summary>Waits until all running requests have an answer (tests and shutdown).</summary>
    public async Task WhenIdleAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (InFlight > 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    private async Task PostAsync(string requestId, string requestJson)
    {
        try
        {
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(new Uri("decide", UriKind.Relative), content).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            _ready.Enqueue(new PortMessage(requestId, body, IsError: !response.IsSuccessStatusCode));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            var code = ex is HttpRequestException ? ServiceErrorCode.Model_unavailable : ServiceErrorCode.Timeout;
            _ready.Enqueue(new PortMessage(requestId, Unreachable(requestJson, code, ex.GetType().Name), IsError: true));
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>A valid, retryable ServiceError for a request the service did not answer.</summary>
    private static string Unreachable(string requestJson, ServiceErrorCode code, string reason)
    {
        var request = ContractSerializer.Deserialize<DecisionRequest>(requestJson);
        return ContractSerializer.Serialize(new ServiceError
        {
            ContractVersion = request.ContractVersion,
            RequestId = request.RequestId,
            Tick = request.Tick,
            Code = code,
            Message = "AI service not reachable (" + reason + ").",
            Retryable = true,
        });
    }
}
