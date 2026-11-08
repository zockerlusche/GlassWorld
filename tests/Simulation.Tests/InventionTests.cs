using System.Text.Json;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// D471 D5 part 1: wish → invention job → final check → runtime type → chronicle; save, load, replay.
/// Every invention here is test data (built from the contract example), never product code.
/// </summary>
public sealed class InventionTests : IDisposable
{
    private const string Need = "thirst";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    // Things near the inhabitant that cannot satisfy the need (test data from the start catalogue).
    private static readonly string[] NearbyTypes = ["flint", "dry_grass", "pyrite"];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public InventionTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ALastingLackBecomesAnInventionJobWithTheInhabitantsViewOfTheMaterials()
    {
        var port = new ScriptedJobPort();
        var host = World(port, 11);
        var inhabitant = host.Inhabitants.All.Single();

        // A belief that differs from the truth: the job must carry the belief, never the truth.
        inhabitant.Beliefs.Set("flint", "burnable", true, 0.6);
        RunUntilJob(host, port);

        var job = Assert.Single(port.Submitted);
        Assert.Equal(JobKind.Invention, job.Kind);
        Assert.Equal(inhabitant.Id, job.Inhabitant);
        var payload = job.Payload.Invention!;
        Assert.Equal(Need, payload.Want);
        Assert.Equal(Content.Value.Inventions.Settings.TechLevel, payload.TechLevel);
        Assert.Equal(NearbyTypes.Order(StringComparer.Ordinal), payload.Materials.Select(m => m.Type).Order(StringComparer.Ordinal));
        Assert.Equal(["burnable"], payload.Materials.Single(m => m.Type == "flint").Observed);
        Assert.Empty(payload.Materials.Single(m => m.Type == "pyrite").Observed);
        Assert.Contains(host.Inhabitants.EventsSince(0), e => e.Event.Kind == InventionSystem.Wanted && e.Event.Action == Need);
        Assert.Single(host.Inventions.Wants);
    }

    [Fact]
    public void WithoutAJobPortTheSystemIsDormantAndStaysOutOfTheHash()
    {
        var host = World(null, 11);
        host.World.Run(Content.Value.Inventions.Settings.Trigger.HoursBelow * host.World.Calendar.Settings.TicksPerHour * 2);
        Assert.False(host.Inventions.HasState);
        Assert.False(host.Chronicle.HasState);
        Assert.Empty(host.Inventions.Log.Entries);
    }

    [Fact]
    public void AnApprovedInventionBecomesARuntimeTypeThatRulesJudgeByItsProperties()
    {
        var port = new ScriptedJobPort();
        var host = World(port, 12);
        RunUntilJob(host, port);
        var job = port.Submitted.Single();
        var introduces = new[] { "container", "drinkable" };
        port.Answer(job, Approved(job, introduces));
        host.World.Step();

        var typeId = InventionSystem.TypeIdFor(job.RequestId);
        Assert.True(host.Types.Contains(typeId));
        // Its own properties plus those every invention has (implied_properties, data).
        var expected = introduces.Concat(Content.Value.Inventions.Settings.ImpliedProperties).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        Assert.Equal(expected, host.Types.TrueProperties(typeId).Order(StringComparer.Ordinal));
        var info = Assert.IsType<InventionInfo>(host.Inventions.Invention(typeId));
        Assert.Equal(host.Inhabitants.All.Single().Id, info.InhabitantId);
        Assert.Empty(host.Inventions.Wants);

        var record = Assert.Single(host.Chronicle.All);
        Assert.Equal("invented", record.Kind);
        Assert.Equal(typeId, record.TargetId);
        Assert.Equal(record, Assert.Single(host.Chronicle.Since(0)));
        Assert.Equal(record, Assert.Single(host.Chronicle.Page(long.MaxValue, 10)));

        // The rules know the new type by its true properties only.
        var obj = host.Objects.Spawn(typeId, host.Inhabitants.All.Single().Position);
        Assert.Equal(typeId, obj.TypeId);
        var drinkable = Content.Value.Actions.WithTruth(host.Types).Applying("drink", typeId);
        Assert.Contains(drinkable, r => r.RequiresProperty == "drinkable");
        Assert.DoesNotContain(drinkable, r => r.RequiresProperty == "salty");
        Assert.Throws<KeyNotFoundException>(() => Content.Value.Actions.Applying("drink", typeId));
    }

