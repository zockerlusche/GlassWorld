using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Look;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B2-5 R16: the look epoch as core state (hash, save game, snapshot), set by a logged command that is
/// checked against the data and the reached tech level; the debug command only in debug mode.
/// </summary>
public sealed class LookEpochTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public LookEpochTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static List<LookEpoch> Epochs() => [.. Content.Value.LookEpochs.Epochs];

    private static string First() => Epochs()[0].Id;

    /// <summary>The highest epoch the world has reached that is not the first one.</summary>
    private static string Reached() => Epochs().Last(e => e.TechLevel <= Content.Value.Inventions.ReachedTechLevel && e.Id != First()).Id;

    /// <summary>The first epoch above the reached tech level.</summary>
    private static string Beyond() => Epochs().First(e => e.TechLevel > Content.Value.Inventions.ReachedTechLevel).Id;

    private static SimulationHost Host(ulong seed = 5, bool debug = false, IDecisionPort? port = null) =>
        SimulationHost.CreatePlaceholderWorld(ContentDirectory, seed, port ?? new MockDecisionPort(), "de", null, new SimulationHostOptions(debug));

    private static PlayerCommand Set(string? epoch) => new(LookEpochSystem.SetLookEpoch, null) { TypeId = epoch };

    private static PlayerCommand DebugSet(string? epoch) => new(LookEpochSystem.DebugSetLookEpoch, null) { TypeId = epoch };

    [Fact]
    public void TheReachedTechLevelIsThePositionOfTheWorldsTechLevelInItsVocabulary()
    {
        var rules = Content.Value.Inventions;
        Assert.Equal(rules.Settings.TechLevel, rules.TechLevels[rules.ReachedTechLevel]);
        Assert.Equal(Content.Value.Vocabularies[VocabularyKind.Tech_level].Entries.Select(e => e.Id), rules.TechLevels);

        // The epochs count the tech levels: every level of the vocabulary has its epoch, in the same order.
        Assert.Equal(Enumerable.Range(0, rules.TechLevels.Count), Epochs().Select(e => e.TechLevel));
        Assert.All(Epochs(), e => Assert.Contains(e.Id, Content.Value.Vocabularies[VocabularyKind.Epoch].Entries.Select(v => v.Id)));
    }

    [Fact]
    public void ANewWorldStartsInTheFirstEpochAndItsHashDoesNotKnowTheEpochYet()
    {
        var host = Host();
        Assert.Equal(First(), host.LookEpoch.Current);
        Assert.Equal(First(), host.Snapshot().LookEpoch);
        Assert.False(host.LookEpoch.HasState);
        Assert.Null(StateCodec.Capture(host).LookEpoch);
        Assert.False(host.Options.DebugCommands);
    }

    [Fact]
    public void TheCommandSetsAReachedEpochAtTheNextTickAndIsLogged()
    {
        var host = Host();
        var untouched = Host();
        host.World.Run(3);
        untouched.World.Run(4);
        Assert.True(host.Decisions.Submit(Set(Reached())).Accepted);
        Assert.Equal(First(), host.LookEpoch.Current);
        var changes = new List<(string Old, string New, long Tick)>();
        host.LookEpoch.Changed += (old, now, tick) => changes.Add((old, now, tick));
        host.World.Step();

        Assert.Equal(Reached(), host.LookEpoch.Current);
        Assert.Equal(Reached(), host.Snapshot().LookEpoch);
        Assert.Equal([(First(), Reached(), 3L)], changes);
        var reported = Assert.Single(host.Inhabitants.Events, e => e.Kind == LookEpochSystem.EpochChanged);
        Assert.Equal((Reached(), 3L), (reported.Detail, reported.Tick));
        Assert.Contains(host.Decisions.Log.Entries, e => e.Kind == DecisionLogKind.Command && e.Tick == 3 && e.Json.Contains(LookEpochSystem.SetLookEpoch, StringComparison.Ordinal));

        // The epoch is part of the state now.
        Assert.NotEqual(untouched.World.StateHash(), host.World.StateHash());

        // Back in the first epoch the world hashes as if it had never left.
        Assert.True(host.Decisions.Submit(Set(First())).Accepted);
        host.World.Step();
        untouched.World.Step();
        Assert.Equal(untouched.World.StateHash(), host.World.StateHash());
    }

    [Fact]
    public void AnEpochAboveTheReachedTechLevelOrOutsideTheDataIsRefused()
    {
        var host = Host();
        Assert.Equal(LookEpochSystem.ReasonNotReached, host.Decisions.Submit(Set(Beyond())).ReasonKey);
        Assert.Equal(LookEpochSystem.ReasonUnknownEpoch, host.Decisions.Submit(Set("space_age")).ReasonKey);
        Assert.Equal(LookEpochSystem.ReasonUnknownEpoch, host.Decisions.Submit(Set(null)).ReasonKey);
        host.World.Step();
        Assert.Equal(First(), host.LookEpoch.Current);
        Assert.DoesNotContain(host.Decisions.Log.Entries, e => e.Kind == DecisionLogKind.Command);
        Assert.All(Epochs(), e => Assert.Equal(e.TechLevel <= host.LookEpoch.ReachedTechLevel, host.LookEpoch.IsReached(e.Id)));
    }

    [Fact]
    public void TheDebugCommandIsRefusedUnlessTheHostRunsInDebugMode()
    {
        var host = Host();
        Assert.Equal(LookEpochSystem.ReasonDebugOff, host.Decisions.Submit(DebugSet(Beyond())).ReasonKey);
        Assert.Equal(LookEpochSystem.ReasonDebugOff, host.Decisions.Submit(DebugSet(Reached())).ReasonKey);
        host.World.Step();
        Assert.Equal(First(), host.LookEpoch.Current);

        var debug = Host(debug: true);
        Assert.True(debug.Options.DebugCommands);
        Assert.Equal(LookEpochSystem.ReasonUnknownEpoch, debug.Decisions.Submit(DebugSet("space_age")).ReasonKey);
        Assert.True(debug.Decisions.Submit(DebugSet(Beyond())).Accepted);
        debug.World.Step();
        Assert.Equal(Beyond(), debug.LookEpoch.Current);

        // Debug mode does not loosen the normal command.
        Assert.Equal(LookEpochSystem.ReasonNotReached, debug.Decisions.Submit(Set(Epochs()[^1].Id)).ReasonKey);
    }

    [Fact]
    public void AReplayFollowsTheLoggedEpochCommandsEvenWithoutDebugMode()
    {
        var original = Host(debug: true);
        original.World.Run(20);
        Assert.True(original.Decisions.Submit(Set(Reached())).Accepted);
        original.World.Run(20);
        Assert.True(original.Decisions.Submit(DebugSet(Beyond())).Accepted);
        original.World.Run(20);
        Assert.Equal(Beyond(), original.LookEpoch.Current);

        var replay = Host(debug: false, port: new ReplayDecisionPort(original.Decisions.Log));
        replay.Decisions.LoadReplay(original.Decisions.Log);
        replay.World.Run(21);
        Assert.Equal(Reached(), replay.LookEpoch.Current);
        replay.World.Run(original.World.Tick - 21);
        Assert.Equal(Beyond(), replay.LookEpoch.Current);
        Assert.Equal(original.Decisions.Log.Entries, replay.Decisions.Log.Entries);
        Assert.Equal(original.World.StateHash(), replay.World.StateHash());
    }

    [Fact]
    public void TheEpochSurvivesSaveAndLoadWithTheSameHash()
    {
        var host = Host();
        host.World.Run(5);
        host.Decisions.Submit(Set(Reached()));
        host.World.Run(5);

        var path = Path.Combine(_directory, "epoch.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var state = SaveGameStore.Load(path);
        Assert.Equal(Reached(), state.LookEpoch);
        var loaded = Restore(state);
        Assert.Equal(Reached(), loaded.LookEpoch.Current);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());

        loaded.World.Run(10);
        host.World.Run(10);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
    }

    [Fact]
    public void ASaveFromBeforeTheEpochLoadsInTheFirstEpoch()
    {
        var host = Host();
        host.World.Run(10);
        var path = Path.Combine(_directory, "old.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));

        // A world in its first epoch writes no entry, exactly like every save game from before.
        var state = SaveGameStore.Load(path);
        Assert.Null(state.LookEpoch);
        var loaded = Restore(state);
        Assert.Equal(First(), loaded.LookEpoch.Current);
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
    }

    [Fact]
    public void ASaveWithAnEpochTheDataDoNotHaveIsRefused()
    {
        var host = Host();
        host.World.Run(2);
        var state = StateCodec.Capture(host) with { LookEpoch = "space_age" };
        Assert.Throws<InvalidDataException>(() => Restore(state));
    }

    private static SimulationHost Restore(SaveState state)
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return StateCodec.Restore(new SimulationHostFactory(Content.Value, settings, new MockDecisionPort(), "de"), state);
    }
}
