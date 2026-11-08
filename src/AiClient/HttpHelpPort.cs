using System.Net.Http.Headers;
using System.Text;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;

namespace GlassWorld.AiClient;

/// <summary>An answer of the help endpoint: the suggestions, or why there are none.</summary>
/// <param name="Response">Valid response for exactly this request; null on any problem.</param>
/// <param name="Problem">Why there is no response (for the log, never shown); null when there is one.</param>
public sealed record HelpAnswer(HelpSuggestionsResponse? Response, string? Problem);

/// <summary>
/// Asks the AI service for help suggestions (<c>POST /help-suggestions</c>, B6 R8/R10, F5238). Only
/// proposals for the player: nothing reaches the world unless the player places a gift, and the core
/// checks that command. Every answer is checked against the contract and must echo the request; any
/// problem gives no answer, and the game shows the suggestions from the data instead (calm fallback,
/// B3 A3).
/// </summary>
public sealed class HttpHelpPort : IDisposable
{
    private readonly HttpClient _http;

    /// <summary>Creates the port for a running service.</summary>
    /// <param name="baseAddress">Service address, <c>http://127.0.0.1:&lt;port&gt;/</c>.</param>
    /// <param name="token">Session token, never logged.</param>
    /// <param name="clientTimeout">Safety limit per call; the service answers within the request's deadline.</param>
    /// <param name="handler">Optional message handler (tests).</param>
    public HttpHelpPort(Uri baseAddress, string token, TimeSpan clientTimeout, HttpMessageHandler? handler = null)
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

    /// <summary>Port for a started service (deadline plus the same safety margin as decisions).</summary>
    public static HttpHelpPort ForService(AiServiceProcess service, int deadlineMs)
    {
        ArgumentNullException.ThrowIfNull(service);
        return new HttpHelpPort(service.BaseAddress, service.Token, TimeSpan.FromMilliseconds(deadlineMs) + HttpDecisionPort.SafetyMargin);
    }

    /// <summary>Asks for suggestions; never throws for service problems (they give an answer without response).</summary>
    public async Task<HelpAnswer> SuggestAsync(HelpSuggestionsRequest request, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var content = new StringContent(ContractSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(new Uri("help-suggestions", UriKind.Relative), content, cancellation).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new HelpAnswer(null, $"service answered {(int)response.StatusCode}");
            }

            return Check(request, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or ObjectDisposedException)
        {
            return new HelpAnswer(null, "AI service not reachable (" + ex.GetType().Name + ")");
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();

    /// <summary>A body counts only if it matches the contract and answers exactly this request.</summary>
    internal static HelpAnswer Check(HelpSuggestionsRequest request, string body)
    {
        var problems = ContractValidator.Validate("help-suggestions-response", body);

        if (problems.Count > 0)
        {
            return new HelpAnswer(null, "invalid response: " + string.Join("; ", problems.Take(3)));
        }

        var response = ContractSerializer.Deserialize<HelpSuggestionsResponse>(body);
        return response.RequestId == request.RequestId && response.Tick == request.Tick
            ? new HelpAnswer(response, null)
            : new HelpAnswer(null, "response for another request");
    }
}
