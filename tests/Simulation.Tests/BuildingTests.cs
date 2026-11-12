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
/// D471 D5 part 2: approved invention → build_plan job → the core's plan check (B4b R14) → building step
/// by step with materials from perception → the object stands → first use; save, load, replay; compute
/// night signal. Inventions and plans are test data built from the contract examples.
/// </summary>
public sealed partial class BuildingTests : IDisposable
{
    private const string Need = "thirst";
    // A copy of the content in which the inventor builds even while thirsty (min_need_to_build 0), so no
    // test has to change the world from outside and every run can be replayed from seed and logs.
    private static readonly Lazy<string> EagerContent = new(CopyContentBuildingAlways);
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(EagerContent.Value));

    // Test data from the start catalogue: the invention is made of the first, its step of the last.
    private static readonly string[] NearbyTypes = ["flint", "dry_grass", "pyrite"];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public BuildingTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void AnApprovedInventionAsksForAPlanWithTheConstructionKit()
    {
        var (host, port, typeId) = Invented(21);
        var planJob = Assert.Single(port.Submitted, j => j.Kind == JobKind.Build_plan);

        // The contract test of this request form (ADR-0009): the job as it left the core fits its schema.
        Assert.Empty(ContractValidator.Validate("generation-job", port.Json[planJob.RequestId]));
        var kit = Content.Value.Inventions.Build.Kit;
        Assert.Equal(kit.Shapes, planJob.Payload.BuildPlan!.Shapes);
        Assert.Equal(kit.Colors.Select(c => c.Id), planJob.Payload.BuildPlan.Colors);
        Assert.Equal(Math.Min(kit.MaxParts, Content.Value.Inventions.Settings.Limits.MaxParts), planJob.Payload.BuildPlan.MaxParts);
        Assert.Equal(host.Inventions.Invention(typeId)!.Name.De, planJob.Payload.BuildPlan.Invention.Name.De);
        Assert.Contains(host.Inventions.Log.Entries, e => e.Kind == DecisionLogKind.JobRequest && e.RequestId == planJob.RequestId);
    }

    [Fact]
    public void TheInventorBuildsStepByStepUsesUpTheMaterialAndTheObjectStands()
    {
        var (host, port, typeId) = Invented(22);
        var events = Record(host);
        AnswerPlan(port, Plan());
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, 3000);

        var task = host.Inventions.Builds.Single();
        Assert.True(host.Objects.TryGet(task.BuiltObjectId!, out var built));
        Assert.Equal(typeId, built.TypeId);
        Assert.Equal(new Position(task.SiteX, task.SiteZ), built.Position);
        Assert.DoesNotContain(host.Objects.All, o => o.TypeId == NearbyTypes[0]);
        Assert.DoesNotContain(host.Objects.All, o => o.TypeId == NearbyTypes[^1]);
        Assert.Contains(host.Objects.All, o => o.TypeId == NearbyTypes[1]);
        Assert.Equal(["1", "2"], events.Where(e => e.Kind == InventionSystem.BuildStep).Select(e => e.Action));
        Assert.Single(events, e => e.Kind == InventionSystem.Built && e.TargetId == built.Id);
        Assert.Single(events, e => e.Kind == InventionSystem.BuildPlanned && e.TargetId == typeId);
    }

    [Fact]
    public void WithoutMaterialInSightTheBuildingWaitsAndGoesOnWhenItAppears()
    {
        var (host, port, _) = Invented(23);
        var stepMaterial = host.Objects.All.Single(o => o.TypeId == NearbyTypes[^1]);
        host.Objects.Remove(stepMaterial.Id);
        AnswerPlan(port, Plan());
        RunUntil(host, () => host.Inventions.Builds.Single().StepsDone == 1, 3000);

        host.World.Run(host.World.Calendar.Settings.TicksPerHour * 6);
        var waiting = host.Inventions.Builds.Single();
        Assert.Equal(1, waiting.StepsDone);
        Assert.Null(waiting.BuiltObjectId);
        Assert.Null(waiting.PendingObjectId);

        host.Objects.Spawn(NearbyTypes[^1], stepMaterial.Position);
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, 3000);
    }

    [Fact]
    public void WithoutMaterialForLongTheBuilderNeedsHelpOnceAndThenGivesUp()
    {
        // A1373 (D827 R15, R17): looking by itself first, then needs_help with the material's properties (never its
        // type name), then giving up - each after the data's hours, counted from the moment no material was in sight.
        var (host, port, typeId) = Invented(23);
        var settings = ContractSerializer.Deserialize<BuildSettings>(File.ReadAllText(Path.Combine(EagerContent.Value, "build", "settings.json")));
        var perHour = host.World.Calendar.Settings.TicksPerHour;
        var stepMaterial = host.Objects.All.Single(o => o.TypeId == NearbyTypes[^1]);
        host.Objects.Remove(stepMaterial.Id);
        var events = Record(host);
        AnswerPlan(port, Plan());
        RunUntil(host, () => host.Inventions.Builds.Single().WaitingSinceTick is not null, 3000);
        var since = host.Inventions.Builds.Single().WaitingSinceTick!.Value;

        RunUntil(host, () => events.Any(e => e.Kind == InventionSystem.NeedsHelpEvent), (int)(settings.HelpAfterHours!.Value * perHour) + 10);
        var help = Assert.Single(events, e => e.Kind == InventionSystem.NeedsHelpEvent);
        Assert.Equal(typeId, help.TargetId);
        Assert.Equal(string.Join(',', host.Types.TrueProperties(NearbyTypes[^1]).Order(StringComparer.Ordinal)), help.Detail);
        Assert.DoesNotContain(NearbyTypes[^1], help.Detail!, StringComparison.Ordinal);
        Assert.Equal(string.Empty, help.Text);
        Assert.True(host.World.Tick - since >= (long)(settings.HelpAfterHours.Value * perHour));
        Assert.True(host.Inventions.Builds.Single().LookedTicks >= (long)(settings.HelpAfterHours.Value * perHour));
        Assert.True(host.Inventions.Builds.Single().NeedsHelp);

        // Saved and loaded while waiting: the same hash, and the wait goes on from where it was.
        var path = Path.Combine(_directory, "waiting.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loaded = Restore(path, host);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(since, loaded.Inventions.Builds.Single().WaitingSinceTick);

        // Only ticks of looking count (asleep or eating do not), so the limit may take longer than its hours.
        RunUntil(host, () => host.Inventions.Builds.Single().GaveUp, (int)(settings.GiveUpAfterHours!.Value * perHour * 3));
        Assert.True(host.Inventions.Builds.Single().GaveUp);
        var failed = Assert.Single(events, e => e.Kind == InventionSystem.BuildFailed);
        Assert.Equal((typeId, InventionSystem.NoMaterial), (failed.TargetId, failed.Detail));

        // Given up is final (review GW3 #283): material in sight again starts nothing, no second failure or call.
        host.Objects.Spawn(NearbyTypes[^1], stepMaterial.Position);
        host.World.Run((int)(settings.GiveUpAfterHours.Value * perHour * 2));
        Assert.Single(events, e => e.Kind == InventionSystem.BuildFailed);
        Assert.Single(events, e => e.Kind == InventionSystem.NeedsHelpEvent);
        var after = host.Inventions.Builds.Single();
        Assert.Equal((null, null, 1), (after.BuiltObjectId, after.PendingObjectId, after.StepsDone));
    }

    [Fact]
    public void MaterialElsewhereIsSaidAndFoundMaterialEndsTheWait()
    {
        // Material out of sight but on the island: the call says so (looking further helps); once some is in sight
        // the wait is over and the building goes on.
        var (host, port, _) = Invented(23);
        var stepMaterial = host.Objects.All.Single(o => o.TypeId == NearbyTypes[^1]);
        host.Objects.Remove(stepMaterial.Id);
        host.Objects.Spawn(NearbyTypes[^1], new Position(5000, 5000));
        var events = Record(host);
        AnswerPlan(port, Plan());
        RunUntil(host, () => events.Any(e => e.Kind == InventionSystem.NeedsHelpEvent), 3000 + (int)(24 * host.World.Calendar.Settings.TicksPerHour));
        Assert.Equal(InventionSystem.InWorld, Assert.Single(events, e => e.Kind == InventionSystem.NeedsHelpEvent).Text);

        host.Objects.Spawn(NearbyTypes[^1], stepMaterial.Position);
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, 3000);
        var done = host.Inventions.Builds.Single();
        Assert.Equal((null, false), (done.WaitingSinceTick, done.NeedsHelp));
        Assert.DoesNotContain(events, e => e.Kind == InventionSystem.BuildFailed);
    }

    [Fact]
    public void TheFirstUseOfAnInventionIsAChronicleEventOnlyOnce()
    {
        var (host, port, typeId) = Invented(24);
        AnswerPlan(port, Plan());
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, 3000);
        var built = host.Inventions.Builds.Single().BuiltObjectId!;
        var inhabitant = host.Inhabitants.All.Single();
        var use = Content.Value.Inventions.Build.Settings.UseAction;

        for (var round = 0; round < 2; round++)
        {
            // Needs fine, so the queued use is not dropped as stale (F5275 B) after the long build.
            foreach (var need in inhabitant.Needs.Keys.ToList())
            {
                inhabitant.Needs[need] = 1.0;
            }

            host.Objects.TryGet(built, out var obj);
            inhabitant.Planned = new Activity(use, built, obj.Position, null);
            var before = host.Chronicle.All.Count;
            RunUntil(host, () => inhabitant.Planned is null && inhabitant.Activity?.Action != use, 2000);
            if (round == 0)
            {
                var entry = Assert.Single(host.Chronicle.All.Skip(before));
                Assert.Equal((InventionSystem.FirstUse, inhabitant.Id, built), (entry.Kind, entry.InhabitantId, entry.TargetId));
            }
            else
            {
                Assert.DoesNotContain(host.Chronicle.All.Skip(before), e => e.Kind == InventionSystem.FirstUse);
            }
        }

        Assert.Equal([typeId], host.Inventions.FirstUsed);
    }

    [Fact]
    public void ThePlanCheckRefusesWhatTheKitAndTheInventionForbid()
    {
        var rules = Content.Value.Inventions;
        var invention = Invention();
        var maxParts = rules.Settings.Limits.MaxParts;
        Assert.Empty(BuildPlanCheck.Check(Plan(), invention, rules.Build.Kit, rules.Build.Settings, maxParts));

        Assert.Contains(BuildPlanCheck.PartCount, Check(p => p.Parts = [p.Parts.First()]));
        Assert.Contains(BuildPlanCheck.PartCount, Check(p => p.Parts = [.. Enumerable.Repeat(p.Parts.First(), maxParts + 1)]));
        Assert.Contains(BuildPlanCheck.UnknownShape, Check(p => p.Parts.First().Shape = "no_such_shape"));
        Assert.Contains(BuildPlanCheck.UnknownColor, Check(p => p.Parts.First().Color = "no_such_colour"));
        Assert.Contains(BuildPlanCheck.MaterialNotInInvention, Check(p => p.Parts.First().Material = NearbyTypes[1]));
        Assert.Contains(BuildPlanCheck.StepsNotContinuous, Check(p => p.Parts.Last().Step = 3));
        Assert.Contains(BuildPlanCheck.PartTooLarge, Check(p => p.Parts.First().SizeM.X = rules.Build.Kit.MaxPartSizeM * 2));
        Assert.Contains(BuildPlanCheck.OutOfBounds, Check(p => p.Parts.Last().PositionM.X = invention.SizeM.X * 3));

        IReadOnlyList<string> Check(Action<BuildPlan> change)
        {
            var plan = Plan();
            change(plan);
            return BuildPlanCheck.Check(plan, invention, rules.Build.Kit, rules.Build.Settings, maxParts);
        }
    }

    [Fact]
    public void ARefusedPlanIsAskedForAgainAndAfterTheLastAttemptTheInventionStaysUnbuilt()
    {
        var (host, port, typeId) = Invented(25);
        var settings = Content.Value.Inventions.Build.Settings;
        var events = Record(host);
        var bad = Plan();
        bad.Parts.First().Material = NearbyTypes[1];
        for (var attempt = 1; attempt <= settings.MaxPlanAttempts; attempt++)
        {
            AnswerPlan(port, bad);
            host.World.Step();
            if (attempt < settings.MaxPlanAttempts)
            {
                RunUntil(host, () => port.Submitted.Count(j => j.Kind == JobKind.Build_plan) == attempt + 1, 2000);
            }
        }

        var task = host.Inventions.Builds.Single();
        Assert.True(task.GaveUp);
        Assert.Null(task.PlanJson);
        Assert.Equal(settings.MaxPlanAttempts, events.Count(e => e.Kind == InventionSystem.BuildPlanRejected));
        Assert.Single(events, e => e.Kind == InventionSystem.BuildFailed && e.TargetId == typeId);
        Assert.Equal(settings.MaxPlanAttempts, port.Submitted.Select(j => j.RequestId).Distinct().Count(id => port.Submitted.Single(j => j.RequestId == id).Kind == JobKind.Build_plan));
    }

    [Fact]
    public void SaveAndLoadDuringTheBuildingGiveTheSameHashAndTheSameBuilding()
    {
        var (host, port, _) = Invented(26);
        AnswerPlan(port, Plan());
        RunUntil(host, () => host.Inventions.Builds.Single().StepsDone == 1, 3000);
        var path = Path.Combine(_directory, "building.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        Assert.Equal(host.World.StateHash(), Restore(path, host).World.StateHash());

        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, 3000);
        var loaded = Restore(path, host);
        loaded.World.Run(host.World.Tick - loaded.World.Tick);
        Assert.Equal(host.Inventions.Builds, loaded.Inventions.Builds);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
    }

    [Fact]
    public void AReplayFromSeedAndLogsBuildsTheSame()
    {
        var (original, port, _) = Invented(27);
        AnswerPlan(port, Plan());
        RunUntil(original, () => original.Inventions.Builds.Single().BuiltObjectId is not null, 3000);

        var replay = World(new ReplayJobPort(original.Inventions.Log), 27, new ReplayDecisionPort(original.Decisions.Log));
        replay.World.Run(original.World.Tick);
        Assert.Equal(original.World.StateHash(), replay.World.StateHash());
    }

    [Fact]
    public void TheComputeNightIsSignalledFromTheCalendar()
    {
        var port = new ScriptedJobPort();
        var host = World(port, 28);
        host.World.Run(host.World.Calendar.TicksPerDay * 2);
        var night = Content.Value.Inventions.Build.NightPhases;
        Assert.True(port.Phases.Count >= 4, $"only {port.Phases.Count} phase signals in two days");
        Assert.All(port.Phases.Zip(port.Phases.Skip(1)), p => Assert.NotEqual(p.First.Phase, p.Second.Phase));
        Assert.True(port.Phases[0].Force);
        Assert.All(port.Phases.Skip(1), p => Assert.False(p.Force));
        foreach (var (phase, tick, _) in port.Phases)
        {
            var isNight = night.Contains(host.World.Calendar.At(tick).Phase.ToString().ToLowerInvariant());
            Assert.Equal(isNight ? JobPhase.Night : JobPhase.Day, phase);
        }
    }

    [Fact]
    public void TheSnapshotShowsTheComputeNightAsSignalledAndTheWorldGoesOn()
    {
        // B7 night scene (GW3): the screen follows the same flag the core signals to the AI service.
        var port = new ScriptedJobPort();
        var host = World(port, 28);
        Assert.Null(host.Snapshot().ComputeNight); // not decided before the first tick
        var seen = new List<bool>();
        for (var hour = 0; hour < 48; hour++)
        {
            var before = host.World.Tick;
            host.World.Run(host.World.Calendar.Settings.TicksPerHour);
            Assert.True(host.World.Tick > before); // world time keeps running at night
            var snapshot = host.Snapshot();
            Assert.Equal(port.Phases[^1].Phase == JobPhase.Night, snapshot.ComputeNight);
            seen.Add(snapshot.ComputeNight == true);
        }

        Assert.Contains(true, seen);
        Assert.Contains(false, seen);
    }

    [Fact]
    public void TheDeciderNeverOffersTheBuildingStep()
    {
        var step = Content.Value.Inventions.Build.Settings.StepAction;
        Assert.Contains(step, Content.Value.Actions.Actions);
        Assert.DoesNotContain(step, Content.Value.Actions.OfferedActions);
        Assert.Contains(Content.Value.Inventions.Build.Settings.UseAction, Content.Value.Actions.OfferedActions);
    }

    [Fact]
    public void AFinishedInventionThatNeedsASoundGetsASoundJobAndKeepsTheFile()
    {
        var (host, port, typeId) = Invented(29);
        AnswerPlan(port, Plan());
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, 3000);
        host.World.Step();

        var job = Assert.Single(port.Submitted, j => j.Kind == JobKind.Sound);

        // The contract test of this request form (ADR-0009): the job as it left the core fits its schema.
        Assert.Empty(ContractValidator.Validate("generation-job", port.Json[job.RequestId]));
        var generation = Content.Value.Inventions.Build.Sound;
        var decision = host.Inventions.Invention(typeId)!;
        Assert.Equal(generation.Priority, job.Priority);
        Assert.Equal(host.Inventions.Builds.Single().BuiltObjectId, job.Object);
        Assert.Equal((decision.SoundDescription, SoundKind.One_shot, generation.Seconds.OneShot, generation.DefaultSteps, typeId), (job.Payload.Sound!.Description, job.Payload.Sound.Kind, job.Payload.Sound.Seconds, job.Payload.Sound.Steps, job.Payload.Sound.ObjectType));
        Assert.Null(decision.SoundFile);

        var status = ContractSerializer.Deserialize<JobStatus>(File.ReadAllText(Path.Combine(TestData.Root, "contracts", "examples", "valid", "job-status.sound-door.json")));
        status.JobId = job.RequestId;
        status.Tick = job.Tick;
        port.Answer(job, status);
        host.World.Step();
        Assert.Equal(status.Result!.Sound!.File, host.Inventions.Invention(typeId)!.SoundFile);

        var path = Path.Combine(_directory, "sound.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loaded = Restore(path, host);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(status.Result.Sound.File, loaded.Inventions.Invention(typeId)!.SoundFile);
    }

    [Fact]
    public void AFailedSoundJobLeavesTheInventionWithoutItsOwnSound()
    {
        var (host, port, typeId) = Invented(30);
        AnswerPlan(port, Plan());
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, 3000);
        host.World.Step();
        var job = Assert.Single(port.Submitted, j => j.Kind == JobKind.Sound);
        var error = new ServiceError { ContractVersion = ContractInfo.Version, RequestId = job.RequestId, Tick = job.Tick, Code = ServiceErrorCode.Bad_request, Message = "not a sound", Retryable = false };
        port.AnswerError(job.RequestId, ContractSerializer.Serialize(error));
        host.World.Run(host.World.Calendar.TicksPerDay * 2);

        var info = host.Inventions.Invention(typeId)!;
        Assert.Null(info.SoundFile);
        Assert.NotNull(info.SoundDescription);
        Assert.Single(port.Submitted, j => j.Kind == JobKind.Sound);
    }

    [Fact]
    public void ARetryableSoundFailureIsTriedAgainEachComputeNightUpToTheLimit()
    {
        var (host, port, typeId) = Invented(32);
        AnswerPlan(port, Plan());
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, 3000);
        host.World.Step();
        var limit = Content.Value.Inventions.Build.Settings.SoundMaxAttempts;
        var night = Content.Value.Inventions.Build.NightPhases;
        for (var attempt = 1; attempt <= limit; attempt++)
        {
            var job = port.Submitted.Last(j => j.Kind == JobKind.Sound);
            Assert.Equal(attempt, port.Submitted.Count(j => j.Kind == JobKind.Sound));
            var error = new ServiceError { ContractVersion = ContractInfo.Version, RequestId = job.RequestId, Tick = job.Tick, Code = ServiceErrorCode.Model_unavailable, Message = "sound model busy", Retryable = true };
            port.AnswerError(job.RequestId, ContractSerializer.Serialize(error));
            host.World.Step();
            if (attempt == limit)
            {
                break;
            }

            // Waits for the start of the next compute night, then goes out with a new request id.
            Assert.True(host.Inventions.Sounds.Single().RetryAtNight);
            RunUntil(host, () => port.Submitted.Count(j => j.Kind == JobKind.Sound) == attempt + 1, (int)host.World.Calendar.TicksPerDay * 2);
            var retry = port.Submitted.Last(j => j.Kind == JobKind.Sound);
            var phase = host.World.Calendar.At(retry.Tick).Phase.ToString().ToLowerInvariant();
            Assert.Contains(night, n => n == phase);
            Assert.NotEqual(job.RequestId, retry.RequestId);
        }

        var final = host.Inventions.Sounds.Single();
        Assert.True(final.Failed);
        Assert.Equal(limit, final.Attempts);
        host.World.Run(host.World.Calendar.TicksPerDay * 2);
        Assert.Equal(limit, port.Submitted.Count(j => j.Kind == JobKind.Sound));
        Assert.Null(host.Inventions.Invention(typeId)!.SoundFile);
    }

    [Fact]
    public void ALoadedWorldSignalsItsCurrentPhaseEvenIfThePortSawItBefore()
    {
        var port = new ScriptedJobPort();
        var host = World(port, 33);
        host.World.Run(200);
        var path = Path.Combine(_directory, "phase.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));

        var loadedPort = new ScriptedJobPort();
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(EagerContent.Value, "decider", "settings.json")));
        var loaded = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(EagerContent.Value), settings, new ReplayDecisionPort(host.Decisions.Log), "de", loadedPort), SaveGameStore.Load(path));
        loaded.World.Step();

        var (phase, tick, force) = Assert.Single(loadedPort.Phases);
        var isNight = Content.Value.Inventions.Build.NightPhases.Contains(loaded.World.Calendar.At(tick).Phase.ToString().ToLowerInvariant());
        Assert.Equal((isNight ? JobPhase.Night : JobPhase.Day, true), (phase, force));
    }

    [Fact]
    public void AnInventionWithoutSoundGetsNoSoundJob()
    {
        var (host, port, typeId) = Invented(31, needsSound: false);
        AnswerPlan(port, Plan());
        RunUntil(host, () => host.Inventions.Builds.Single().BuiltObjectId is not null, 3000);
        host.World.Run(10);
        Assert.DoesNotContain(port.Submitted, j => j.Kind == JobKind.Sound);
        Assert.Null(host.Inventions.Invention(typeId)!.SoundDescription);
    }

    // ------------------------------------------------------------------ helpers (test data only)

    private static (SimulationHost Host, ScriptedJobPort Port, string TypeId) Invented(ulong seed, bool needsSound = true)
    {
        var port = new ScriptedJobPort();
        var host = World(port, seed);
        var limit = (Content.Value.Inventions.Settings.Trigger.HoursBelow + 2) * host.World.Calendar.Settings.TicksPerHour;
        // Other jobs may go out first (a memory job about what the inventor tried); the invention job is the one.
        for (var i = 0; i < limit && !port.Submitted.Any(j => j.Kind == JobKind.Invention); i++)
        {
            host.World.Step();
        }

        var job = Assert.Single(port.Submitted, j => j.Kind == JobKind.Invention);
        var approved = Approved(job);
        approved.Result!.Invention!.Sound!.Needed = needsSound;
        port.Answer(job, approved);
        host.World.Step();
        var typeId = InventionSystem.TypeIdFor(job.RequestId);
        Assert.True(
            host.Types.Contains(typeId),
            "not registered; job log: " + string.Join(" | ", host.Inventions.Log.Entries.Select(e => $"{e.Kind}:{e.Note}")));
        return (host, port, typeId);
    }

    private static SimulationHost World(IJobPort port, ulong seed, IDecisionPort? decisions = null)
    {
        var host = SimulationHost.CreateEmpty(EagerContent.Value, seed, decisions ?? new MockDecisionPort(), "de", port);
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

    private static string CopyContentBuildingAlways()
    {
        var source = Path.Combine(TestData.Root, "content");
        var target = Path.Combine(Path.GetTempPath(), "glassworld-building-content-" + Path.GetRandomFileName());
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        var settings = Path.Combine(target, "build", "settings.json");
        var build = ContractSerializer.Deserialize<BuildSettings>(File.ReadAllText(settings));
        build.MinNeedToBuild = 0;
        File.WriteAllText(settings, ContractSerializer.Serialize(build));
        return target;
    }

    private static List<WorldEvent> Record(SimulationHost host)
    {
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        return events;
    }

    private static void RunUntil(SimulationHost host, Func<bool> done, int maxTicks)
    {
        for (var i = 0; i < maxTicks && !done(); i++)
        {
            host.World.Step();
        }

        Assert.True(done(), "condition not reached");
    }

    private static void AnswerPlan(ScriptedJobPort port, BuildPlan plan)
    {
        var job = port.Submitted.Last(j => j.Kind == JobKind.Build_plan);
        var example = File.ReadAllText(Path.Combine(TestData.Root, "contracts", "examples", "valid", "job-status.build-plan-bowl.json"));
        var status = ContractSerializer.Deserialize<JobStatus>(example);
        status.JobId = job.RequestId;
        status.Tick = job.Tick;
        status.Result!.BuildPlan = plan;
        port.Answer(job, status);
    }

    /// <summary>Two parts in two steps: the invention's material first, then its step material.</summary>
    private static BuildPlan Plan()
    {
        var kit = Content.Value.Inventions.Build.Kit;
        var plan = new BuildPlan();
        for (var i = 0; i < 2; i++)
        {
            plan.Parts.Add(new BuildPart
            {
                Shape = kit.Shapes.ElementAt(i),
                SizeM = new SizeM2 { X = 0.3, Y = 0.2, Z = 0.3 },
                PositionM = new PositionM { X = 0, Y = 0.1 + (0.2 * i), Z = 0 },
                RotationYDeg = 0,
                Material = i == 0 ? NearbyTypes[0] : NearbyTypes[^1],
                Color = kit.Colors.ElementAt(i).Id,
                Step = i + 1,
            });
        }

        return plan;
    }

    private static Invention Invention()
    {
        var example = File.ReadAllText(Path.Combine(TestData.Root, "contracts", "examples", "valid", "job-status.invention-approved.json"));
        var invention = ContractSerializer.Deserialize<JobStatus>(example).Result!.Invention!.Invention!;
        invention.Materials = [NearbyTypes[0]];
        foreach (var step in invention.Steps)
        {
            step.Materials = [NearbyTypes[^1]];
        }

        invention.Introduces = ["container"];
        invention.TechLevel = Content.Value.Inventions.Settings.TechLevel;
        invention.SizeM.X = invention.SizeM.Y = invention.SizeM.Z = Content.Value.Inventions.Settings.Limits.MaxSizeM / 2;
        return invention;
    }

    private static JobStatus Approved(GenerationJob job)
    {
        var example = File.ReadAllText(Path.Combine(TestData.Root, "contracts", "examples", "valid", "job-status.invention-approved.json"));
        var status = ContractSerializer.Deserialize<JobStatus>(example);
        status.JobId = job.RequestId;
        status.Tick = job.Tick;
        status.Result!.Invention!.Invention = Invention();
        return status;
    }

    private static SimulationHost Restore(string path, SimulationHost original)
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(EagerContent.Value, "decider", "settings.json")));
        var factory = new SimulationHostFactory(ContentSet.Load(EagerContent.Value), settings, new ReplayDecisionPort(original.Decisions.Log), "de", new ReplayJobPort(original.Inventions.Log));
        return StateCodec.Restore(factory, SaveGameStore.Load(path));
    }

    /// <summary>Records submitted jobs and phase signals; outcomes are handed in by the test.</summary>
    private sealed class ScriptedJobPort : IJobPort
    {
        private readonly Queue<PortMessage> _ready = new();

        public List<GenerationJob> Submitted { get; } = [];

        /// <summary>Every submitted job as it left the core, by request id.</summary>
        public Dictionary<string, string> Json { get; } = new(StringComparer.Ordinal);

        public List<(string Phase, long Tick, bool Force)> Phases { get; } = [];

        public void Submit(string requestId, string jobJson)
        {
            Json[requestId] = jobJson;
            Submitted.Add(ContractSerializer.Deserialize<GenerationJob>(jobJson));
        }

        public void Answer(GenerationJob job, JobStatus status) => _ready.Enqueue(new PortMessage(job.RequestId, ContractSerializer.Serialize(status), IsError: false));

        public void AnswerError(string requestId, string json) => _ready.Enqueue(new PortMessage(requestId, json, IsError: true));

        public IReadOnlyList<PortMessage> Receive(long tick)
        {
            var ready = _ready.ToList();
            _ready.Clear();
            return ready;
        }

        public void SetPhase(string phase, long tick, bool force) => Phases.Add((phase, tick, force));
    }
}
