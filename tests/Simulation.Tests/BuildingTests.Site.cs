using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1373 (D827 R15, TL #26920): where to build - the core finds free places, the decider chooses among them; the nearest
/// stands until it answers, and without an answer it stays.
/// </summary>
public sealed partial class BuildingTests
{
    private static readonly Position[] Sites = [new(3, 0), new(6, 0), new(9, 0)];

    [Fact]
    public void TheDeciderChoosesAmongTheFreePlacesAndTheThingStandsThere()
    {
        var port = new SitePort(request => request.Options.Single(o => o.Place!.X == 6).OptionId);
        var (host, jobs, typeId) = Invented(22, decisions: port);
        host.Inventions.FindSites = (_, _) => Sites;
        AnswerPlan(jobs, Plan());
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, 3000);

        var request = Assert.Single(port.Sites);
        Assert.Equal(DecisionsSystem.BuildSite, request.Occasion);
        Assert.Equal([3.0, 6.0, 9.0], request.Options.Select(o => o.Place!.X).Order());
        Assert.All(request.Options, o => Assert.Equal(("build_here", (string?)null), (o.Action, o.Target)));
        Assert.Empty(ContractValidator.Validate("decision-request", ContractSerializer.Serialize(request)));

        var task = host.Inventions.Builds.Single();
        Assert.Equal((6.0, 0.0), (task.SiteX, task.SiteZ));
        Assert.True(host.Objects.TryGet(task.BuiltObjectId!, out var built));
        Assert.Equal((typeId, new Position(6, 0)), (built.TypeId, built.Position));
    }

    [Fact]
    public void WithoutAnAnswerTheNearestPlaceStays()
    {
        var port = new SitePort(_ => null);
        var (host, jobs, _) = Invented(22, decisions: port);
        host.Inventions.FindSites = (_, _) => Sites;
        AnswerPlan(jobs, Plan());
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, 3000);

        Assert.Single(port.Sites);
        var task = host.Inventions.Builds.Single();
        Assert.Equal((3.0, 0.0), (task.SiteX, task.SiteZ));
    }

    [Fact]
    public void OnTheIslandThePlacesAreDryWalkableReachableFreeAndApart()
    {
        var host = SimulationHost.CreateGeneratedWorld(Path.Combine(TestData.Root, "content"), 23, new MockDecisionPort(), "de", "start_island");
        var site = Content.Value.Inventions.Build.Settings.Site!;
        var map = host.Map!;
        var builder = host.Inhabitants.All.First();
        var places = map.FreeSites(builder.Position, site);

        Assert.InRange(places.Count, 1, site.Candidates);
        var start = map.CellOf(builder.Position)!.Value;
        var near = new List<WorldObject>();
        foreach (var place in places)
        {
            var cell = map.CellOf(place)!.Value;
            Assert.Equal(WaterKind.None, map.Map.Cell(cell.X, cell.Z).Water);
            Assert.Equal(map.AreaOf(start.X, start.Z), map.AreaOf(cell.X, cell.Z));
            near.Clear();
            host.Objects.Within(place, site.ClearanceM, near);
            Assert.Empty(near);
            Assert.True(Math.Sqrt(Math.Pow(place.X - builder.Position.X, 2) + Math.Pow(place.Z - builder.Position.Z, 2)) <= site.RadiusM);
        }

        for (var i = 0; i < places.Count; i++)
        {
            for (var j = i + 1; j < places.Count; j++)
            {
                Assert.True(Math.Sqrt(Math.Pow(places[i].X - places[j].X, 2) + Math.Pow(places[i].Z - places[j].Z, 2)) >= site.MinSpacingM);
            }
        }
    }

    /// <summary>A decider that answers build site questions as the test says and leaves the rest to the mock.</summary>
    private sealed class SitePort(Func<DecisionRequest, string?> choose) : IDecisionPort
    {
        private readonly MockDecisionPort _mock = new();
        private readonly Queue<PortMessage> _ready = new();

        public List<DecisionRequest> Sites { get; } = [];

        public void Send(string requestId, string requestJson)
        {
            var request = ContractSerializer.Deserialize<DecisionRequest>(requestJson);
            if (request.Occasion != DecisionsSystem.BuildSite)
            {
                _mock.Send(requestId, requestJson);
                return;
            }

            Sites.Add(request);
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

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            var ready = _ready.ToList();
            _ready.Clear();
            return [.. ready, .. _mock.Receive(tick)];
        }
    }
}
