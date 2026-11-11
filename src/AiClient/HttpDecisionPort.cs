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
        if (baseAddress.Host != "127.0.0.1")
        {
            // The service checks the Host header (ADR-0006); 'localhost' would always get 403.
            throw new ArgumentException("The AI service address must be http://127.0.0.1:<port>/.", nameof(baseAddress));
        }

        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsClient = true;
        _http.BaseAddress = baseAddress;
        _http.Timeout = clientTimeout;
        _http.DefaultRequestHeaders.Add(TokenHeader, token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Port for a started service. The client timeout is the request deadline plus a safety margin, so the
    /// client never gives up while the service is still working on an answer.
    /// </summary>
    public static HttpDecisionPort ForService(AiServiceProcess service, int deadlineMs)
    {
        ArgumentNullException.ThrowIfNull(service);
        return new HttpDecisionPort(service.BaseAddress, service.Token, TimeSpan.FromMilliseconds(deadlineMs) + SafetyMargin);
    }

    /// <summary>Added to the request deadline for the HTTP client timeout.</summary>
    public static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(30);

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
    /// <returns>True if the port became idle; false if the timeout ran out first (callers must not
    /// read on as if every answer had arrived).</returns>
    public async Task<bool> WhenIdleAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (InFlight > 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        return InFlight == 0;
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
            if (response.IsSuccessStatusCode)
            {
                _ready.Enqueue(new PortMessage(requestId, body, IsError: false));
            }
            else
            {
                _ready.Enqueue(new PortMessage(requestId, AsServiceError(requestJson, body, (int)response.StatusCode), IsError: true));
            }
        }
        catch (ObjectDisposedException)
        {
            // The game is shutting down; nobody reads the answer any more.
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
    private static string Unreachable(string requestJson, ServiceErrorCode code, string reason) =>
        Synthesize(requestJson, code, "AI service not reachable (" + reason + ").", retryable: true);

    /// <summary>
    /// The error body as sent if it is a valid ServiceError; otherwise (e.g. a proxy or crash page) a
    /// synthesized one: 5xx is retryable (internal), 4xx is not (bad_request).
    /// </summary>
    private static string AsServiceError(string requestJson, string body, int status)
    {
        if (ContractValidator.Validate("service-error", body).Count == 0)
        {
            return body;
        }

        return status >= 500
            ? Synthesize(requestJson, ServiceErrorCode.Internal, "AI service answered HTTP " + status + " without a valid error body.", retryable: true)
            : Synthesize(requestJson, ServiceErrorCode.Bad_request, "AI service answered HTTP " + status + " without a valid error body.", retryable: false);
    }

    private static string Synthesize(string requestJson, ServiceErrorCode code, string message, bool retryable) =>
        ServiceErrors.For(requestJson, code, message, retryable);
}
