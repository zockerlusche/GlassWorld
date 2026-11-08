using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.AiClient;

/// <summary>
/// Job port over HTTP to the local AI service (B7 job envelope, ADR-0006): <c>POST /jobs</c>, then
/// <c>GET /jobs/{id}</c> until the job is done or failed. Only final states reach the core; queued,
/// running and deferred jobs (e.g. sound in the compute night) are polled on. Errors become valid
/// <c>ServiceError</c>s: unreachable service and lost jobs are retryable (the core sends a new attempt
/// with a new id, B2487). The port never makes up a result.
/// </summary>
public sealed class HttpJobPort : IJobPort, IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _maxPollInterval;
    private readonly ConcurrentQueue<PortMessage> _ready = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _phaseLock = new();
    private string? _phase;
    private long _phaseTick;
    private int _inFlight;

    /// <summary>Creates the port for a running service.</summary>
    /// <param name="baseAddress">Service address, e.g. <c>http://127.0.0.1:51234/</c>.</param>
    /// <param name="token">Session token (from <c>GW_AI_TOKEN</c>), never logged.</param>
    /// <param name="clientTimeout">Safety limit per HTTP call (submitting and polling are short calls).</param>
    /// <param name="pollInterval">Pause between two status queries of the same job.</param>
    /// <param name="handler">Optional message handler (tests).</param>
    /// <param name="maxPollInterval">Longest wait between polls while a job is queued or deferred (default 30 s).</param>
    public HttpJobPort(Uri baseAddress, string token, TimeSpan clientTimeout, TimeSpan pollInterval, HttpMessageHandler? handler = null, TimeSpan? maxPollInterval = null)
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
        _pollInterval = pollInterval;
        _maxPollInterval = maxPollInterval ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>Port for a started service (polls every second).</summary>
    public static HttpJobPort ForService(AiServiceProcess service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return new HttpJobPort(service.BaseAddress, service.Token, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
    }

    /// <summary>Jobs submitted and not yet final.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <inheritdoc/>
    public void Submit(string requestId, string jobJson)
    {
        Interlocked.Increment(ref _inFlight);
        _ = Task.Run(() => RunAsync(requestId, jobJson, _stop.Token));
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

    /// <summary>Waits until every submitted job is final (tests and shutdown).</summary>
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
        _stop.Cancel();
        _stop.Dispose();
        _http.Dispose();
    }

    private async Task RunAsync(string requestId, string jobJson, CancellationToken stop)
    {
        try
        {
            using (var content = new StringContent(jobJson, Encoding.UTF8, "application/json"))
            using (var submitted = await _http.PostAsync(new Uri("jobs", UriKind.Relative), content, stop).ConfigureAwait(false))
            {
                if (!submitted.IsSuccessStatusCode)
                {
                    var body = await submitted.Content.ReadAsStringAsync(stop).ConfigureAwait(false);
                    _ready.Enqueue(new PortMessage(requestId, AsServiceError(jobJson, body, (int)submitted.StatusCode), IsError: true));
                    return;
                }
            }

            var wait = _pollInterval;
            while (true)
            {
                using var response = await _http.GetAsync(new Uri("jobs/" + Uri.EscapeDataString(requestId), UriKind.Relative), stop).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(stop).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _ready.Enqueue(new PortMessage(requestId, AsServiceError(jobJson, body, (int)response.StatusCode), IsError: true));
                    return;
                }

                switch (StateOf(body))
                {
                    case JobState.Done or JobState.Failed:
                        _ready.Enqueue(new PortMessage(requestId, body, IsError: false));
                        return;
                    case null:
                        // Not a valid job status: an internal error that a new attempt will not fix (review GW2 #75).
                        _ready.Enqueue(new PortMessage(requestId, Synthesize(jobJson, ServiceErrorCode.Internal, "AI service sent an invalid job status.", retryable: false), IsError: true));
                        return;
                    case JobState.Running:
                        wait = _pollInterval;
                        break;
                    default:
                        // Queued or deferred (night kinds wait for the night): poll less and less often.
                        wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, _maxPollInterval.Ticks));
                        break;
                }

                await Task.Delay(wait, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The game is shutting down; nobody reads the outcome any more.
        }
        catch (ObjectDisposedException)
        {
            // Same: the port was disposed while a call was running.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            var code = ex is HttpRequestException ? ServiceErrorCode.Model_unavailable : ServiceErrorCode.Timeout;
            _ready.Enqueue(new PortMessage(requestId, Synthesize(jobJson, code, "AI service not reachable (" + ex.GetType().Name + ").", retryable: true), IsError: true));

            // The core gives this id up and retries with a new one; the old job must not keep running
            // in the service (review GW2 #75). Best effort: the service may be gone.
            await CancelAsync(requestId).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>
    /// True for a done or failed job. A body that is no valid JobStatus is handed to the core as is, which
    /// logs it as a contract violation (never polled forever).
    /// </summary>
    /// <summary>
    /// Tells the service that the compute night begins or ends (<c>POST /phase</c>, contract <c>phase-change</c>);
    /// only a real change is sent, asynchronously and best effort (a failure only means the service plans
    /// without it until the next change or <see cref="Resend"/>).
    /// </summary>
    public void SetPhase(string phase, long tick)
    {
        ArgumentException.ThrowIfNullOrEmpty(phase);
        lock (_phaseLock)
        {
            if (_phase == phase)
            {
                return;
            }

            _phase = phase;
            _phaseTick = tick;
        }

        SendPhase(phase, tick);
    }

    /// <summary>Sends the last phase again, e.g. after the service was started anew (it starts in day).</summary>
    public void Resend()
    {
        string? phase;
        long tick;
        lock (_phaseLock)
        {
            (phase, tick) = (_phase, _phaseTick);
        }

        if (phase is not null)
        {
            SendPhase(phase, tick);
        }
    }

    private void SendPhase(string phase, long tick)
    {
        var body = ContractSerializer.Serialize(new PhaseChange
        {
            ContractVersion = ContractInfo.Version,
            Phase = phase == JobPhase.Night ? PhaseChangePhase.Night : PhaseChangePhase.Day,
            Tick = tick,
        });
        Interlocked.Increment(ref _inFlight);
        _ = Task.Run(async () =>
        {
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(new Uri("phase", UriKind.Relative), content, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ObjectDisposedException)
            {
                // Best effort: the planner works without the signal until the next one.
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        });
    }

    /// <summary>Cancels a job the core no longer waits for (<c>DELETE /jobs/{id}</c>, idempotent, best effort).</summary>
    public async Task CancelAsync(string requestId)
    {
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = await _http.DeleteAsync(new Uri("jobs/" + Uri.EscapeDataString(requestId), UriKind.Relative), cancel.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
            // Nothing to do: the service is not reachable or the game is closing.
        }
    }

    private static JobState? StateOf(string body)
    {
        try
        {
            return ContractSerializer.Deserialize<JobStatus>(body).State;
        }
        catch (ContractViolationException)
        {
            return null;
        }
    }

    private static string AsServiceError(string jobJson, string body, int status)
    {
        if (ContractValidator.Validate("service-error", body).Count == 0)
        {
            return body;
        }

        var message = string.Create(CultureInfo.InvariantCulture, $"AI service answered HTTP {status} without a valid error body.");
        return status >= 500
            ? Synthesize(jobJson, ServiceErrorCode.Internal, message, retryable: true)
            : Synthesize(jobJson, ServiceErrorCode.Bad_request, message, retryable: false);
    }

    private static string Synthesize(string jobJson, ServiceErrorCode code, string message, bool retryable)
    {
        var job = ContractSerializer.Deserialize<GenerationJob>(jobJson);
        return ContractSerializer.Serialize(new ServiceError
        {
            ContractVersion = job.ContractVersion,
            RequestId = job.RequestId,
            Tick = job.Tick,
            Code = code,
            Message = message,
            Retryable = retryable,
        });
    }
}
