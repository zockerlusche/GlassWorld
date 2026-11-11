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
/// Marcel W1698: every inhabitant has an own name from the data list; who knows whose name is per-observer
/// state. The naming action (data: knowledge.naming) is offered only while the asker does not know the
/// other's name; both learn each other's names unless the other is busy with an urgent need.
/// </summary>
public sealed class NamingTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);
    private static readonly KnowledgeNaming Naming = Content.Knowledge.Naming!;
    private static readonly DeciderSettings Settings = ContractSerializer.Deserialize<DeciderSettings>(
        File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gw-naming-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void StartInhabitantsGetDistinctProperNamesFromTheDataTheSameForTheSameSeed()
    {
        var first = NewWorld(5, new SilentPort()).Inhabitants.All.Select(i => i.Name).ToList();
        var again = NewWorld(5, new SilentPort()).Inhabitants.All.Select(i => i.Name).ToList();

        Assert.Equal(first, again);
        Assert.Equal(first.Count, first.Distinct().Count());
        Assert.All(first, n => Assert.Contains(n, Content.InhabitantNames));
        Assert.All(first, n => Assert.Matches("^[A-Z][a-z]{1,15}$", n!));
        Assert.All(NewWorld(5, new SilentPort()).Inhabitants.All, i => Assert.Empty(i.KnownNames));
    }

    [Fact]
    public void TheRequestCarriesTheOwnNameAndOffersIntroduceOnlyTowardsSomeoneWhoseNameIsUnknown()
    {
        var unknown = AskOnce(knows: false);
        Assert.Empty(ContractValidator.Validate("decision-request", ContractSerializer.Serialize(unknown.Request)));
        Assert.Equal(unknown.Asker.Name, unknown.Request.Inhabitant.Name);
        var seen = Assert.Single(unknown.Request.Perception.Inhabitants, i => i.Id == unknown.Other.Id);
        Assert.Null(seen.Called);
        Assert.Contains(unknown.Request.Options, o => o.Action == Naming.Action && o.Target == unknown.Other.Id);

        var known = AskOnce(knows: true);
        seen = Assert.Single(known.Request.Perception.Inhabitants, i => i.Id == known.Other.Id);
        Assert.Equal(known.Other.Name, seen.Called?.Name);
        Assert.Null(seen.Called?.Label);
        Assert.DoesNotContain(known.Request.Options, o => o.Action == Naming.Action);
    }

    [Fact]
    public void IntroducingTeachesBothNamesWithOneEventPerLearner()
    {
        var (world, inhabitants, events) = Meeting();
        var asker = inhabitants.Get("inh-000001");
        var other = inhabitants.Get("inh-000002");

        Introduce(world, asker, other);

        Assert.Contains(other.Id, asker.KnownNames);
        Assert.Contains(asker.Id, other.KnownNames);
        var learned = events.Where(e => e.Kind == InhabitantsSystem.NameLearned).ToList();
        Assert.Equal(2, learned.Count);
        Assert.Contains(learned, e => e.InhabitantId == asker.Id && e.TargetId == other.Id);
        Assert.Contains(learned, e => e.InhabitantId == other.Id && e.TargetId == asker.Id);
        Assert.False(InhabitantsSystem.HasPurpose(Content.Knowledge, asker, other, Naming.Action));
    }

    [Fact]
    public void AnOtherBusyWithAnUrgentNeedOnlyLearnsTheAskersName()
    {
        var (world, inhabitants, events) = Meeting();
        var asker = inhabitants.Get("inh-000001");
        var other = inhabitants.Get("inh-000002");
        var need = other.Needs.Keys.First();
        other.Needs[need] = Naming.BusyNeedBelow / 4;

        Introduce(world, asker, other);

        Assert.Contains(asker.Id, other.KnownNames);
        Assert.DoesNotContain(other.Id, asker.KnownNames);
        var learned = Assert.Single(events, e => e.Kind == InhabitantsSystem.NameLearned);
        Assert.Equal(other.Id, learned.InhabitantId);
        Assert.True(InhabitantsSystem.HasPurpose(Content.Knowledge, asker, other, Naming.Action));
    }

    [Fact]
    public void NamesAndKnownNamesSurviveSaveAndLoad()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 11, new MockDecisionPort(), "de");
        host.World.Run(10);
        var a = host.Inhabitants.All.First();
        var b = host.Inhabitants.All.Last();
        Assert.DoesNotContain(b.Id, a.KnownNames);
        a.KnownNames.Add(b.Id);
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "world.sqlite");
        SaveGameStore.Save(path, StateCodec.Capture(host));

        var loaded = StateCodec.Restore(Factory(), SaveGameStore.Load(path));

        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(host.Inhabitants.All.Select(i => i.Name), loaded.Inhabitants.All.Select(i => i.Name));
        Assert.Contains(b.Id, loaded.Inhabitants.Get(a.Id).KnownNames);
        Assert.Equal(a.KnownNames, loaded.Inhabitants.Get(a.Id).KnownNames);
    }

    [Fact]
    public void ASaveGameFromBeforeNamesLoadsWithNamesFromTheSeedAndNobodyKnowsAName()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 11, new MockDecisionPort(), "de");
        host.World.Run(10);
        var current = StateCodec.Capture(host);
        var old = current with { Inhabitants = [.. current.Inhabitants.Select(i => i with { Name = null, KnownNames = null })], NamesSaved = false };
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "old.sqlite");
        SaveGameStore.Save(path, old);
        var stored = SaveGameStore.Load(path);
        Assert.False(stored.NamesSaved);

        var loaded = StateCodec.Restore(Factory(), stored);

        Assert.Equal(host.Inhabitants.All.Select(i => i.Name), loaded.Inhabitants.All.Select(i => i.Name));
        Assert.All(loaded.Inhabitants.All, i => Assert.Empty(i.KnownNames));
        Assert.True(StateCodec.Capture(loaded).NamesSaved);
    }

    /// <summary>
    /// Promise from the #150 review: the rule checker ignores name_text and gossip unless the option they
    /// belong to (name_person, gossip) was chosen - here another option is chosen, and the answer acts exactly
    /// as the same answer without them.
    /// </summary>
    [Fact]
    public void NameTextAndGossipAreIgnoredUnlessTheirOptionWasChosen()
    {
        var plain = AnswerFirstOption(withExtras: false);
        var extras = AnswerFirstOption(withExtras: true);

        Assert.DoesNotContain(extras.Request.Options, o => o.Action is "name_person" or "gossip");
        Assert.Contains(extras.Log, e => e.Kind == DecisionLogKind.Response);
        Assert.DoesNotContain(extras.Log, e => e.Kind is DecisionLogKind.Discarded or DecisionLogKind.Error);
        Assert.Equal(plain.Decided, extras.Decided);
        Assert.Equal(plain.Names, extras.Names);
        Assert.All(extras.Known, Assert.Empty);
    }

    private static (DecisionRequest Request, Activity? Decided, List<string?> Names, List<string[]> Known, IReadOnlyList<DecisionLogEntry> Log) AnswerFirstOption(bool withExtras)
    {
        DecisionRequest? asked = null;
        var port = new ScriptedPort(request =>
        {
            asked ??= request;
            var response = new DecisionResponse
            {
                ContractVersion = ContractInfo.Version,
                RequestId = request.RequestId,
                Tick = request.Tick,
                OptionId = request.Options.OrderBy(o => o.OptionId, StringComparer.Ordinal).First(o => o.Action != Naming.Action).OptionId,
                Rationale = "x",
                Meta = new ResponseMeta { Source = ResponseMetaSource.Mock, Model = "mock", LatencyMs = 0, Attempts = 1 },
            };
            if (withExtras)
            {
                response.NameText = "Bob";
                response.Gossip = new Gossip { About = "inh-000002", Property = "poisonous", Value = true };
            }

            return ContractSerializer.Serialize(response);
        });
        var w = NewWorld(3, port);
        w.Objects.Spawn("blue_berry", new Position(2, 0));
        w.World.Run(3);
        var asker = w.Inhabitants.Get("inh-000001");
        return (asked!, asker.Activity ?? asker.Planned, [.. w.Inhabitants.All.Select(i => i.Name)], [.. w.Inhabitants.All.Select(i => i.KnownNames.ToArray())], w.Decisions.Log.Entries);
    }

    private static (DecisionRequest Request, Inhabitant Asker, Inhabitant Other) AskOnce(bool knows)
    {
        var port = new SilentPort();
        var w = NewWorld(3, port);
        var asker = w.Inhabitants.Get("inh-000001");
        var other = w.Inhabitants.Get("inh-000002");
        if (knows)
        {
            asker.KnownNames.Add(other.Id);
        }

        w.Objects.Spawn("blue_berry", new Position(2, 0));
        w.World.Step();
        return (port.Requests.First(r => r.Inhabitant.Id == asker.Id), asker, other);
    }

    private static void Introduce(World world, Inhabitant asker, Inhabitant other)
    {
        asker.Activity = new Activity(Naming.Action, other.Id, other.Position, null);
        for (var i = 0; i < 1_000 && asker.Activity is not null; i++)
        {
            world.Step();
        }

        Assert.Null(asker.Activity);
    }

    private static SimulationHostFactory Factory() => new(ContentSet.Load(ContentDirectory), Settings, new MockDecisionPort(), "de");

    /// <summary>A and b near each other (b idles), the other two far away.</summary>
    private static (World World, InhabitantsSystem Inhabitants, List<WorldEvent> Events) Meeting()
    {
        var world = new World(3, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Content.Knowledge,
            Names = Content.InhabitantNames,
        };
        world.Add(objects).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [new(0, 0), new(1.5, 0), new(60, 60), new(-60, -60)]);
        foreach (var inhabitant in inhabitants.All)
        {
            inhabitant.Activity = new Activity("idle", null, inhabitant.Position, 1_000);
        }

        var events = new List<WorldEvent>();
        inhabitants.Reported += events.Add;
        return (world, inhabitants, events);
    }

    private static (World World, WorldObjects Objects, InhabitantsSystem Inhabitants, DecisionsSystem Decisions) NewWorld(ulong seed, IDecisionPort port)
    {
        var world = new World(seed, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Content.Knowledge,
            Names = Content.InhabitantNames,
        };
        var decisions = new DecisionsSystem(Settings, inhabitants, objects, Content.Actions, Content.Brain, Content.Vocabularies[VocabularyKind.Action], port, Content.Inventions.Guard, "de")
        {
            Knowledge = Content.Knowledge,
        };
        world.Add(objects).Add(decisions).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [new Position(0, 0), new Position(3, 0), new Position(500, 500), new Position(1000, 0)]);
        return (world, objects, inhabitants, decisions);
    }

    /// <summary>Keeps every request, never answers.</summary>
    private sealed class SilentPort : IDecisionPort
    {
        public List<DecisionRequest> Requests { get; } = [];

        public void Send(string requestId, string requestJson) => Requests.Add(ContractSerializer.Deserialize<DecisionRequest>(requestJson));

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }

    /// <summary>Answers every request at the next tick with a scripted JSON.</summary>
    private sealed class ScriptedPort(Func<DecisionRequest, string> answer) : IDecisionPort
    {
        private readonly Queue<PortMessage> _ready = new();

        public void Send(string requestId, string requestJson) =>
            _ready.Enqueue(new PortMessage(requestId, answer(ContractSerializer.Deserialize<DecisionRequest>(requestJson)), IsError: false));

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            var messages = _ready.ToList();
            _ready.Clear();
            return messages;
        }
    }
}