    [Fact]
    public void ARefusedInventionLeavesNoTypeClosesTheWishAndWaitsForTheCooldown()
    {
        var port = new ScriptedJobPort();
        var host = World(port, 13);
        RunUntilJob(host, port);
        var job = port.Submitted.Single();
        var status = Approved(job, ["container"]);
        status.Result!.Invention!.Invention!.Materials = ["not_perceived_thing"];
        port.Answer(job, status);
        host.World.Step();

        Assert.False(host.Types.Contains(InventionSystem.TypeIdFor(job.RequestId)));
        Assert.Empty(host.Inventions.Wants);
        Assert.Contains(host.Inhabitants.EventsSince(0), e => e.Event.Kind == InventionSystem.Rejected);
        Assert.Contains(host.Inventions.Log.Entries, e => e.Kind == DecisionLogKind.JobResult && e.Note == "rejected:" + InventionCheck.MaterialNotPerceived);
        Assert.Empty(host.Chronicle.All);

        host.World.Run(host.World.Calendar.Settings.TicksPerHour);
        Assert.Single(port.Submitted);
    }

    [Fact]
    public void ARetryableErrorKeepsTheWishAndSendsItAgainWithANewRequestId()
    {
        var port = new ScriptedJobPort();
        var host = World(port, 14);
        RunUntilJob(host, port);
        var first = port.Submitted.Single();
        var error = new ServiceError { ContractVersion = ContractInfo.Version, RequestId = first.RequestId, Tick = first.Tick, Code = ServiceErrorCode.Model_unavailable, Message = "test", Retryable = true };
        port.AnswerError(first.RequestId, ContractSerializer.Serialize(error));
        host.World.Step();
        Assert.NotNull(Assert.Single(host.Inventions.Wants).RetryAtTick);

        host.World.Run((long)Content.Value.Inventions.Settings.RetryAfterMinutes * host.World.Calendar.Settings.TicksPerHour / 60);
        Assert.Equal(2, port.Submitted.Count);
        Assert.NotEqual(first.RequestId, port.Submitted[1].RequestId);
        Assert.Equal(port.Submitted[1].RequestId, Assert.Single(host.Inventions.Wants).RequestId);
    }

    [Fact]
    public void SaveAndLoadKeepInventionsRuntimeTypesWishesAndChronicle()
    {
        var port = new ScriptedJobPort();
        var host = World(port, 15);
        RunUntilJob(host, port);
        var job = port.Submitted.Single();
        port.Answer(job, Approved(job, ["container"]));
        host.World.Run(5);
        var typeId = InventionSystem.TypeIdFor(job.RequestId);
        host.Objects.Spawn(typeId, host.Inhabitants.All.Single().Position);
        var path = Path.Combine(_directory, "inventions.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));

        var state = SaveGameStore.Load(path);
        var loaded = StateCodec.Restore(Factory(new ReplayJobPort(host.Inventions.Log), new ReplayDecisionPort(host.Decisions.Log)), state);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        Assert.True(loaded.Types.Contains(typeId));
        Assert.Equal(host.Chronicle.All, loaded.Chronicle.All);
        Assert.Equal(host.Inventions.Log.Entries, loaded.Inventions.Log.Entries);
        Assert.NotNull(loaded.Inventions.Invention(typeId));
    }

    [Fact]
    public void ASaveGameOfVersionOneIsMigratedOnLoadWithoutChangingTheFile()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 9, new MockDecisionPort(), "de");
        host.World.Run(120);

        // A B1 build knew beliefs without source or time (B4a-S5): what it saved loads as experienced at tick 0.
        foreach (var inhabitant in host.Inhabitants.All)
        {
            foreach (var (typeId, propertyId, belief) in inhabitant.Beliefs.All.ToList())
            {
                inhabitant.Beliefs.Set(typeId, propertyId, belief.Value, belief.Confidence);
            }

            Assert.Equal(0, inhabitant.Relationships.Count);
        }

        var path = Path.Combine(_directory, "v1.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));

        // Turn the file back into schema 1 (as written by the B1 build).
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE inventions; DROP TABLE wants; DROP TABLE invention_timers; DROP TABLE job_log; DROP TABLE chronicle; DROP TABLE island; DROP TABLE relationships; ALTER TABLE beliefs DROP COLUMN source; ALTER TABLE beliefs DROP COLUMN told_by; ALTER TABLE beliefs DROP COLUMN tick;
                DELETE FROM meta WHERE key = 'invention_counter';
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        var loaded = StateCodec.Restore(Factory(new ReplayJobPort(new DecisionLog()), new MockDecisionPort()), SaveGameStore.Load(path));
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(1, SaveGameStore.VersionOf(path));
    }

