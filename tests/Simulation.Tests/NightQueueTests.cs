using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Night;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1766 (A1568 step 2b, B2789, D1113 v3 4b): in the day mode night_only the jobs of the held kinds wait in the
/// save game's night queue and go out when the compute night begins - most important first, each inhabitant in
/// turn, at most the data's number per night, the rest moves up; back to "ai" sends all at once. The day mode
/// is a logged command; the queue is saved and hashed only while it holds something.
/// </summary>
public sealed class NightQueueTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gw-night-queue-" + Guid.NewGuid().ToString("N"));

    public NightQueueTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ByDayInNightOnlyAHeldKindWaitsAndGoesOutWhenTheNightBegins()
    {
        var (queue, port, clock, router) = Queue();
        queue.NightOnly = true;

        Submit(router, "j1", "process", 10, "inh-000001");
        Submit(router, "q1", "question", 10, "inh-000001");

        Assert.Equal(["q1"], port.Sent);
        Assert.Equal(1, queue.HeldFor("inh-000001"));
        Assert.True(queue.HasState);

        clock.Night = true;
        clock.Begins = true;
        queue.Advance(clock.World);

        Assert.Equal(["q1", "j1"], port.Sent);
        Assert.Empty(queue.Held);
        Assert.False(queue.HasState);
    }

    [Fact]
    public void InTheModeAiAndAtNightNothingIsHeld()
    {
        var (queue, port, clock, router) = Queue();
        Submit(router, "a", "invention", 10, "inh-000001");

        queue.NightOnly = true;
        clock.Night = true;
        Submit(router, "b", "invention", 10, "inh-000001");

        Assert.Equal(["a", "b"], port.Sent);
        Assert.Empty(queue.Held);
    }

    [Fact]
    public void ANightSendsTheMostImportantFirstEachInhabitantInTurnAtMostTheCapTheRestMovesUp()
    {
        var settings = new NightQueue { CapPerNight = 3, AgeBonusPerNight = 10 };
        foreach (var kind in Content.Value.NightQueue!.HeldKinds)
        {
            settings.HeldKinds.Add(kind);
        }

        var (queue, port, clock, router) = Queue(settings);
        queue.NightOnly = true;
        Submit(router, "a1", "process", 20, "inh-000001", tick: 1);
        Submit(router, "a2", "process", 20, "inh-000001", tick: 2);
        Submit(router, "b1", "process", 20, "inh-000002", tick: 3);
        Submit(router, "low", "milestone", 5, null, tick: 0);
        Submit(router, "top", "build_plan", 40, "inh-000002", tick: 4);

        clock.Night = true;
        clock.Begins = true;
        queue.Advance(clock.World);

        // top (40, inh-000002) first; then a 20 of inh-000001, which got none yet (a1); then both have one, so the
        // oldest 20 (a2 before b1).
        Assert.Equal(["top", "a1", "a2"], port.Sent);
        Assert.Equal(["b1", "low"], queue.Held.Select(h => h.RequestId));
        Assert.All(queue.Held, h => Assert.Equal(1, h.Nights));

        // The next night the rest goes; what waited a night counts its bonus (b1: 20 + 10 before low: 5 + 10).
        clock.Night = false;
        clock.Begins = false;
        queue.Advance(clock.World);
        Assert.Equal(3, port.Sent.Count);
        clock.Night = true;
        clock.Begins = true;
        queue.Advance(clock.World);
        Assert.Equal(["top", "a1", "a2", "b1", "low"], port.Sent);
    }

    [Fact]
    public void BackToAiWhatWaitsGoesOutAtOnce()
    {
        var (queue, port, clock, router) = Queue();
        queue.NightOnly = true;
        Submit(router, "x", "sound", 10, "inh-000001");
        Submit(router, "y", "image", 30, "inh-000002");
        Assert.Empty(port.Sent);

        queue.NightOnly = false;
        queue.Advance(clock.World);

        Assert.Equal(["y", "x"], port.Sent);
        Assert.Empty(queue.Held);
    }

    [Fact]
    public void TheDayModeIsALoggedCommandThatALoadedWorldKeeps()
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 21, new SilentDecisionPort(), "de", new RecordingPort());
        Assert.False(host.Decisions.Submit(new PlayerCommand(NightQueueSystem.DayModeCommand, null) { Text = "sometimes" }).Accepted);
        Assert.True(host.Decisions.Submit(new PlayerCommand(NightQueueSystem.DayModeCommand, null) { Text = NightQueueSystem.DayModeNightOnly }).Accepted);
        host.World.Step();
        Assert.True(host.NightQueue.NightOnly);

        host.NightQueue.Restore(new NightQueueState([new HeldJob("held-1", "process", 10, "inh-000001", 0, 1, "{\"kind\":\"process\"}")]));
        var path = Path.Combine(_directory, "queue.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loaded = StateCodec.Restore(Factory(new RecordingPort()), SaveGameStore.Load(path));

        Assert.True(loaded.NightQueue.NightOnly);
        Assert.Equal(host.NightQueue.Held, loaded.NightQueue.Held);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
    }

    [Fact]
    public void ByDayInNightOnlyATalkHasNoWordsNoDialogueJobGoesOut()
    {
        var port = new RecordingPort();
        var host = SimulationHost.CreateEmpty(ContentDirectory, 21, new SilentDecisionPort(), "de", port);
        Name(host);
        while (host.NightJobs.IsNightNow(host.World))
        {
            host.World.Step();
        }

        Assert.True(host.Decisions.Submit(new PlayerCommand(NightQueueSystem.DayModeCommand, null) { Text = NightQueueSystem.DayModeNightOnly }).Accepted);
        host.World.Step();
        host.Inhabitants.Report(new WorldEvent(InhabitantsSystem.ActionStarted, host.World.Tick, "inh-000001", "talk_to", "inh-000002"));
        Assert.DoesNotContain(port.Kinds, k => k == "dialogue");

        Assert.True(host.Decisions.Submit(new PlayerCommand(NightQueueSystem.DayModeCommand, null) { Text = NightQueueSystem.DayModeAi }).Accepted);
        host.World.Step();
        host.Inhabitants.Report(new WorldEvent(InhabitantsSystem.ActionStarted, host.World.Tick, "inh-000001", "talk_to", "inh-000002"));
        Assert.Contains(port.Kinds, k => k == "dialogue");
    }

    [Fact]
    public void TheHeldKindsAreJobKinds()
    {
        var kinds = Enum.GetValues<JobKind>().Select(k => k.ToString().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        Assert.All(Content.Value.NightQueue!.HeldKinds, k => Assert.Contains(k, kinds));
        Assert.DoesNotContain("dialogue", Content.Value.NightQueue.HeldKinds);
    }

    private static void Submit(JobRouter router, string id, string kind, int priority, string? inhabitant, long tick = 0)
    {
        var who = inhabitant is null ? "null" : $"{{\"id\":\"{inhabitant}\"}}";
        router.Rest.Submit(id, $"{{\"request_id\":\"{id}\",\"kind\":\"{kind}\",\"priority\":{priority},\"tick\":{tick},\"inhabitant\":{who}}}");
    }

    private static (NightQueueSystem Queue, RecordingPort Port, Clock Clock, JobRouter Router) Queue(NightQueue? settings = null)
    {
        var port = new RecordingPort();
        var host = SimulationHost.CreateEmpty(ContentDirectory, 21, new SilentDecisionPort(), "de");
        var clock = new Clock(host.World);
        var router = new JobRouter(port);
        var queue = new NightQueueSystem(settings ?? Content.Value.NightQueue, router, host.World, _ => clock.Night, _ => clock.Begins);
        return (queue, port, clock, router);
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
                new Position(id == "inh-000001" ? 0 : 100, 0),
                model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
                model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal),
                model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal),
                model.WillToLive.Start);
            host.Inhabitants.Add(inhabitant);
            inhabitant.Name = name;
            inhabitant.Activity = new Activity("idle", null, inhabitant.Position, 1_000_000);
        }

        host.Inhabitants.Get("inh-000001").Met.Add("inh-000002");
        host.Inhabitants.Get("inh-000002").Met.Add("inh-000001");
    }

    private static SimulationHostFactory Factory(IJobPort jobs)
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return new SimulationHostFactory(Content.Value, settings, new SilentDecisionPort(), "de", jobs);
    }

    private sealed class Clock(World world)
    {
        public World World { get; } = world;

        public bool Night { get; set; }

        public bool Begins { get; set; }
    }

    private sealed class SilentDecisionPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }

    private sealed class RecordingPort : IJobPort
    {
        public List<string> Sent { get; } = [];

        public List<string> Kinds { get; } = [];

        public void Submit(string requestId, string jobJson)
        {
            Sent.Add(requestId);
            using var doc = System.Text.Json.JsonDocument.Parse(jobJson);
            Kinds.Add(doc.RootElement.GetProperty("kind").GetString()!);
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];

        public void SetPhase(string phase, long tick, bool force = false)
        {
        }
    }
}
