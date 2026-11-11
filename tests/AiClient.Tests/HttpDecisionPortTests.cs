using System.Net;
using System.Text;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.AiClient.Tests;

public sealed class HttpDecisionPortTests
{
    /// <summary>
    /// Upper bound for a request against the fake service; a passing run takes milliseconds. It was
    /// 5 s, which a loaded CI machine sometimes exceeded together with the one-time schema load.
    /// </summary>
    private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(30);

    private static readonly string Content = FindContent();

    /// <summary>
    /// The first contract validation loads all schemas once (about 1-2 s, measured; longer under a
    /// parallel test run). Error answers are validated in the port, so this happens before the timed
    /// part: the tests measure the port, not the schema load (cause of the flaky
    /// ErrorBodyWithoutValidJson... runs).
    /// </summary>
    public HttpDecisionPortTests() => _ = ContractValidator.SchemaNames.Count;

    [Fact]
    public async Task UnreachableServiceGivesARetryableModelUnavailable()
    {
        using var port = NewPort(new FakeService(_ => throw new HttpRequestException("connection refused")));
        port.Send("r1", SampleRequest());
        Assert.True(await port.WhenIdleAsync(IdleLimit), "the port did not finish its requests in time");

        var message = Assert.Single(port.Receive(0));
        Assert.True(message.IsError);
        var error = ContractSerializer.Deserialize<ServiceError>(message.Json);
        Assert.Equal((ServiceErrorCode.Model_unavailable, true), (error.Code, error.Retryable));
    }

    [Theory]
    [InlineData(500, ServiceErrorCode.Internal, true)]
    [InlineData(502, ServiceErrorCode.Internal, true)]
    [InlineData(404, ServiceErrorCode.Bad_request, false)]
    public async Task ErrorBodyWithoutValidJsonBecomesAContractServiceError(int status, ServiceErrorCode code, bool retryable)
    {
        using var port = NewPort(new FakeService(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("<html>oops</html>") }));
        port.Send("r1", SampleRequest());
        Assert.True(await port.WhenIdleAsync(IdleLimit), "the port did not finish its requests in time");

