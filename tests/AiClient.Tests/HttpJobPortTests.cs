using System.Net;
using System.Text;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Decisions;

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

    [Fact]
    public async Task AJobGivenUpAfterAPollFailureIsCancelledInTheService()
    {
        var job = Job();
        var deleted = new List<string>();
        var polls = 0;
        using var port = NewPort(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json(HttpStatusCode.Accepted, ContractSerializer.Serialize(new JobAccepted { ContractVersion = ContractInfo.Version, JobId = job.RequestId, State = JobState.Queued }));
            }

            if (request.Method == HttpMethod.Delete)
            {
                deleted.Add(request.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return ++polls < 2 ? Json(HttpStatusCode.OK, Status(job, JobState.Running)) : throw new HttpRequestException("connection reset");
        });

        port.Submit(job.RequestId, ContractSerializer.Serialize(job));
        await port.WhenIdleAsync(TimeSpan.FromSeconds(5));

        Assert.True(Assert.Single(port.Receive(0)).IsError);
        Assert.Equal(["/jobs/" + job.RequestId], deleted);
    }

    [Fact]
    public async Task AnInvalidJobStatusIsANonRetryableInternalError()
    {
        var job = Job();
        using var port = NewPort(request => request.Method == HttpMethod.Post
            ? Json(HttpStatusCode.Accepted, ContractSerializer.Serialize(new JobAccepted { ContractVersion = ContractInfo.Version, JobId = job.RequestId, State = JobState.Queued }))
            : Json(HttpStatusCode.OK, "{\"state\": \"nonsense\"}"));
        port.Submit(job.RequestId, ContractSerializer.Serialize(job));
        await port.WhenIdleAsync(TimeSpan.FromSeconds(5));

        var message = Assert.Single(port.Receive(0));
        Assert.True(message.IsError);
        var error = ContractSerializer.Deserialize<ServiceError>(message.Json);
        Assert.Equal((ServiceErrorCode.Internal, false), (error.Code, error.Retryable));
    }

    [Fact]
    public async Task ADeferredJobIsPolledLessAndLessOften()
    {
        var job = Job();
        var times = new List<DateTime>();
        using var port = new HttpJobPort(new Uri("http://127.0.0.1:5000/"), "token", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10), new FakeService(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json(HttpStatusCode.Accepted, ContractSerializer.Serialize(new JobAccepted { ContractVersion = ContractInfo.Version, JobId = job.RequestId, State = JobState.Queued }));
            }

            times.Add(DateTime.UtcNow);
            return Json(HttpStatusCode.OK, Status(job, times.Count < 6 ? JobState.Deferred : JobState.Done));
        }), maxPollInterval: TimeSpan.FromMilliseconds(160));

        port.Submit(job.RequestId, ContractSerializer.Serialize(job));
        await port.WhenIdleAsync(TimeSpan.FromSeconds(10));

        Assert.False(Assert.Single(port.Receive(0)).IsError);
        var gaps = times.Zip(times.Skip(1), (a, b) => (b - a).TotalMilliseconds).ToList();
        Assert.True(gaps[^1] > gaps[0], $"polls did not slow down: {string.Join(", ", gaps)}");
    }

    [Fact]
    public async Task OnlyARealPhaseChangeIsPostedAndResendRepeatsTheLastOne()
    {
        var posts = new List<(string Path, string Body, bool Token)>();
        using var port = NewPort(request =>
        {
            posts.Add((request.RequestUri!.AbsolutePath, request.Content!.ReadAsStringAsync().Result, request.Headers.Contains(HttpDecisionPort.TokenHeader)));
            return Json(HttpStatusCode.OK, "{}");
        });

        port.SetPhase(JobPhase.Night, 1320);
        await port.WhenIdleAsync(TimeSpan.FromSeconds(5));
        port.SetPhase(JobPhase.Night, 1321);
        await port.WhenIdleAsync(TimeSpan.FromSeconds(5));
        port.SetPhase(JobPhase.Day, 1800);
        await port.WhenIdleAsync(TimeSpan.FromSeconds(5));
        port.Resend();
        await port.WhenIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, posts.Count);
        Assert.All(posts, p => Assert.Equal(("/phase", true), (p.Path, p.Token)));
        Assert.All(posts, p => Assert.Empty(ContractValidator.Validate("phase-change", p.Body)));
        var sent = posts.Select(p => ContractSerializer.Deserialize<PhaseChange>(p.Body)).Select(c => (c.Phase, c.Tick)).ToList();
        Assert.Equal([(PhaseChangePhase.Night, 1320L), (PhaseChangePhase.Day, 1800L), (PhaseChangePhase.Day, 1800L)], sent);
    }

    [Fact]
    public async Task PhaseSignalsArriveInCallOrderAndForceSendsTheSamePhaseAgain()
    {
        var phases = new List<PhaseChangePhase>();
        var first = true;
        using var port = NewPort(request =>
        {
            if (first)
            {
                // The first post is slow: the later ones must still arrive after it.
                first = false;
                Thread.Sleep(200);
            }

            lock (phases)
            {
                phases.Add(ContractSerializer.Deserialize<PhaseChange>(request.Content!.ReadAsStringAsync().Result).Phase);
            }

            return Json(HttpStatusCode.OK, "{}");
        });

        port.SetPhase(JobPhase.Night, 10);
        port.SetPhase(JobPhase.Day, 20);
        port.SetPhase(JobPhase.Night, 30);
        port.SetPhase(JobPhase.Night, 31, force: true);
        await port.WhenIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([PhaseChangePhase.Night, PhaseChangePhase.Day, PhaseChangePhase.Night, PhaseChangePhase.Night], phases);
    }

    [Fact]
    public async Task AnUnreachableServiceDoesNotBreakThePhaseSignal()
    {
        using var port = NewPort(_ => throw new HttpRequestException("connection refused"));
        port.SetPhase(JobPhase.Night, 5);
        await port.WhenIdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, port.InFlight);
        Assert.Empty(port.Receive(0));
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