    [Fact]
    public void AReplayFromSeedAndLogsGivesTheSameHash()
    {
        var port = new ScriptedJobPort();
        var original = World(port, 16);
        RunUntilJob(original, port);
        var job = port.Submitted.Single();
        port.Answer(job, Approved(job, ["container"]));
        original.World.Run(30);

        var replay = World(new ReplayJobPort(original.Inventions.Log), 16, new ReplayDecisionPort(original.Decisions.Log));
        replay.World.Run(original.World.Tick);
        Assert.Equal(original.World.StateHash(), replay.World.StateHash());
        Assert.True(replay.Types.Contains(InventionSystem.TypeIdFor(job.RequestId)));
    }

    [Fact]
    public void TheFinalCheckRefusesWhatTheDataForbid()
    {
        var rules = Content.Value.Inventions;
        var perceived = NearbyTypes.ToHashSet(StringComparer.Ordinal);
        Assert.Empty(InventionCheck.Check(Result(["container"]), perceived, rules));

        Assert.Equal([InventionCheck.NotApproved], InventionCheck.Check(Mutate(r => r.Decision = InventionResultDecision.Reject), perceived, rules));
        Assert.Equal([InventionCheck.NotApproved], InventionCheck.Check(Mutate(r => r.Rounds.Last().Decision = GuardVerdictDecision.Revise), perceived, rules));
        Assert.Contains(InventionCheck.MaterialNotPerceived, InventionCheck.Check(Mutate(r => r.Invention!.Steps.First().Materials = ["not_perceived_thing"]), perceived, rules));
        Assert.Contains(InventionCheck.UnknownProperty, InventionCheck.Check(Mutate(r => r.Invention!.Introduces = ["no_such_property"]), perceived, rules));
        Assert.Contains(InventionCheck.UnknownPurpose, InventionCheck.Check(Mutate(r => r.Invention!.Purpose = "no_such_action"), perceived, rules));
        Assert.Contains(InventionCheck.UnknownProcess, InventionCheck.Check(Mutate(r => r.Invention!.Steps.First().Process = "no_such_process"), perceived, rules));
        Assert.Contains(InventionCheck.TechLevel, InventionCheck.Check(Mutate(r => r.Invention!.TechLevel = rules.TechLevels[^1]), perceived, rules));
        Assert.Contains(InventionCheck.TooLarge, InventionCheck.Check(Mutate(r => r.Invention!.SizeM.X = rules.Settings.Limits.MaxSizeM * 2), perceived, rules));
        Assert.Contains(InventionCheck.NameNotAllowed, InventionCheck.Check(Mutate(r => r.Invention!.Name.En = BlockedWord()), perceived, rules));

        // "downsize" in the last round is an approval of the smaller version.
        Assert.Empty(InventionCheck.Check(Mutate(r => r.Rounds.Last().Decision = GuardVerdictDecision.Downsize), perceived, rules));

        // New needs and abilities: actions from the vocabulary, names through the content guard.
        var action = rules.Actions.Order(StringComparer.Ordinal).First();
        Assert.Empty(InventionCheck.Check(Mutate(r => r.Invention!.IntroducesNeeds = [new NewNeed { Id = "test_need", NameDe = "Ruhe", NameEn = "calm", DecayPerDay = 0.1, Actions = [action] }]), perceived, rules));
        Assert.Contains(InventionCheck.UnknownAction, InventionCheck.Check(Mutate(r => r.Invention!.IntroducesAbilities = [new NewAbility { Id = "test_ability", NameDe = "Flechten", NameEn = "braiding", StartLevel = 0.1, Actions = ["no_such_action"] }]), perceived, rules));
        Assert.Contains(InventionCheck.NameNotAllowed, InventionCheck.Check(Mutate(r => r.Invention!.IntroducesNeeds = [new NewNeed { Id = "test_need", NameDe = "Ruhe", NameEn = BlockedWord(), DecayPerDay = 0.1, Actions = [action] }]), perceived, rules));

        // B2531: a property the data mark as one of living beings is refused.
        var living = rules with { Settings = Copy(rules.Settings, s => s.LivingProperties = ["container"]) };
        Assert.Contains(InventionCheck.LivingProperty, InventionCheck.Check(Result(["container"]), perceived, living));

        InventionResult Mutate(Action<InventionResult> change)
        {
            var result = Result(["container"]);
            change(result);
            return result;
        }
    }

