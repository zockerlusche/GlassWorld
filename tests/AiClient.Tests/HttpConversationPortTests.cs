using System.Net;
using System.Text;

namespace GlassWorld.AiClient.Tests;

/// <summary>Conversations through the AI service (A1342): answers and problems reach the core as messages.</summary>
public sealed class HttpConversationPortTests
{
    private static readonly Uri Service = new("http://127.0.0.1:50999/");

    [Fact]
    public async Task AnAnswerIsQueuedForTheNextTick()
    {
        string? path = null;
        using var port = new HttpConversationPort(Service, "token", TimeSpan.FromSeconds(5), new FakeService(request =>
        {
            path = request.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"outcome\":\"reply\"}", Encoding.UTF8, "application/json") };
        }));
        port.Send("r-1", "{}");
        var message = await Single(port);
        Assert.Equal("/conversation", path);
        Assert.Equal("r-1", message.RequestId);
        Assert.False(message.IsError);
        Assert.Contains("reply", message.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnyProblemBecomesAnErrorMessage()
    {
        // The core turns every error into a calm "no answer" with the favour back (R16).
        using var refused = new HttpConversationPort(Service, "token", TimeSpan.FromSeconds(5), new FakeService(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }));
        refused.Send("r-2", "{}");
        Assert.True((await Single(refused)).IsError);

        using var down = new HttpConversationPort(Service, "token", TimeSpan.FromSeconds(5), new FakeService(_ => throw new HttpRequestException("connection refused")));
        down.Send("r-3", "{}");
        var message = await Single(down);
        Assert.True(message.IsError);
        Assert.Equal("r-3", message.RequestId);
    }

    [Fact]
    public void OnlyTheLocalServiceIsAllowed() =>
        Assert.Throws<ArgumentException>(() => new HttpConversationPort(new Uri("http://localhost:50999/"), "token", TimeSpan.FromSeconds(5)));

    private static async Task<Simulation.Decisions.PortMessage> Single(HttpConversationPort port)
    {
        for (var i = 0; i < 200 && port.InFlight > 0; i++)
        {
            await Task.Delay(10);
        }

        return Assert.Single(port.Receive(0));
    }

    private sealed class FakeService(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
