using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Player;

namespace GlassWorld.AiClient;

/// <summary>
/// Conversations through the local AI service (B6 R13, A1342): POST /conversation, answers queued for
/// the core's next tick like <see cref="HttpDecisionPort"/>. The core checks every answer; any problem
/// (service off, timeout, HTTP error) becomes an error message, which the core turns into a calm "no
/// answer" with the favour back (R16).
/// </summary>
public sealed class HttpConversationPort : IConversationPort, IDisposable
{
    private readonly HttpClient _http;
    private readonly ConcurrentQueue<PortMessage> _ready = new();
    private int _inFlight;

    /// <summary>Creates the port for the service at <paramref name="baseAddress"/> (127.0.0.1 only, ADR-0006).</summary>
    public HttpConversationPort(Uri baseAddress, string token, TimeSpan clientTimeout, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrEmpty(token);
        if (baseAddress.Host != "127.0.0.1")
        {
            throw new ArgumentException("The AI service address must be http://127.0.0.1:<port>/.", nameof(baseAddress));
        }

        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = baseAddress;
        _http.Timeout = clientTimeout;
        _http.DefaultRequestHeaders.Add(HttpDecisionPort.TokenHeader, token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Requests still on their way.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>The port for a started service; the client waits the request deadline plus a margin.</summary>
    public static HttpConversationPort ForService(AiServiceProcess service, int deadlineMs)
    {
        ArgumentNullException.ThrowIfNull(service);
        return new HttpConversationPort(service.BaseAddress, service.Token, TimeSpan.FromMilliseconds(deadlineMs) + HttpDecisionPort.SafetyMargin);
    }

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

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();

    private async Task PostAsync(string requestId, string requestJson)
    {
        try
        {
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(new Uri("conversation", UriKind.Relative), content).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            _ready.Enqueue(new PortMessage(requestId, body, IsError: !response.IsSuccessStatusCode));
        }
        catch (ObjectDisposedException)
        {
            // The game is shutting down; nobody reads the answer any more.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _ready.Enqueue(new PortMessage(requestId, "{\"code\":\"model_unavailable\",\"message\":\"" + ex.GetType().Name + "\"}", IsError: true));
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
