using System.Net;
using System.Text;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;

namespace GlassWorld.AiClient.Tests;

public sealed class HttpJobPortTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public async Task AJobIsPolledUntilItIsDoneAndOnlyTheFinalStateReachesTheCore()
    {
        var job = Job();
        var polls = 0;
        var paths = new List<string>();
        using var port = NewPort(request =>
        {
            paths.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post)
            {
                return Json(HttpStatusCode.Accepted, ContractSerializer.Serialize(new JobAccepted { ContractVersion = ContractInfo.Version, JobId = job.RequestId, State = JobState.Queued }));
            }

            polls++;
            return Json(HttpStatusCode.OK, Status(job, polls < 3 ? JobState.Running : JobState.Done));
        });

        port.Submit(job.RequestId, ContractSerializer.Serialize(job));
        await port.WhenIdleAsync(TimeSpan.FromSeconds(10));

        var message = Assert.Single(port.Receive(0));
        Assert.False(message.IsError);
        Assert.Equal(JobState.Done, ContractSerializer.Deserialize<JobStatus>(message.Json).State);
        Assert.Equal(3, polls);
        Assert.Equal("POST /jobs", paths[0]);
        Assert.All(paths.Skip(1), p => Assert.Equal("GET /jobs/" + job.RequestId, p));
    }

    [Fact]
    public async Task AnUnreachableServiceGivesARetryableError()
    {
        var job = Job();
        using var port = NewPort(_ => throw new HttpRequestException("connection refused"));
        port.Submit(job.RequestId, ContractSerializer.Serialize(job));
        await port.WhenIdleAsync(TimeSpan.FromSeconds(5));

        var message = Assert.Single(port.Receive(0));
        Assert.True(message.IsError);
        var error = ContractSerializer.Deserialize<ServiceError>(message.Json);
        Assert.Equal((ServiceErrorCode.Model_unavailable, true, job.RequestId), (error.Code, error.Retryable, error.RequestId));
    }

    [Fact]
    public async Task AServiceErrorFromTheServiceIsPassedThrough()
    {
        var job = Job();
        var body = ContractSerializer.Serialize(new ServiceError { ContractVersion = ContractInfo.Version, RequestId = job.RequestId, Tick = job.Tick, Code = ServiceErrorCode.Overloaded, Message = "full", Retryable = true });
        using var port = NewPort(_ => Json(HttpStatusCode.ServiceUnavailable, body));
        port.Submit(job.RequestId, ContractSerializer.Serialize(job));
        await port.WhenIdleAsync(TimeSpan.FromSeconds(5));

        var message = Assert.Single(port.Receive(0));
        Assert.True(message.IsError);
        Assert.Equal(body, message.Json);
    }

    private static HttpJobPort NewPort(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new Uri("http://127.0.0.1:5000/"), "token", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(5), new FakeService(respond));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static GenerationJob Job() =>
        ContractSerializer.Deserialize<GenerationJob>(File.ReadAllText(Path.Combine(Root, "contracts", "examples", "valid", "generation-job.invention-water-vessel.json")));

    private static string Status(GenerationJob job, JobState state)
    {
        var status = ContractSerializer.Deserialize<JobStatus>(File.ReadAllText(Path.Combine(Root, "contracts", "examples", "valid", "job-status.invention-approved.json")));
        status.JobId = job.RequestId;
        status.Tick = job.Tick;
        if (state != JobState.Done)
        {
            status.State = state;
            status.Result = null;
            status.Provenance = null;
        }

        return ContractSerializer.Serialize(status);
    }

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
