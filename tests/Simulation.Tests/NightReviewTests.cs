using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1585 (B7 R18-R22, B2592, B2687): with the review data enabled the compute night sends one look back per
/// inhabitant instead of the memory job, and the core checks the answer again (GW1 #22713): ideas only on what the
/// inhabitant believes, intentions only with the data's actions; ideas and intentions are saved and hashed.
/// The shipped data keep it off (memory jobs as before, NightJobsTests).
/// </summary>
public sealed class NightReviewTests
{
    private static readonly string ShippedContent = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<string> OwnContent = new(CopyContentWithReviewOn);
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(OwnContent.Value));

    [Fact]
    public void TheShippedDataHaveTheLookBackOn()
    {
        // B2698 (TL, D971): switched on with the night questions that ask without a notice.
        Assert.True(ContentSet.Load(ShippedContent).Knowledge.Review!.Enabled);
    }

    [Fact]
    public void WithTheDataOnTheNightSendsALookBackWithTheFundbuchInsteadOfTheMemoryJob()
    {
        var (host, a, port) = World();

        RunIntoTheNight(host);

        var job = Assert.Single(port.Submitted);
        Assert.Empty(ContractValidator.Validate("generation-job", port.Json[job.RequestId]));
        Assert.Equal(JobKind.Review, job.Kind);
        var review = job.Payload.Review!;
        var settings = Content.Value.Knowledge.Review!;
        Assert.Equal(settings.IntentionActions, review.Actions);
        Assert.Contains(review.Knowledge, k => k.Subject.Id == "flint" && k.Property == "sharp");
        Assert.Equal(Content.Value.Knowledge.Memory!.CondenseAt, review.Events.Count);
        Assert.True(Assert.Single(host.NightJobs.MemoryJobs).Review);
    }

    [Fact]
    public void AGoodLookBackKeepsTheMemoryAnIdeaAnIntentionAndDropsWhatItDoesNotKnow()
    {
        var (host, a, port) = World();
        RunIntoTheNight(host);
        var job = Assert.Single(port.Submitted);
        var formative = job.Payload.Review!.Events.Where(e => e.Formative).Select(e => e.Tick).ToList();

        var answer = Status(job, new ReviewResult
        {
            Summary = "Found a sharp stone. Hazelnuts are good.",
            Kept = formative,
            Dream = new DreamResult { Text = new GeneratedText { Locale = Locale.De, Text = "Ich träume vom Strand." }, Tone = "calm", About = null },
            Ideas =
            [
                new ReviewIdeaResult { Id = null, Text = "The sharp stone might open a nut.", Roots = [Type("flint"), Type("hazelnut")], Thought = new GeneratedText { Locale = Locale.De, Text = "Was, wenn ich die Nuss mit dem Stein öffne?" } },
                new ReviewIdeaResult { Id = null, Text = "Fire from nothing.", Roots = [Type("fire")], Thought = null },
                new ReviewIdeaResult { Id = null, Text = "A known type posing as someone.", Roots = [new Subject { Kind = SubjectKind.Inhabitant, Id = "flint" }], Thought = null }, // review GW1 #328: roots by kind
            ],
            Intentions =
            [
                new ReviewIntention { Action = "show", About = Type("hazelnut"), Thought = new GeneratedText { Locale = Locale.De, Text = "Heute zeige ich die Nüsse." } },
                new ReviewIntention { Action = "fly", About = null, Thought = null },
            ],
            MorningMood = "joy",
        });
        port.Answer(job, answer);
        host.World.Step();

        Assert.Equal("Found a sharp stone. Hazelnuts are good.", a.Memory.Summary);
        var idea = Assert.Single(a.Ideas.Ideas);
        Assert.Equal([new IdeaRoot("type", "flint"), new IdeaRoot("type", "hazelnut")], idea.Roots);
        Assert.Equal(Content.Value.Knowledge.Review!.IdeaStart, idea.Strength);
        var intention = Assert.Single(a.Ideas.Intentions);
        Assert.Equal(("show", "type", "hazelnut"), (intention.Action, intention.AboutKind, intention.AboutId));
        Assert.Contains(host.Inventions.Log.Entries, e => e.RequestId == job.RequestId && e.Note is { } n && n.Contains("idea", StringComparison.Ordinal) && n.Contains("intention", StringComparison.Ordinal));
    }

    [Fact]
    public void IdeasAndIntentionsAreSavedAndHashedOnlyWhenThereAreAny()
    {
        var (host, a, _) = World();
        var before = host.World.StateHash();
        a.Ideas.Take(null, "A thought.", [new IdeaRoot("type", "flint")], 10, 0.3, 0.2, 8);
        a.Ideas.Intend([new Intention("show", "type", "flint", 10)]);
        Assert.NotEqual(before, host.World.StateHash());

        var restored = StateCodec.Restore(new SimulationHostFactory(host.Content, host.DeciderSettings, new SilentDecisionPort(), "de"), StateCodec.Capture(host));
        Assert.Equal(host.World.StateHash(), restored.World.StateHash());
        Assert.Equal(a.Ideas.Ideas, restored.Inhabitants.All.Single().Ideas.Ideas);
    }

    private static Subject Type(string id) => new() { Kind = SubjectKind.Type, Id = id };

    private static JobStatus Status(GenerationJob job, ReviewResult result) => new()
    {
        ContractVersion = ContractInfo.Version,
        JobId = job.RequestId,
        Tick = job.Tick,
        Kind = JobKind.Review,
        State = JobState.Done,
        Result = new JobResult { Review = result },
        Error = null,
        Provenance = new JobProvenance { Source = JobProvenanceSource.Mock, Model = "mock", PromptVersion = null, Seed = job.Seed, LatencyMs = 0, Attempts = 1 },
    };

    private static (SimulationHost Host, Inhabitant A, ScriptedJobPort Port) World()
    {
        var port = new ScriptedJobPort();
        var host = SimulationHost.CreateEmpty(OwnContent.Value, 21, new SilentDecisionPort(), "de", port);
        var model = Content.Value.InhabitantModel;
        var a = new Inhabitant(
            "inh-000001",
            "female",
            1,
            new Position(0, 0),
            model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
            model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal),
            model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal),
            model.WillToLive.Start);
        host.Inhabitants.Add(a);
        a.Activity = new Activity("idle", null, new Position(0, 0), 1_000_000);
        a.Beliefs.Set("flint", "sharp", true, 0.8, KnowledgeSources.Seen, null, 5);
        a.Beliefs.Set("hazelnut", "edible", true, 1.0, KnowledgeSources.Experienced, null, 6);
        var memory = Content.Value.Knowledge.Memory!;
        for (var i = 0; i < memory.CondenseAt; i++)
        {
            a.Memory.Remember(100 + (i * 3), "eat", InhabitantsSystem.AboutType, "hazelnut", formative: i == 0, memory.MaxEvents);
        }

        return (host, a, port);
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

    private static string CopyContentWithReviewOn()
    {
        var target = Path.Combine(Path.GetTempPath(), "glassworld-review-content-" + Path.GetRandomFileName());
        foreach (var file in Directory.EnumerateFiles(ShippedContent, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(ShippedContent, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        var path = Path.Combine(target, "knowledge", "settings.json");
        var settings = ContractSerializer.Deserialize<KnowledgeSettings>(File.ReadAllText(path));
        settings.Review!.Enabled = true;
        File.WriteAllText(path, ContractSerializer.Serialize(settings));
        return target;
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

        public List<GenerationJob> Submitted { get; } = [];

        public Dictionary<string, string> Json { get; } = new(StringComparer.Ordinal);

        public void Submit(string requestId, string jobJson)
        {
            Json[requestId] = jobJson;
            if (ContractSerializer.Deserialize<GenerationJob>(jobJson) is { Kind: JobKind.Memory or JobKind.Review or JobKind.Question } job)
            {
                Submitted.Add(job);
            }
        }

        public void Answer(GenerationJob job, JobStatus status) => _ready.Enqueue(new PortMessage(job.RequestId, ContractSerializer.Serialize(status), IsError: false));

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
