using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1373 step 2 (D827 R15/R17c, D850, TL B2658): when the help does not come, the stuck builder asks the decider
/// what to do - only with the ways the core leaves open - and the core does what was chosen.
/// </summary>
public sealed partial class BuildingTests
{
    private static BuildEscalation Escalation => ContractSerializer.Deserialize<BuildSettings>(File.ReadAllText(Path.Combine(EagerContent.Value, "build", "settings.json"))).Escalation!;

    [Fact]
    public void AStuckBuilderAsksWithTheCoresOptionsOnlyAndAtMostMaxTimes()
    {
        // Every answer is one the core does not know: the question closes as failed and the next comes later.
        var decider = new EscalationPort(_ => "o99");
        var (host, port, typeId) = Stuck(decider);
        var events = Record(host);
        var perHour = host.World.Calendar.Settings.TicksPerHour;
        var settings = ContractSerializer.Deserialize<BuildSettings>(File.ReadAllText(Path.Combine(EagerContent.Value, "build", "settings.json")));
        RunUntil(host, () => host.Inventions.Builds.Single().GaveUp, (int)(settings.GiveUpAfterHours!.Value * perHour * 3));

        Assert.Equal(Escalation.Max, decider.Stuck.Count);
        Assert.Equal(Escalation.Max, host.Inventions.Builds.Single().Escalations);
        var allowed = new[] { Escalation.UseInsteadAction, Escalation.SmallerAction, Escalation.GiveUpAction };
        for (var i = 0; i < decider.Stuck.Count; i++)
        {
            var request = decider.Stuck[i];
            var building = request.Building!;
            Assert.Equal((i + 1, Escalation.Max), (building.Escalation, building.MaxEscalations));
            Assert.Equal(host.Types.TrueProperties(NearbyTypes[^1]).Order(StringComparer.Ordinal), building.MissingProperties);
            Assert.Equal(Approved(port.Submitted.First(j => j.Kind == JobKind.Invention)).Result!.Invention!.Invention!.Name.De, building.Name.De);
            Assert.All(request.Options, o => Assert.Contains(o.Action, allowed));
            Assert.Contains(request.Options, o => o.Action == Escalation.GiveUpAction);
            Assert.Contains(request.Options, o => o.Action == Escalation.SmallerAction);
        }

        // In the end it gives up after the data's hours, as without asking.
        var failed = Assert.Single(events, e => e.Kind == InventionSystem.BuildFailed);
        Assert.Equal((typeId, InventionSystem.NoMaterial), (failed.TargetId, failed.Detail));
    }

    [Fact]
    public void TheStuckOptionsAreShuffledLikeEveryQuestion()
    {
        // D1096 #7 (D1025): the position of an option says nothing - the order comes from the world and the question id.
        var decider = new EscalationPort(_ => null);
        var (host, _, typeId) = Stuck(decider);
        var builder = host.Inhabitants.All.Single();
        RunUntil(host, () => decider.Stuck.Count > 0, (int)(48 * host.World.Calendar.Settings.TicksPerHour));
        var request = decider.Stuck.First();
        var questionId = host.Decisions.OpenQuestionIds(builder.Id).Single();
        var expected = host.Inventions.EscalationOptionsOf(builder.Id, typeId).Take(DecisionsSystem.MaxOptions).ToList();
        DecisionsSystem.Shuffle(expected, DecisionsSystem.OrderSeed(host.World.Seed, questionId));
        Assert.Equal(expected.Select(o => (o.Action, o.TargetId)), request.Options.Select(o => (o.Action, o.Target)));
    }

    [Fact]
    public void GivingUpByChoiceEndsTheBuildingAndFrustrates()
    {
        var decider = new EscalationPort(r => r.Options.Single(o => o.Action == Escalation.GiveUpAction).OptionId);
        var (host, _, typeId) = Stuck(decider);
        var events = Record(host);
        var builder = host.Inhabitants.All.Single();
        RunUntil(host, () => host.Inventions.Builds.Single().GaveUp, (int)(120 * host.World.Calendar.Settings.TicksPerHour));
        var failed = Assert.Single(events, e => e.Kind == InventionSystem.BuildFailed);
        Assert.Equal((typeId, InventionSystem.GaveUpByChoice), (failed.TargetId, failed.Detail));
        Assert.Single(decider.Stuck);
        Assert.True(builder.Moods["frustration"] > Content.Value.InhabitantModel.Moods.Single(m => m.Id == "frustration").Baseline);
    }

    [Fact]
    public void FinishingSmallerLeavesTheThingAsFarAsItIsBuilt()
    {
        var decider = new EscalationPort(r => r.Options.Single(o => o.Action == Escalation.SmallerAction).OptionId);
        var (host, _, typeId) = Stuck(decider);
        var events = Record(host);
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, (int)(120 * host.World.Calendar.Settings.TicksPerHour));
        var task = host.Inventions.Builds.Single();
        Assert.True(task.Smaller);
        Assert.Equal(1, task.StepsDone);
        var built = Assert.Single(events, e => e.Kind == InventionSystem.Built);
        Assert.Equal((task.BuiltObjectId, InventionSystem.FinishedSmaller), (built.TargetId, built.Detail));
        Assert.True(host.Objects.TryGet(task.BuiltObjectId!, out var thing));
        Assert.Equal(typeId, thing.TypeId);

