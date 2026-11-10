using System.Net;
using System.Text;
using GlassWorld.Contracts;

namespace GlassWorld.AiClient.Tests;

public sealed class HttpSetupPortTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public async Task TheSelfCheckIsReadFromGetSetupCheck()
    {
        var paths = new List<string>();
        using var port = NewPort(request =>
        {
            paths.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            return Json(HttpStatusCode.OK, Example("self-check.nodes-missing.json"));
        });

        var answer = await port.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(answer.Problem);
        Assert.Equal(["GET /setup/check"], paths);
        var image = Assert.Single(answer.Check!.Checks, c => c.Ai == AiCheckAi.Image);
        Assert.Equal(AiCheckState.Problem, image.State);
        Assert.Equal("msg.selfcheck.image.nodes_missing.fix", image.FixKey);
        Assert.Equal(["UnetLoaderGGUF"], image.Detail);
    }

    [Fact]
    public async Task TheReportIsReadFromPostDiagnostics()
    {
        var paths = new List<string>();
        using var port = NewPort(request =>
        {
            paths.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            return Json(HttpStatusCode.OK, Example("diagnostics-report.comfyui-down.json"));
        });

        var answer = await port.DiagnosticsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["POST /diagnostics"], paths);
        Assert.Equal("diagnostics-20261110-101500.md", answer.Report!.FileName);
        Assert.Contains("Self-check", answer.Report.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("self-check.unknown-ai.json")]
    [InlineData("diagnostics-report.path-in-name.json")]
    public async Task AnAnswerAgainstTheContractIsNoAnswer(string invalid)
    {
        using var port = NewPort(_ => Json(HttpStatusCode.OK, File.ReadAllText(Path.Combine(Root, "contracts", "examples", "invalid", invalid))));

        var check = await port.CheckAsync(TestContext.Current.CancellationToken);
        var report = await port.DiagnosticsAsync(TestContext.Current.CancellationToken);

        Assert.Null(check.Check);
        Assert.Null(report.Report);
        Assert.NotNull(check.Problem);
        Assert.NotNull(report.Problem);
    }

    [Fact]
    public async Task AServiceProblemNeverThrows()
    {
        using var down = NewPort(_ => throw new HttpRequestException("refused"));
        using var error = NewPort(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"));

        Assert.Null((await down.CheckAsync(TestContext.Current.CancellationToken)).Check);
        Assert.Null((await down.DiagnosticsAsync(TestContext.Current.CancellationToken)).Report);
        Assert.Equal("service answered 503", (await error.CheckAsync(TestContext.Current.CancellationToken)).Problem);
    }

    [Fact]
    public void OnlyTheLocalServiceIsAsked() =>
        Assert.Throws<ArgumentException>(() => new HttpSetupPort(new Uri("http://example.org/"), "token", TimeSpan.FromSeconds(1)));

    private static HttpSetupPort NewPort(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new Uri("http://127.0.0.1:5000/"), "token", TimeSpan.FromSeconds(5), new FakeService(respond));

    private static string Example(string name) => File.ReadAllText(Path.Combine(Root, "contracts", "examples", "valid", name));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GlassWorld.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class FakeService(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
