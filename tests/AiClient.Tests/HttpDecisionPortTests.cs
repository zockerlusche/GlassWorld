using System.Net;
using System.Text;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Decisions;

namespace GlassWorld.AiClient.Tests;

public sealed class HttpDecisionPortTests
{
    private static readonly string Content = FindContent();

    [Fact]
    public async Task UnreachableServiceGivesARetryableModelUnavailable()
    {
        using var port = NewPort(new FakeService(_ => throw new HttpRequestException("connection refused")));
        port.Send("r1", SampleRequest());
        await port.WhenIdleAsync(TimeSpan.FromSeconds(5));

        var message = Assert.Single(port.Receive(0));
        Assert.True(message.IsError);
        var error = ContractSerializer.Deserialize<ServiceError>(message.Json);
        Assert.Equal((ServiceErrorCode.Model_unavailable, true), (error.Code, error.Retryable));
    }

    [Fact]
    public async Task ServiceErrorsAndAnswersArePassedThroughWithTokenAndPath()
    {
        HttpRequestMessage? seen = null;
        var answer = ContractSerializer.Serialize(new DecisionResponse
        {
            ContractVersion = "0.1",
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
        await port.WhenIdleAsync(TimeSpan.FromSeconds(5));

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
            await port.WhenIdleAsync(TimeSpan.FromSeconds(5));
        }

        Assert.NotEmpty(host.Decisions.Open);
        Assert.All(host.Decisions.Open, q => Assert.NotNull(q.RetryAtTick));
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
            await port.WhenIdleAsync(TimeSpan.FromSeconds(5));
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
