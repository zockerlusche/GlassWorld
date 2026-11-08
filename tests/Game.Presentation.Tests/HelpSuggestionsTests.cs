using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GlassWorld.AiClient;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.Tests;

/// <summary>Help suggestions for a call (A1341 criterion 3, B6 R8/R10, F5238).</summary>
public sealed class HelpSuggestionsTests
{
    private static readonly string Examples = Path.Combine(TestPaths.Root, "contracts", "examples", "valid");

    [Fact]
    public async Task TheMockGivesTwoToThreeSuggestionsForAThirstyInhabitant()
    {
        // The stand-in answers like the service's MockHelp (contracts example help-suggestions-response.mock,
        // checked against the real mock in ai-service/tests/test_help.py): it echoes id and tick and
        // offers the first allowed gift.
        var host = ThirstyWorld(out var call);
        var request = HelpSuggestions.Request(host, call, "de");
        Assert.Empty(ContractValidator.Validate("help-suggestions-request", ContractSerializer.Serialize(request)));
        Assert.NotEmpty(request.AllowedGifts);

        string? posted = null;
        using var service = new FakeService(message =>
        {
            Assert.Equal("/help-suggestions", message.RequestUri!.AbsolutePath);
            Assert.True(message.Headers.Contains(HttpDecisionPort.TokenHeader));
            posted = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var sent = JsonNode.Parse(posted)!;
            var answer = JsonNode.Parse(File.ReadAllText(Path.Combine(Examples, "help-suggestions-response.mock.json")))!;
            answer["request_id"] = sent["request_id"]!.GetValue<string>();
            answer["tick"] = sent["tick"]!.GetValue<long>();
            answer["suggestions"]![1]!["gift"]!["type_id"] = sent["allowed_gifts"]![0]!["type_id"]!.GetValue<string>();
            return Json(HttpStatusCode.OK, answer.ToJsonString());
        });
        using var port = new HttpHelpPort(new Uri("http://127.0.0.1:1/"), "secret", TimeSpan.FromSeconds(30), service);

        var reply = await port.SuggestAsync(request, TestContext.Current.CancellationToken);
        Assert.Null(reply.Problem);
        Assert.Equal(request.RequestId, JsonNode.Parse(posted!)!["request_id"]!.GetValue<string>());

        var cards = HelpSuggestions.Cards(reply.Response, HelpSuggestions.AllowedTypes(host, call), HelpSuggestions.MaxSuggestions(host));
        Assert.InRange(cards.Count, 2, 3);
        Assert.Equal([HelpCard.Answer, HelpCard.Gift], cards.Select(c => c.Kind));
        Assert.All(cards, c => Assert.False(string.IsNullOrWhiteSpace(c.Text)));
    }

    [Fact]
    public void TheSameCallAsksTheSameQuestion()
    {
        var first = HelpSuggestions.Request(ThirstyWorld(out var call), call, "en");
        var second = HelpSuggestions.Request(ThirstyWorld(out var again), again, "en");
        Assert.Equal(ContractSerializer.Serialize(first), ContractSerializer.Serialize(second));
        Assert.Equal(HelpSuggestionsRequestTrigger.Knock, first.Trigger);
    }

    [Fact]
    public void GiftsOutsideTheAllowedListAreDroppedAndNothingUsableFallsBackToTheData()
    {
        var response = ContractSerializer.Deserialize<HelpSuggestionsResponse>(File.ReadAllText(Path.Combine(Examples, "help-suggestions-response.mock.json")));

        // The mock's gift (water_bowl) is not allowed here: only the answer stays.
        var onlyAnswer = HelpSuggestions.Cards(response, ["apple"], 3);
        Assert.Equal([HelpCard.Answer], onlyAnswer.Select(c => c.Kind));

        // No answer at all: gift cards from the data, without AI text.
        var fallback = HelpSuggestions.Cards(null, ["apple", "pear", "plum", "fig"], 3);
        Assert.Equal(["apple", "pear", "plum"], fallback.Select(c => c.GiftType));
        Assert.All(fallback, c => Assert.Null(c.Text));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"code":"model_unavailable"}""")]
    [InlineData(HttpStatusCode.OK, "<html>oops</html>")]
    [InlineData(HttpStatusCode.OK, """{"contract_version":"0.2"}""")]
    public async Task AnyProblemGivesNoResponse(HttpStatusCode status, string body)
    {
        var host = ThirstyWorld(out var call);
        using var service = new FakeService(_ => Json(status, body));
        using var port = new HttpHelpPort(new Uri("http://127.0.0.1:1/"), "secret", TimeSpan.FromSeconds(30), service);
        var reply = await port.SuggestAsync(HelpSuggestions.Request(host, call, "de"), TestContext.Current.CancellationToken);
        Assert.Null(reply.Response);
        Assert.NotNull(reply.Problem);
    }

    [Fact]
    public async Task AnAnswerForAnotherRequestIsIgnored()
    {
        var host = ThirstyWorld(out var call);
        var example = File.ReadAllText(Path.Combine(Examples, "help-suggestions-response.mock.json"));
        using var service = new FakeService(_ => Json(HttpStatusCode.OK, example));
        using var port = new HttpHelpPort(new Uri("http://127.0.0.1:1/"), "secret", TimeSpan.FromSeconds(30), service);
        var reply = await port.SuggestAsync(HelpSuggestions.Request(host, call, "de"), TestContext.Current.CancellationToken);
        Assert.Null(reply.Response);
    }

    [Fact]
    public async Task AnUnreachableServiceGivesNoResponse()
    {
        var host = ThirstyWorld(out var call);
        using var service = new FakeService(_ => throw new HttpRequestException("refused"));
        using var port = new HttpHelpPort(new Uri("http://127.0.0.1:1/"), "secret", TimeSpan.FromSeconds(30), service);
        var reply = await port.SuggestAsync(HelpSuggestions.Request(host, call, "de"), TestContext.Current.CancellationToken);
        Assert.Null(reply.Response);
    }

    private static SimulationHost ThirstyWorld(out HelpRow call)
    {
        var host = SimulationHost.CreateEmpty(Path.Combine(TestPaths.Root, "content"), 1, new MockDecisionPort(), "de");
        var needs = host.Content.InhabitantModel.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal);
        needs["thirst"] = 0.05;
        host.Inhabitants.Add(new Inhabitant("inh-000001", "female", 1, new Position(0, 0), needs, new Dictionary<string, double>(StringComparer.Ordinal), new Dictionary<string, double>(StringComparer.Ordinal), 0.8));
        call = new HelpRow("inh-000001", 1, "thirst", host.World.Tick);
        return host;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>In-process stand-in for the AI service (no sockets, CI-safe).</summary>
    private sealed class FakeService(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
