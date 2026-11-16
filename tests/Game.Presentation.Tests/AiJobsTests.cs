using GlassWorld.Presentation.Ui;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Night;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1801 (Marcel W2243): the developer view lists what the AI works on now and what waits for the night.</summary>
public sealed class AiJobsTests
{
    [Fact]
    public void ARequestWithoutAFinalStateRunsAHeldOneWaitsAndAFinishedOneIsGone()
    {
        var log = new List<DecisionLogEntry>
        {
            new(DecisionLogKind.JobRequest, "j1", 10, "{\"kind\":\"invention\",\"inhabitant\":\"inh-000001\"}", null),
            new(DecisionLogKind.JobRequest, "j2", 20, "{\"kind\":\"image\",\"inhabitant\":null}", null),
            new(DecisionLogKind.JobResult, "j1", 30, "{}", null),
            new(DecisionLogKind.JobRequest, "j3", 40, "fingerprint-only", null),
        };
        var held = new[] { new HeldJob("j9", "dream", 1, "inh-000002", 35, 0, "{}") };
        var open = AiJobs.Open(log, held);

        Assert.Equal(["j3", "j2", "j9"], open.Select(j => j.RequestId));
        Assert.Equal(("?", (string?)null), (open[0].Kind, open[0].InhabitantId)); // only a fingerprint left
        Assert.Equal(("image", false), (open[1].Kind, open[1].Waiting));
        Assert.Equal(("dream", "inh-000002", true), (open[2].Kind, open[2].InhabitantId, open[2].Waiting));
    }

    [Fact]
    public void AHeldJobIsNotCountedAsRunningToo()
    {
        var log = new List<DecisionLogEntry> { new(DecisionLogKind.JobRequest, "j9", 5, "{\"kind\":\"dream\"}", null) };
        var open = AiJobs.Open(log, [new HeldJob("j9", "dream", 1, null, 5, 0, "{}")]);
        Assert.True(Assert.Single(open).Waiting);
    }

    [Fact]
    public void ADeciderRequestWithoutAnAnswerIsAIJobToo()
    {
        // Review GW2 #567: the decisions are AI work as well - a request without a response is listed with its inhabitant.
        var decisions = new List<DecisionLogEntry>
        {
            new(DecisionLogKind.Request, "d1", 50, "{\"inhabitant\":{\"id\":\"inh-000003\"}}", null),
            new(DecisionLogKind.Request, "d2", 51, "{\"inhabitant\":{\"id\":\"inh-000004\"}}", null),
            new(DecisionLogKind.Response, "d2", 60, "{}", null),
        };
        var open = AiJobs.Open([], [], decisions);
        var job = Assert.Single(open);
        Assert.Equal((AiJobs.Decide, "inh-000003", false), (job.Kind, job.InhabitantId, job.Waiting));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void EveryJobKindHasItsWordInBothLanguages(string locale)
    {
        var texts = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "i18n", locale + ".json")))
            .RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToHashSet(StringComparer.Ordinal);
        var kinds = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "contracts", "schemas", "common.schema.json")))
            .RootElement.GetProperty("$defs").GetProperty("JobKind").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!);
        Assert.All(kinds, kind => Assert.Contains("ui.debug.job_kind." + kind, texts));
        Assert.Contains("ui.debug.jobs", texts);
        Assert.Contains("ui.debug.job_kind." + AiJobs.Decide, texts);
    }
}