        var message = Assert.Single(port.Receive(0));
        Assert.True(message.IsError);
        var error = ContractSerializer.Deserialize<ServiceError>(message.Json);
        Assert.Equal((code, retryable), (error.Code, error.Retryable));
    }

    /// <summary>
    /// ADR-0009: the running game does not validate what it sends, so the port must be able to report a
    /// failure also for a request that is not valid - otherwise the question would wait for an answer that
    /// never comes (the port used to read the request with validation to write the error).
    /// </summary>
    [Theory]
    [InlineData("{\"contract_version\":\"0.2\",\"request_id\":\"not-an-id\",\"tick\":\"soon\"}")]
    [InlineData("{\"tick\":12}")]
    [InlineData("[1,2,3]")]
    [InlineData("this is no json")]
    public async Task AFailureIsReportedAlsoForARequestThatIsNotValid(string request)
    {
        using var unreachable = NewPort(new FakeService(_ => throw new HttpRequestException("connection refused")));
        unreachable.Send("r1", request);
        Assert.True(await unreachable.WhenIdleAsync(IdleLimit), "the port did not finish its requests in time");
        var message = Assert.Single(unreachable.Receive(0));
        Assert.Equal(("r1", true), (message.RequestId, message.IsError));
        var error = ContractSerializer.Deserialize<ServiceError>(message.Json);
        Assert.Equal((ServiceErrorCode.Model_unavailable, true), (error.Code, error.Retryable));

        // The service refuses it (422 without a ServiceError body): a refusal, not retryable.
        using var refusing = NewPort(new FakeService(_ => new HttpResponseMessage((HttpStatusCode)422) { Content = new StringContent("{\"detail\":\"invalid\"}") }));
        refusing.Send("r2", request);
        Assert.True(await refusing.WhenIdleAsync(IdleLimit), "the port did not finish its requests in time");
        var refused = ContractSerializer.Deserialize<ServiceError>(Assert.Single(refusing.Receive(0)).Json);
        Assert.Equal((ServiceErrorCode.Bad_request, false), (refused.Code, refused.Retryable));

        // What the service did say is kept, also when it was no ServiceError.
        Assert.Equal("AI service answered HTTP 422 without a valid error body: {\"detail\":\"invalid\"}", refused.Message);
    }

    /// <summary>
    /// Review GW2 #244: the service refuses an invalid request with a ServiceError that names the first
    /// problems. With the request no longer validated on the way out (ADR-0009), those words are the only
    /// place that says what was wrong: they must be readable in the log entry of the counted refusal.
    /// </summary>
    [Fact]
    public async Task WhatTheServiceSaidWasWrongStaysInTheLogEntryOfARefusedRequest()
    {
        const string Problems = "Invalid DecisionRequest: $.perception.objects[3].distance_m: -1 is less than the minimum of 0; $.options: [] should be non-empty";
        var service = new FakeService(request =>
        {
            // As the service does: 422 and a ServiceError bad_request with the request's id and tick and the problems.
            var sent = ContractSerializer.Deserialize<DecisionRequest>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var body = ContractSerializer.Serialize(new ServiceError
            {
                ContractVersion = ContractInfo.Version,
                RequestId = sent.RequestId,
                Tick = sent.Tick,
                Code = ServiceErrorCode.Bad_request,
                Message = Problems,
                Retryable = false,
            });
            return new HttpResponseMessage((HttpStatusCode)422) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        });
        using var port = NewPort(service);
        var host = SimulationHost.CreatePlaceholderWorld(Content, 3, port, "de", options: new SimulationHostOptions { ValidateRequests = false });
        for (var i = 0; i < 20; i++)
        {
            host.World.Step();
            Assert.True(await port.WhenIdleAsync(IdleLimit), "the port did not finish its requests in time");
        }

        var refused = host.Decisions.Log.Entries.Where(e => e.Kind == DecisionLogKind.Error).ToList();
        Assert.NotEmpty(refused);
        Assert.All(refused, e =>
        {
            Assert.Equal(ReplayNotes.RequestRejected, e.Note);
            Assert.Equal(Problems, ReplayNotes.MessageOf(e.Json));
            Assert.Equal(e.RequestId, ContractSerializer.Deserialize<ServiceError>(e.Json).RequestId);
        });
        Assert.Equal(refused.Count, host.RejectedRequests);
        Assert.Equal(refused.Select(e => (e.RequestId, e.Tick, (string?)Problems)), host.Rejections);
    }

    [Theory]
    [InlineData(404, "<html>\n  <body>Not   Found</body>\r\n</html>", "AI service answered HTTP 404 without a valid error body: <html> <body>Not Found</body> </html>")]
    [InlineData(502, "", "AI service answered HTTP 502 without a valid error body.")]
    [InlineData(500, " \n\t ", "AI service answered HTTP 500 without a valid error body.")]
    public void TheBeginningOfABodyThatIsNoServiceErrorIsKeptOnOneLine(int status, string body, string message) =>
        Assert.Equal(message, ServiceErrors.WithoutValidBody(status, body));

    [Fact]
    public void ALongBodyIsCutSoTheMessageFitsAServiceError()
    {
        var message = ServiceErrors.WithoutValidBody(422, new string('x', 5000));
        Assert.EndsWith(": " + new string('x', ServiceErrors.ExcerptLength), message, StringComparison.Ordinal);
        Assert.True(message.Length <= 500);
        Assert.Equal(message, ContractSerializer.Deserialize<ServiceError>(ServiceErrors.For(SampleRequest(), ServiceErrorCode.Bad_request, message, retryable: false)).Message);
    }

    [Fact]
    public void ASynthesizedErrorNamesTheRequestWhereItCanBeRead()
    {
        var valid = ContractSerializer.Deserialize<DecisionRequest>(SampleRequest());
        var named = ContractSerializer.Deserialize<ServiceError>(ServiceErrors.For(SampleRequest(), ServiceErrorCode.Timeout, "late", retryable: true));
        Assert.Equal((valid.ContractVersion, valid.RequestId, valid.Tick), (named.ContractVersion, named.RequestId, named.Tick));

        var unreadable = ContractSerializer.Deserialize<ServiceError>(ServiceErrors.For("{\"request_id\":7}", ServiceErrorCode.Internal, "broken", retryable: true));
        Assert.Equal((ContractInfo.Version, ServiceErrors.UnreadableRequestId, 0L), (unreadable.ContractVersion, unreadable.RequestId, unreadable.Tick));
    }

    [Theory]
    [InlineData("http://localhost:5000/")]
    [InlineData("http://0.0.0.0:5000/")]
    [InlineData("http://192.168.1.2:5000/")]
    public void OnlyTheLoopbackAddressIsAccepted(string address) =>
        Assert.Throws<ArgumentException>(() => new HttpDecisionPort(new Uri(address), "t", TimeSpan.FromSeconds(1)));

    [Fact]
    public async Task ServiceErrorsAndAnswersArePassedThroughWithTokenAndPath()
    {
        HttpRequestMessage? seen = null;
        var answer = ContractSerializer.Serialize(new DecisionResponse
        {
            ContractVersion = ContractInfo.Version,
            RequestId = "3f2b8c1e-5d4a-4b6f-9e21-0c7d8a9b1f00",
            Tick = 1,
            OptionId = "o1",
            Rationale = "x",
            Meta = new ResponseMeta { Source = ResponseMetaSource.Mock, Model = "m", LatencyMs = 1, Attempts = 1 },
        });
        using var port = NewPort(new FakeService(request =>
        {
            seen = request;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }));
        port.Send("r1", SampleRequest());
        Assert.True(await port.WhenIdleAsync(IdleLimit), "the port did not finish its requests in time");

        var message = Assert.Single(port.Receive(0));
        Assert.False(message.IsError);
        Assert.Equal(answer, message.Json);
        Assert.Equal("/decide", seen!.RequestUri!.AbsolutePath);
        Assert.Equal("secret", Assert.Single(seen.Headers.GetValues(HttpDecisionPort.TokenHeader)));
    }

    /// <summary>
    /// B3 acceptance A3 (core part) and A6: with Ollama or the service off, the game keeps running in
    /// routine mode and the question stays open; when the service is back, it is answered.
    /// </summary>
    [Fact]
    public async Task WithTheServiceOffInhabitantsLiveOnAndQuestionsWaitUntilItIsBack()
    {
        var service = new FakeService(_ => throw new HttpRequestException("connection refused"));
        using var port = NewPort(service);
        var host = SimulationHost.CreatePlaceholderWorld(Content, 3, port, "de");

        for (var i = 0; i < 120; i++)
        {
            host.World.Step();
            Assert.True(await port.WhenIdleAsync(IdleLimit), "the port did not finish its requests in time");
        }

        // Every question that went out waits to be sent again; further ones were not sent after it at all
        // (A1487: one thing at a time), they wait behind it with their question marks.
        Assert.Contains(host.Decisions.Open, q => q.RetryAtTick is not null);
        Assert.All(host.Decisions.Open, q => Assert.True(q.RetryAtTick is not null || q.RequestId.Length == 0));
        Assert.Contains(host.Inhabitants.All, i => i.Activity is not null);
        Assert.DoesNotContain(host.Decisions.Log.Entries, e => e.Kind == DecisionLogKind.Response);

        // The service comes back: it answers every request with its first option.
        service.Respond = request =>
        {
            var parsed = ContractSerializer.Deserialize<DecisionRequest>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var json = ContractSerializer.Serialize(new DecisionResponse
            {
                ContractVersion = parsed.ContractVersion,
                RequestId = parsed.RequestId,
                Tick = parsed.Tick,
                OptionId = parsed.Options.First().OptionId,
                Rationale = "back",
                Meta = new ResponseMeta { Source = ResponseMetaSource.Model, Model = "fake", LatencyMs = 1, Attempts = 1 },
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        };
        var retryTicks = (long)host.DeciderSettings.RetryAfterMinutes * host.World.Calendar.Settings.TicksPerHour / 60;
        for (var i = 0; i < retryTicks + 5; i++)
        {
            host.World.Step();
            Assert.True(await port.WhenIdleAsync(IdleLimit), "the port did not finish its requests in time");
        }

        Assert.Contains(host.Decisions.Log.Entries, e => e.Kind == DecisionLogKind.Response);
    }

    private static HttpDecisionPort NewPort(FakeService service) =>
        new(new Uri("http://127.0.0.1:1/"), "secret", TimeSpan.FromSeconds(30), service);

    private static string SampleRequest() =>
        File.ReadAllText(Path.Combine(Content, "..", "contracts", "examples", "valid", "decision-request.hungry-berries.json"));

    private static string FindContent()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GlassWorld.slnx")))
            {
                return Path.Combine(dir.FullName, "content");
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    /// <summary>In-process stand-in for the AI service (no sockets, CI-safe).</summary>
    private sealed class FakeService(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request));
    }
}
