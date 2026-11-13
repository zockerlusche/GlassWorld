using System.Text.Json;
using System.Text.Json.Nodes;
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
/// B2687 K2 (A1451 part 2 = D471 P3, plan D932): what comes of two things put together that no combination
/// rule fits - the data's processing rules, else the world's judged rule for exactly this try, else one
/// job to the judge (v2: process null + candidates) whose answer becomes world truth for good.
/// </summary>
public sealed class ProcessTests
{
    private const string Combine = "combine";
    private const string Actor = "inh-000001";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    [Fact]
    public void TheSignatureMatchesTheContractsTestVectors()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData.Root, "contracts", "testvectors", "process-signature.json")));
        var vectors = file.RootElement.GetProperty("vectors").EnumerateArray().ToList();
        Assert.NotEmpty(vectors);
        foreach (var vector in vectors)
        {
            var signature = vector.GetProperty("signature");
            var process = signature.GetProperty("process").GetString()!;
            var inputs = signature.GetProperty("inputs").EnumerateArray().Select(i => i.EnumerateArray().Select(p => p.GetString()!).ToList()).ToList();
            var tool = signature.GetProperty("tool").ValueKind == JsonValueKind.Null ? null : signature.GetProperty("tool").EnumerateArray().Select(p => p.GetString()!).ToList();
            var heat = signature.GetProperty("heat_level").ValueKind == JsonValueKind.Null ? null : signature.GetProperty("heat_level").GetString();
            Assert.Equal(vector.GetProperty("canonical").GetString(), ProcessSignatures.Canonical(process, inputs, tool, heat));
            Assert.Equal(vector.GetProperty("rule_id").GetString(), ProcessSignatures.RuleId(process, inputs, tool, heat));
        }
    }

    [Fact]
    public void AStoneStruckWithAStoneGetsAnEdgeAndStaysTheSameThing()
    {
        var (host, events, _) = World(port: null);
        var stone = host.Objects.Spawn("pyrite", new Position(1, 0));
        var hammer = host.Objects.Spawn("pyrite", new Position(1.5, 0));

        RunCombine(host, stone, hammer);
        Assert.True(host.Objects.TryGet(stone.Id, out var changed));
        Assert.NotEqual("pyrite", changed.TypeId);
        Assert.Equal("pyrite", host.Processes!.RootOf(changed.TypeId));
        Assert.Contains("sharp", (IReadOnlySet<string>)host.Types.TrueProperties(changed.TypeId));
        Assert.Equal(host.Types.TrueProperties("pyrite").Count + 1, host.Types.TrueProperties(changed.TypeId).Count);
        Assert.Equal("strike_hard_stone_to_edge", Assert.Single(events, e => e.Kind == InhabitantsSystem.Combined).Detail);
        Assert.True(host.Inhabitants.Get(Actor).Beliefs.TryGet(changed.TypeId, "sharp", out var sharp) && sharp.Value);

        // The same change of the same type is one variant: a second stone becomes the same type.
        var other = host.Objects.Spawn("pyrite", new Position(1, 0.5));
        RunCombine(host, other, hammer);
        Assert.True(host.Objects.TryGet(other.Id, out var second));
        Assert.Equal(changed.TypeId, second.TypeId);
    }

    [Fact]
    public void AnUnknownTryIsJudgedOnceAndTheAnswerBecomesWorldTruth()
    {
        var port = new ScriptedJobPort();
        var (host, events, _) = World(port);
        var flint = host.Objects.Spawn("flint", new Position(1, 0));
        var grass = host.Objects.Spawn("dry_grass", new Position(1.5, 0));

        RunCombine(host, grass, flint);
        var job = Assert.Single(port.Submitted);
        Assert.Equal(JobKind.Process, job.Kind);
        var payload = job.Payload.Process!;
        Assert.Null(payload.Process);
        Assert.Equal(ProcessSignatures.CombineTrial, payload.Signature.Process);
        Assert.Contains("split", payload.Candidates!);
        Assert.DoesNotContain(events, e => e.Kind == InhabitantsSystem.Combined);

        // While it is judged, the same try asks nobody again.
        RunCombine(host, grass, flint);
        Assert.Single(port.Submitted);

        var id = Assert.Single(host.Processes!.Pending).Id;
        port.Answer(job, Feasible(job, id, "split", Props(host, grass), Props(host, flint), add: "building_material"));
        host.World.Run(2);
        var judged = Assert.Single(host.Processes.Rules);
        Assert.True(judged.Feasible);
        Assert.Empty(host.Processes.Pending);

        // From now on the world knows: the grass becomes building material, without asking.
        RunCombine(host, grass, flint);
        Assert.Single(port.Submitted);
        Assert.True(host.Objects.TryGet(grass.Id, out var changed));
        Assert.Contains("building_material", (IReadOnlySet<string>)host.Types.TrueProperties(changed.TypeId));
        Assert.Equal(id, Assert.Single(events, e => e.Kind == InhabitantsSystem.Combined).Detail);
    }

    [Fact]
    public void NothingHappensIsWorldTruthTooAndAnAnswerThatDoesNotFitIsRefused()
    {
        var port = new ScriptedJobPort();
        var (host, _, _) = World(port);
        var flint = host.Objects.Spawn("flint", new Position(1, 0));
        var grass = host.Objects.Spawn("dry_grass", new Position(1.5, 0));

        RunCombine(host, grass, flint);
        var job = Assert.Single(port.Submitted);
        var id = Assert.Single(host.Processes!.Pending).Id;

        // A candidate the core did not offer: refused, kept as 'nothing happens', never asked again.
        port.Answer(job, Feasible(job, id, "melt", Props(host, grass), Props(host, flint), add: "building_material"));
        host.World.Run(2);
        Assert.False(Assert.Single(host.Processes.Rules).Feasible);
        Assert.Contains(host.Inventions.Log.Entries, e => e.Kind == DecisionLogKind.JobResult && e.Note!.Contains("not_a_candidate", StringComparison.Ordinal));
        RunCombine(host, grass, flint);
        Assert.Single(port.Submitted);
        Assert.True(host.Objects.TryGet(grass.Id, out var same));
        Assert.Equal("dry_grass", same.TypeId);
    }

    [Fact]
    public void AnErrorIsNoAnswerALaterTryAsksAgain()
    {
        var port = new ScriptedJobPort();
        var (host, _, _) = World(port);
        var flint = host.Objects.Spawn("flint", new Position(1, 0));
        var grass = host.Objects.Spawn("dry_grass", new Position(1.5, 0));

        RunCombine(host, grass, flint);
        port.AnswerError(port.Submitted[0].RequestId, "{\"code\":\"bad_request\",\"message\":\"combine trial needs judge v2\",\"retryable\":false}");
        host.World.Run(2);
        Assert.Empty(host.Processes!.Rules);
        Assert.Empty(host.Processes.Pending);
        RunCombine(host, grass, flint);
        Assert.Equal(2, port.Submitted.Count);
    }

    [Fact]
    public void JudgedRulesAndVariantsSurviveSavingAndLoadingWithTheSameHash()
    {
        var port = new ScriptedJobPort();
        var (host, _, _) = World(port);
        var stone = host.Objects.Spawn("pyrite", new Position(1, 0));
        var hammer = host.Objects.Spawn("pyrite", new Position(1.5, 0));
        var flint = host.Objects.Spawn("flint", new Position(1, 0.5));
        var grass = host.Objects.Spawn("dry_grass", new Position(1.2, 0.5));
        RunCombine(host, stone, hammer);
        RunCombine(host, grass, flint);
        var job = Assert.Single(port.Submitted);
        port.Answer(job, Feasible(job, Assert.Single(host.Processes!.Pending).Id, "split", Props(host, grass), Props(host, flint), add: "building_material"));
        host.World.Run(2);
        Assert.True(host.Objects.TryGet(stone.Id, out var changed));

        var path = Path.Combine(Path.GetTempPath(), "gw-process-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var loaded = StateCodec.Restore(Factory(new ReplayJobPort(host.Inventions.Log)), SaveGameStore.Load(path));
            Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
            Assert.Equal("pyrite", loaded.Processes!.RootOf(changed.TypeId));
            Assert.Equal(host.Types.TrueProperties(changed.TypeId).Order(), loaded.Types.TrueProperties(changed.TypeId).Order());
            Assert.Single(loaded.Processes.Rules);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WorldsThatNeverProcessKeepTheirHash()
    {
        var (host, _, _) = World(new ScriptedJobPort());
        Assert.False(host.Processes!.HasState);
        var before = host.World.StateHash();
        host.World.Run(5);
        Assert.False(host.Processes.HasState);
        Assert.NotEqual(before, string.Empty);
    }

    private static string[] Props(SimulationHost host, WorldObject thing) => [.. host.Types.TrueProperties(thing.TypeId).Order(StringComparer.Ordinal)];

    /// <summary>A judge's answer in the contract's form, built from the valid example.</summary>
    private static JobStatus Feasible(GenerationJob job, string ruleId, string process, string[] changing, string[] other, string add)
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(TestData.Root, "contracts", "examples", "valid", "job-status.process-feasible.json")))!;
        node["job_id"] = job.RequestId;
        node["tick"] = job.Tick;
        var rule = node["result"]!["process"]!["rule"]!;
        rule["id"] = ruleId;
        rule["process"] = process;
        rule["inputs"] = new JsonArray(Condition(changing), Condition(other));
        rule["tool"] = null;
        rule["heat_min"] = null;
        rule["tech_level"] = "tool";
        rule["result"] = new JsonObject
        {
            ["add_properties"] = new JsonArray(add),
            ["remove_properties"] = new JsonArray(),
            ["produces"] = null,
            ["consumes"] = new JsonArray(),
        };
        return ContractSerializer.Deserialize<JobStatus>(node.ToJsonString());

        static JsonObject Condition(string[] all) => new()
        {
            ["properties_all"] = new JsonArray([.. all.Select(p => (JsonNode)JsonValue.Create(p)!)]),
            ["properties_none"] = new JsonArray(),
        };
    }

    private static (SimulationHost Host, List<WorldEvent> Events, IJobPort? Port) World(IJobPort? port)
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 13, new MockDecisionPort(), "de", port);
        var model = Content.Value.InhabitantModel;
        var needs = model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal);
        var moods = model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal);
        var traits = model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal);
        host.Inhabitants.Add(new Inhabitant(Actor, "female", 1, new Position(0, 0), needs, moods, traits, model.WillToLive.Start));
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        return (host, events, port);
    }

    private static void RunCombine(SimulationHost host, WorldObject target, WorldObject partner)
    {
        var inhabitant = host.Inhabitants.Get(Actor);

        // Each try on its own: the repetition guard (W1696) would let a try without effect rest.
        inhabitant.Resting.Clear();
        inhabitant.Repeat = null;
        inhabitant.Activity = new Activity(Combine, target.Id, target.Position, null, Partners: [partner.Id]);
        for (var i = 0; i < 2000 && inhabitant.Activity is { Action: Combine }; i++)
        {
            host.World.Run(1);
        }

        Assert.False(inhabitant.Activity is { Action: Combine }, "the try did not end");
    }

    private static SimulationHostFactory Factory(IJobPort jobs)
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new MockDecisionPort(), "de", jobs);
    }

    private sealed class ScriptedJobPort : IJobPort
    {
        private readonly Queue<PortMessage> _ready = new();

        public List<GenerationJob> Submitted { get; } = [];

        public void Submit(string requestId, string jobJson) => Submitted.Add(ContractSerializer.Deserialize<GenerationJob>(jobJson));

        public void Answer(GenerationJob job, JobStatus status) => _ready.Enqueue(new PortMessage(job.RequestId, ContractSerializer.Serialize(status), IsError: false));

        public void AnswerError(string requestId, string json) => _ready.Enqueue(new PortMessage(requestId, json, IsError: true));

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            var ready = _ready.ToList();
            _ready.Clear();
            return ready;
        }
    }
}
