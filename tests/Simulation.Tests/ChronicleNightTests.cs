using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B8 R10 (A1355): at nightfall the chronicle entries since the last nightfall go out as one chronicle job
/// (names as the chronicle shows them); the answer becomes the day's narrative after the core's checks,
/// anything else leaves the base lines alone. Saved, loaded and hashed like the other night state.
/// </summary>
public sealed class ChronicleNightTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gw-chronicle-night-" + Guid.NewGuid().ToString("N"));

    public ChronicleNightTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ADayWithoutEntriesSendsNothingAndStaysOutOfTheHash()
    {
        var (host, port) = World();
        RunIntoTheNight(host);

        Assert.Empty(port.Chronicle);
        Assert.False(host.ChronicleNight.HasState);
    }

    [Fact]
    public void AtNightfallTheDaysEntriesGoOutOnceWithTheChroniclesNames()
    {
        var (host, port) = World();
        Meet(host);
        host.Inhabitants.Report(new WorldEvent("first_use", host.World.Tick, "inh-000002", null, "obj-unknown"));

        RunIntoTheNight(host);

        var job = Assert.Single(port.Chronicle);
        Assert.Empty(ContractValidator.Validate("generation-job", port.Json[job.RequestId]));
        var settings = Content.Value.Chronicle.Narrative!;
        Assert.Equal((JobKind.Chronicle, settings.JobPriority, Locale.De, (string?)null), (job.Kind, job.Priority, job.DisplayLocale, job.Inhabitant));
        var payload = job.Payload.Chronicle!;
        Assert.Equal(settings.MaxChars, payload.MaxChars);
        Assert.Equal(1, payload.Day);
        Assert.Collection(
            payload.Entries,
            e => Assert.Equal(("first_meeting", "msg.chronicle.first_meeting", "Joro", "Rena"), (e.Kind, e.TextKey, e.Inhabitant.Label, e.Target?.Label)),
            e => Assert.Equal(("first_use", "Rena", (string?)null), (e.Kind, e.Inhabitant.Label, e.Target?.Label)));
        Assert.Contains(host.Inventions.Log.Entries, e => e.Kind == DecisionLogKind.JobRequest && e.RequestId == job.RequestId);
        Assert.True(host.ChronicleNight.HasState);

        // The next nightfall without new entries sends nothing again.
        host.World.Step();
        RunIntoTheNight(host);
        Assert.Single(port.Chronicle);
    }

    [Fact]
    public void AnAnswerThatPassesBecomesTheDaysNarrativeAndSurvivesSaveAndLoad()
    {
        var (host, port) = World();
        Meet(host);
        RunIntoTheNight(host);
        var job = Assert.Single(port.Chronicle);

        port.Answer(job, Told(job, "Am ersten Tag begegneten sich Joro und Rena."));
        host.World.Step();

        var told = Assert.Single(host.ChronicleNight.Narratives);
        Assert.Equal((1L, 1L, 1L, "de", "Am ersten Tag begegneten sich Joro und Rena."), (told.Day, told.FirstSeq, told.LastSeq, told.Locale, told.Text));
        Assert.Empty(host.ChronicleNight.Jobs);

        var path = Path.Combine(_directory, "chronicle.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loaded = StateCodec.Restore(Factory(new ScriptedJobPort()), SaveGameStore.Load(path));
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(host.ChronicleNight.Narratives, loaded.ChronicleNight.Narratives);
    }

    [Theory]
    [InlineData("too_long")]
    [InlineData("wrong_language")]
    [InlineData("none")]
    [InlineData("error")]
    public void WhatDoesNotPassLeavesTheBaseLinesAloneForGood(string answer)
    {
        var (host, port) = World();
        Meet(host);
        RunIntoTheNight(host);
        var job = Assert.Single(port.Chronicle);

        switch (answer)
        {
            case "too_long":
                // Longer than the job allows: the contract itself refuses it, the core discards it.
                port.AnswerUnchecked(job, Told(job, new string('a', job.Payload.Chronicle!.MaxChars + 1)));
                break;
            case "wrong_language":
                port.Answer(job, Told(job, "On the first day Joro and Rena met.", Locale.En));
                break;
            case "none":
                port.Answer(job, Told(job, null));
                break;
            default:
                port.AnswerError(job.RequestId, "{}");
                break;
        }

        host.World.Run(3);

        Assert.Empty(host.ChronicleNight.Narratives);
        Assert.Empty(host.ChronicleNight.Jobs);
        Assert.Single(port.Chronicle);
    }

    [Fact]
    public void WithoutTheAiNothingIsToldAndTheHashStaysAsBefore()
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 21, new SilentDecisionPort(), "de");
        Name(host);
        Meet(host);
        RunIntoTheNight(host);

        Assert.False(host.ChronicleNight.HasState);
    }

    /// <summary>Joro and Rena meet as the core lets them: each knows the other, and the meeting is reported.</summary>
    private static void Meet(SimulationHost host)
    {
        host.Inhabitants.Get("inh-000001").Met.Add("inh-000002");
        host.Inhabitants.Get("inh-000002").Met.Add("inh-000001");
        host.Inhabitants.Report(new WorldEvent("first_meeting", host.World.Tick, "inh-000001", null, "inh-000002"));
    }

    private static (SimulationHost Host, ScriptedJobPort Port) World()
    {
        var port = new ScriptedJobPort();
        var host = SimulationHost.CreateEmpty(ContentDirectory, 21, new SilentDecisionPort(), "de", port);
        Name(host);
        return (host, port);
    }

    private static void Name(SimulationHost host)
    {
        foreach (var (id, name) in new[] { ("inh-000001", "Joro"), ("inh-000002", "Rena") })
        {
            var model = Content.Value.InhabitantModel;
            var inhabitant = new Inhabitant(
                id,
                "female",
                1,
                new Position(id == "inh-000001" ? 0 : 400, 0),
                model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
                model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal),
                model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal),
                model.WillToLive.Start);
            host.Inhabitants.Add(inhabitant);
            inhabitant.Name = name;
            inhabitant.Activity = new Activity("idle", null, inhabitant.Position, 1_000_000);
        }
    }

    private static void RunIntoTheNight(SimulationHost host)
    {
        var limit = host.World.Tick + (2 * host.World.Calendar.TicksPerDay);
        while (!host.NightJobs.NightBegins(host.World))
        {
            Assert.True(host.World.Tick < limit, "no night began within two days");
            host.World.Step();
        }

        host.World.Step();
    }

    private static SimulationHostFactory Factory(IJobPort jobs)
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return new SimulationHostFactory(Content.Value, settings, new SilentDecisionPort(), "de", jobs);
    }

    /// <summary>The contract example of a day narrative, fitted to the job (test data).</summary>
    private static JobStatus Told(GenerationJob job, string? text, Locale locale = Locale.De)
    {
        var status = ContractSerializer.Deserialize<JobStatus>(File.ReadAllText(Path.Combine(TestData.Root, "contracts", "examples", "valid", "job-status.chronicle-day.json")));
        status.JobId = job.RequestId;
        status.Tick = job.Tick;
        status.Result!.Chronicle!.Narrative = text is null ? null : new GeneratedText { Locale = locale, Text = text };
        return status;
    }

    private sealed class SilentDecisionPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }

    private sealed class ScriptedJobPort : IJobPort
    {
        private readonly Queue<PortMessage> _ready = new();

        /// <summary>Chronicle jobs in the order they were sent; other kinds are left out.</summary>
        public List<GenerationJob> Chronicle { get; } = [];

        public Dictionary<string, string> Json { get; } = new(StringComparer.Ordinal);

        public void Submit(string requestId, string jobJson)
        {
            Json[requestId] = jobJson;
            if (ContractSerializer.Deserialize<GenerationJob>(jobJson) is { Kind: JobKind.Chronicle } job)
            {
                Chronicle.Add(job);
            }
        }

        public void Answer(GenerationJob job, JobStatus status) => _ready.Enqueue(new PortMessage(job.RequestId, ContractSerializer.Serialize(status), IsError: false));

        public void AnswerUnchecked(GenerationJob job, JobStatus status) => _ready.Enqueue(new PortMessage(job.RequestId, ContractSerializer.Serialize(status, validate: false), IsError: false));

        public void AnswerError(string requestId, string json) => _ready.Enqueue(new PortMessage(requestId, json, IsError: true));

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            var ready = _ready.ToList();
            _ready.Clear();
            return ready;
        }

        public void SetPhase(string phase, long tick, bool force = false)
        {
        }
    }
}