    [Fact]
    public void AnApprovedInventionBringsItsNewSkillIntoTheRegister()
    {
        // A1372 (B4b R11): introduces_abilities -> skill register, saved with the world; a duplicate is dropped.
        var port = new ScriptedJobPort();
        var host = World(port, 12);
        RunUntilJob(host, port);
        var job = port.Submitted.Single();
        var status = Approved(job, ["container"]);
        status.Result!.Invention!.Invention!.IntroducesAbilities =
        [
            new NewAbility { Id = "braiding", NameDe = "Flechten", NameEn = "braiding", StartLevel = 0.1, Actions = ["combine", "use"] },
            new NewAbility { Id = "building", NameDe = "Bauen", NameEn = "building", StartLevel = 0.1, Actions = ["use"] },
        ];
        var before = host.World.StateHash();
        port.Answer(job, status);
        host.World.Step();

        var typeId = InventionSystem.TypeIdFor(job.RequestId);
        var added = Assert.Single(host.Skills.Added);
        Assert.Equal(("braiding", "Flechten", typeId), (added.Id, added.NameDe, added.SourceTypeId));
        Assert.Contains(host.Skills.PractisedBy("combine"), p => p.Skill == "braiding");
        Assert.Contains(host.Inhabitants.Events, e => e.Kind == SkillRegister.SkillRefused && e.TargetId == typeId && e.Detail == SkillRegister.ReasonTaken);
        Assert.NotEqual(before, host.World.StateHash());

        var path = Path.Combine(Path.GetTempPath(), "gw-skills-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var loaded = StateCodec.Restore(Factory(new ReplayJobPort(host.Inventions.Log), new ReplayDecisionPort(host.Decisions.Log)), SaveGameStore.Load(path));
            Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
            Assert.Equal(["combine", "use"], Assert.Single(loaded.Skills.Added).Actions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static SimulationHost World(IJobPort? port, ulong seed, IDecisionPort? decisions = null)
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, seed, decisions ?? new MockDecisionPort(), "de", port);
        var model = Content.Value.InhabitantModel;
        var needs = model.Needs.ToDictionary(n => n.Id, n => n.Id == Need ? 0.0 : 1.0, StringComparer.Ordinal);
        var moods = model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal);
        var traits = model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal);
        host.Inhabitants.Add(new Inhabitant("inh-000001", "female", 1, new Position(0, 0), needs, moods, traits, model.WillToLive.Start));
        for (var i = 0; i < NearbyTypes.Length; i++)
        {
            host.Objects.Spawn(NearbyTypes[i], new Position(1 + i, 0));
        }

        return host;
    }

    private static SimulationHostFactory Factory(IJobPort jobs, IDecisionPort decisions)
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, decisions, "de", jobs);
    }

    private static void RunUntilJob(SimulationHost host, ScriptedJobPort port)
    {
        var limit = (Content.Value.Inventions.Settings.Trigger.HoursBelow + 2) * host.World.Calendar.Settings.TicksPerHour;
        for (var i = 0; i < limit && port.Submitted.Count == 0; i++)
        {
            host.World.Step();
        }

        Assert.NotEmpty(port.Submitted);
    }

    /// <summary>The contract example of an approved invention, fitted to the job (test data).</summary>
    private static JobStatus Approved(GenerationJob job, string[] introduces)
    {
        var example = File.ReadAllText(Path.Combine(TestData.Root, "contracts", "examples", "valid", "job-status.invention-approved.json"));
        var status = ContractSerializer.Deserialize<JobStatus>(example);
        status.JobId = job.RequestId;
        status.Tick = job.Tick;
        status.Result!.Invention = Result(introduces, [.. job.Payload.Invention!.Materials.Select(m => m.Type)]);
        return status;
    }

    private static InventionResult Result(string[] introduces, string[]? materials = null)
    {
        var example = File.ReadAllText(Path.Combine(TestData.Root, "contracts", "examples", "valid", "job-status.invention-approved.json"));
        var result = ContractSerializer.Deserialize<JobStatus>(example).Result!.Invention!;
        var used = materials ?? NearbyTypes;
        var invention = result.Invention!;
        invention.Materials = [used[0]];
        foreach (var step in invention.Steps)
        {
            step.Materials = [used[^1]];
        }

        invention.Introduces = introduces;
        invention.TechLevel = Content.Value.Inventions.Settings.TechLevel;
        invention.SizeM.X = invention.SizeM.Y = invention.SizeM.Z = Content.Value.Inventions.Settings.Limits.MaxSizeM / 2;
        return result;
    }

    private static InventionSettings Copy(InventionSettings settings, Action<InventionSettings> change)
    {
        var copy = ContractSerializer.Deserialize<InventionSettings>(ContractSerializer.Serialize(settings));
        change(copy);
        return copy;
    }

    /// <summary>A term the content guard blocks, taken from the safety data (no literal here).</summary>
    private static string BlockedWord()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContentDirectory, "safety", "rules.json")));
        return doc.RootElement.GetProperty("categories")[0].GetProperty("terms_en")[0].GetString()!;
    }

    /// <summary>Records submitted jobs; outcomes are handed in by the test.</summary>
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
