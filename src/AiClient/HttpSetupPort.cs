using System.Net.Http.Headers;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;

namespace GlassWorld.AiClient;

/// <summary>An answer of the self-check: the checks per AI, or why there are none.</summary>
/// <param name="Check">Valid self-check; null on any problem.</param>
/// <param name="Problem">Why there is none (for the log, never shown as such); null when there is one.</param>
public sealed record SelfCheckAnswer(SelfCheck? Check, string? Problem);

/// <summary>An answer of the diagnostics endpoint: the report, or why there is none.</summary>
/// <param name="Report">Valid report (file name in the service's log folder and its text); null on any problem.</param>
/// <param name="Problem">Why there is none (for the log); null when there is one.</param>
public sealed record DiagnosticsAnswer(DiagnosticsReport? Report, string? Problem);

/// <summary>
/// The AI service's setup endpoints for the player (W1703, Bogen 35 R11): <c>GET /setup/check</c> (per AI a
/// state with text and fix hint as keys) and <c>POST /diagnostics</c> (a report without tokens or user paths).
/// Every answer is checked against its contract; any problem gives no answer and never throws, so the game
/// keeps running calmly (W1703 rule 3).
/// </summary>
public sealed class HttpSetupPort : IDisposable
{
    private readonly HttpClient _http;

    /// <summary>Creates the port for a running service.</summary>
    /// <param name="baseAddress">Service address, <c>http://127.0.0.1:&lt;port&gt;/</c>.</param>
    /// <param name="token">Session token, never logged.</param>
    /// <param name="clientTimeout">Safety limit per call.</param>
    /// <param name="handler">Optional message handler (tests).</param>
    public HttpSetupPort(Uri baseAddress, string token, TimeSpan clientTimeout, HttpMessageHandler? handler = null)
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

    /// <summary>The service answers both at once; the report gathers some status first, so allow a little longer.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Port for a started service.</summary>
    public static HttpSetupPort ForService(AiServiceProcess service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return new HttpSetupPort(service.BaseAddress, service.Token, Timeout);
    }

    /// <summary>Asks for the self-check; never throws for service problems.</summary>
    public async Task<SelfCheckAnswer> CheckAsync(CancellationToken cancellation = default)
    {
        var (body, problem) = await SendAsync(HttpMethod.Get, "setup/check", cancellation).ConfigureAwait(false);
        if (body is null)
        {
            return new SelfCheckAnswer(null, problem);
        }

        var problems = ContractValidator.Validate("self-check", body);
        return problems.Count > 0
            ? new SelfCheckAnswer(null, "invalid self-check: " + string.Join("; ", problems.Take(3)))
            : new SelfCheckAnswer(ContractSerializer.Deserialize<SelfCheck>(body), null);
    }

    /// <summary>Asks for a diagnostics report; never throws for service problems.</summary>
    public async Task<DiagnosticsAnswer> DiagnosticsAsync(CancellationToken cancellation = default)
    {
        var (body, problem) = await SendAsync(HttpMethod.Post, "diagnostics", cancellation).ConfigureAwait(false);
        if (body is null)
        {
            return new DiagnosticsAnswer(null, problem);
        }

        var problems = ContractValidator.Validate("diagnostics-report", body);
        return problems.Count > 0
            ? new DiagnosticsAnswer(null, "invalid report: " + string.Join("; ", problems.Take(3)))
            : new DiagnosticsAnswer(ContractSerializer.Deserialize<DiagnosticsReport>(body), null);
    }

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();

    private async Task<(string? Body, string? Problem)> SendAsync(HttpMethod method, string path, CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? (body, null) : (null, $"service answered {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or ObjectDisposedException)
        {
            return (null, "AI service not reachable (" + ex.GetType().Name + ")");
        }
    }
}
