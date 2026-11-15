using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1778 (B2814, Marcel W2204): an action that fails at the core becomes an open question - the situation (action,
/// kind of target, reason) goes to the decider with the usual options, the action rests on that target, once per kind
/// of failure; reasons that pass by themselves and events without a reason ask nothing.
/// </summary>
public sealed class FailureQuestionTests
{
    private static readonly Lazy<string> ContentCopy = new(CopyContentWithoutLeisure);
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private static readonly Lazy<DeciderSettings> Decider = new(() => ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json"))));

    [Fact]
    public void AFailedActionOpensOneQuestionWithItsSituationAndTheActionRestsOnThatTarget()
    {
        var (host, port, thing) = World();
        Fail(host, thing, InhabitantsSystem.TooHigh);
        host.World.Step();

        var question = Assert.Single(host.Decisions.Open, q => q.Kind == DecisionsSystem.ActionFailed);
        Assert.Equal((DecisionsSystem.ActionFailed, "failed:eat|apple|too_high"), (question.Kind, question.Subject));
        var (id, json) = Assert.Single(port.Sent, s => s.Id == question.RequestId);
        Assert.Equal(question.RequestId, id);
        Assert.Empty(ContractValidator.Validate("decision-request", json));
        var request = ContractSerializer.Deserialize<DecisionRequest>(json);
        Assert.Equal(DecisionsSystem.ActionFailed, request.Occasion);
        Assert.Equal(("eat", "apple", "too_high"), (request.Failure!.Action, request.Failure.TargetType, request.Failure.Reason));

        var joro = host.Inhabitants.Get("inh-000001");
        var rest = Assert.Single(joro.Resting);
        Assert.Equal(("eat", thing.Id), (rest.Action, rest.TargetId));
        Assert.Equal(host.World.Tick - 1 + (long)Math.Round(Decider.Value.Failures!.RestHours * host.World.Calendar.Settings.TicksPerHour), rest.Until);
    }

    [Fact]
    public void TheSameFailureAgainAsksNothingMoreAnotherReasonDoes()
    {
        var (host, _, thing) = World();
        Fail(host, thing, InhabitantsSystem.TooHigh);
        host.Inhabitants.Report(new WorldEvent(InhabitantsSystem.OutOfReach, host.World.Tick, "inh-000001", "eat", thing.Id) { TargetType = thing.TypeId, Detail = InhabitantsSystem.TooHigh });
        host.World.Step();
        Fail(host, thing, InhabitantsSystem.TooHigh);
        host.World.Step();
        Assert.Single(host.Decisions.Open, q => q.Kind == DecisionsSystem.ActionFailed);

        Fail(host, thing, InhabitantsSystem.TooSmooth);
        host.World.Step();
        Assert.Equal(2, host.Decisions.Open.Count(q => q.Kind == DecisionsSystem.ActionFailed));
    }

    [Theory]
    [InlineData(InhabitantsSystem.WokenRested)]
    [InlineData(InhabitantsSystem.KeptAwakeBy + "/hunger")]
    [InlineData(null)]
    public void AReasonThatPassesByItselfOrNoReasonAsksNothing(string? reason)
    {
        var (host, _, thing) = World();
        Fail(host, thing, reason);
        host.World.Step();

        Assert.DoesNotContain(host.Decisions.Open, q => q.Kind == DecisionsSystem.ActionFailed);
        Assert.Empty(host.Inhabitants.Get("inh-000001").Resting);
    }

    [Fact]
    public void AJammedWayRestsTheActionButAsksNothing()
    {
        // TL B2821: blocked rests (they turn to another target) but asks no question.
        var (host, _, thing) = World();
        Fail(host, thing, "blocked");
        host.World.Step();

        Assert.DoesNotContain(host.Decisions.Open, q => q.Kind == DecisionsSystem.ActionFailed);
        Assert.Single(host.Inhabitants.Get("inh-000001").Resting, r => r.Action == "eat" && r.TargetId == thing.Id);
    }

    [Fact]
    public void AFailureQuestionIsReadBackFromItsSubject()
    {
        var failure = DecisionsSystem.FailureOf("failed:talk_to|-|listener_asleep")!;
        Assert.Equal(("talk_to", (string?)null, "listener_asleep"), (failure.Action, failure.TargetType, failure.Reason));
        Assert.Null(DecisionsSystem.FailureOf("type:apple"));
    }

    private static void Fail(SimulationHost host, WorldObject thing, string? reason) =>
        host.Inhabitants.Report(new WorldEvent(InhabitantsSystem.ActionCancelled, host.World.Tick, "inh-000001", "eat", thing.Id) { Detail = reason });

    private static (SimulationHost Host, RecordingPort Port, WorldObject Thing) World()
    {
        var port = new RecordingPort();
        var host = SimulationHost.CreateEmpty(ContentCopy.Value, 21, port, "de");
        var model = Content.Value.InhabitantModel;
        var joro = new Inhabitant(
            "inh-000001",
            "female",
            1,
            new Position(0, 0),
            model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
            model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal),
            model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal),
            model.WillToLive.Start);
        host.Inhabitants.Add(joro);
        joro.Name = "Joro";
        joro.Activity = new Activity("idle", null, joro.Position, 1_000_000);
        var thing = host.Objects.Spawn("apple", new Position(1, 0));
        joro.Beliefs.Set("apple", "edible", true, 1.0);
        return (host, port, thing);
    }

    /// <summary>The shipped content without the free-moment question, so only the failure asks (it would take the room and the decider).</summary>
    private static string CopyContentWithoutLeisure()
    {
        var target = Path.Combine(Path.GetTempPath(), "glassworld-failure-content-" + Path.GetRandomFileName());
        foreach (var file in Directory.EnumerateFiles(ContentDirectory, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(ContentDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        var path = Path.Combine(target, "decider", "settings.json");
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(path));
        foreach (var trigger in settings.Triggers.Where(t => t.Kind == TriggerKind.Leisure))
        {
            trigger.Enabled = false;
        }

        File.WriteAllText(path, ContractSerializer.Serialize(settings));
        return target;
    }

    private sealed class RecordingPort : IDecisionPort
    {
        public List<(string Id, string Json)> Sent { get; } = [];

        public void Send(string requestId, string requestJson) => Sent.Add((requestId, requestJson));

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