        // Saved and loaded: the same hash.
        var path = Path.Combine(_directory, "smaller.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        Assert.Equal(host.World.StateHash(), Restore(path, host).World.StateHash());
    }

    [Fact]
    public void AThingTakenInsteadIsJudgedByTheWorldAndTheBuilderLearns()
    {
        // Two kinds of thing in sight the builder believes would do: one truly has what is missing, one does not.
        string? choose = null;
        var decider = new EscalationPort(r => r.Options.FirstOrDefault(o => o.Action == Escalation.UseInsteadAction && o.Target == choose)?.OptionId);
        var (host, _, typeId) = Stuck(decider);
        var missing = host.Types.TrueProperties(NearbyTypes[^1]).Order(StringComparer.Ordinal).ToList();
        host.Types.Register("inv_00000000f175", missing);
        host.Types.Register("inv_00000000100c", missing.Skip(1));
        var fits = host.Objects.Spawn("inv_00000000f175", new Position(1, 1));
        var looks = host.Objects.Spawn("inv_00000000100c", new Position(1, -1));
        var builder = host.Inhabitants.All.Single();
        foreach (var p in missing)
        {
            builder.Beliefs.Set("inv_00000000f175", p, true, 0.6, KnowledgeSources.Guessed, null, host.World.Tick);
            builder.Beliefs.Set("inv_00000000100c", p, true, 0.6, KnowledgeSources.Guessed, null, host.World.Tick);
        }

        var events = Record(host);
        choose = looks.Id;
        RunUntil(host, () => events.Any(e => e.Kind == InventionSystem.SubstituteFailed), (int)(120 * host.World.Calendar.Settings.TicksPerHour));
        var failed = Assert.Single(events, e => e.Kind == InventionSystem.SubstituteFailed);
        Assert.Equal((typeId, "inv_00000000100c"), (failed.TargetId, failed.Detail));
        Assert.True(builder.Beliefs.TryGet("inv_00000000100c", missing[0], out var learned));
        Assert.Equal((false, KnowledgeSources.Experienced), (learned.Value, learned.Source));
        Assert.Null(host.Inventions.Builds.Single().Substitutes);

        // The next question no longer offers what it now knows does not do; the one that does is taken and built in.
        choose = fits.Id;
        RunUntil(host, () => events.Any(e => e.Kind == InventionSystem.SubstituteTaken), (int)(120 * host.World.Calendar.Settings.TicksPerHour));
        Assert.DoesNotContain(decider.Stuck[^1].Options, o => o.Target == looks.Id);
        Assert.Equal("inv_00000000f175", host.Inventions.Builds.Single().Substitutes![NearbyTypes[^1]]);
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, (int)(24 * host.World.Calendar.Settings.TicksPerHour));
        Assert.False(host.Inventions.Builds.Single().Smaller);
        Assert.False(host.Objects.TryGet(fits.Id, out _));
    }

    /// <summary>An invention whose second step's material is nowhere: the builder works in the first and gets stuck.</summary>
    private static (SimulationHost Host, ScriptedJobPort Port, string TypeId) Stuck(EscalationPort decider)
    {
        var (host, port, typeId) = Invented(23, decisions: decider);
        host.Objects.Remove(host.Objects.All.Single(o => o.TypeId == NearbyTypes[^1]).Id);
        AnswerPlan(port, Plan());
        RunUntil(host, () => host.Inventions.Builds.Single().WaitingSinceTick is not null, 3000);
        return (host, port, typeId);
    }

    /// <summary>
    /// The decider for these tests: a stuck building's question is recorded and answered by <c>choose</c> (null = no
    /// answer); every other request as the mock answers it.
    /// </summary>
    private sealed class EscalationPort(Func<DecisionRequest, string?> choose) : IDecisionPort
    {
        private readonly MockDecisionPort _mock = new();
        private readonly Queue<PortMessage> _ready = new();

        public List<DecisionRequest> Stuck { get; } = [];

        public void Send(string requestId, string requestJson)
        {
            var request = ContractSerializer.Deserialize<DecisionRequest>(requestJson);
            if (request.Building is null)
            {
                _mock.Send(requestId, requestJson);
                return;
            }

            Stuck.Add(request);
            if (choose(request) is not { } optionId)
            {
                return;
            }

            var response = new DecisionResponse
            {
                ContractVersion = request.ContractVersion,
                RequestId = request.RequestId,
                Tick = request.Tick,
                OptionId = optionId,
                Rationale = "test",
                Meta = new ResponseMeta { Source = ResponseMetaSource.Mock, Model = "test", LatencyMs = 0, Attempts = 1 },
            };
            _ready.Enqueue(new PortMessage(requestId, ContractSerializer.Serialize(response), IsError: false));
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [.. _mock.Receive(tick), .. Drain()];

        private List<PortMessage> Drain()
        {
            var messages = _ready.ToList();
            _ready.Clear();
            return messages;
        }
    }
}
