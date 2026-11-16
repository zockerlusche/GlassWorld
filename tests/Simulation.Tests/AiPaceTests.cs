using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1587 S1 (B2804, D1161): the game's speed, whom the camera follows and how long an AI job takes reach the core
/// only as logged player commands - checked, applied at the next tick, the last of each kind restored with a save
/// game, and outside the state hash.
/// </summary>
public sealed class AiPaceTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "glassworld-tests-" + Path.GetRandomFileName());

    public AiPaceTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static SimulationHost Host() =>
        SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), "de", null, new SimulationHostOptions(false));

    private static PlayerCommand Speed(int? factor) => new(AiPace.SetSpeed, null) { Amount = factor };

    private static PlayerCommand Followed(string? id) => new(AiPace.SetFollowed, id);

    private static PlayerCommand Rate(int? ms) => new(AiPace.SetAiRate, null) { Amount = ms };

    [Fact]
    public void WithoutCommandsTheWorldRunsAtNormalSpeedFollowingNobodyUnmeasured()
    {
        var host = Host();
        host.World.Run(3);
        Assert.Equal((1, null, null), (host.Pace.Speed, host.Pace.FollowedId, host.Pace.JobMs));
    }

    [Fact]
    public void OnlyTheGamesSpeedsAKnownInhabitantAndAMeasurableTimeAreAccepted()
    {
        var host = Host();
        var fast = Content.Value.Calendar.Settings.FastForwardFactors.Max();
        Assert.True(host.Decisions.Submit(Speed(fast)).Accepted);
        Assert.True(host.Decisions.Submit(Speed(1)).Accepted);
        Assert.False(host.Decisions.Submit(Speed(0)).Accepted);
        Assert.False(host.Decisions.Submit(Speed(3)).Accepted);
        Assert.False(host.Decisions.Submit(Speed(null)).Accepted);

        var someone = host.Inhabitants.All.First().Id;
        Assert.True(host.Decisions.Submit(Followed(someone)).Accepted);
        Assert.True(host.Decisions.Submit(Followed(null)).Accepted);
        Assert.False(host.Decisions.Submit(Followed("inh-999999")).Accepted);

        Assert.True(host.Decisions.Submit(Rate(1500)).Accepted);
        Assert.False(host.Decisions.Submit(Rate(0)).Accepted);
        Assert.False(host.Decisions.Submit(Rate(AiPace.MaxJobMs + 1)).Accepted);
    }

    [Fact]
    public void TheCommandsTakeEffectAtTheNextTick()
    {
        var host = Host();
        var someone = host.Inhabitants.All.First().Id;
        var fast = Content.Value.Calendar.Settings.FastForwardFactors.Max();
        host.Decisions.Submit(Speed(fast));
        host.Decisions.Submit(Followed(someone));
        host.Decisions.Submit(Rate(1500));
        Assert.Equal(1, host.Pace.Speed);

        host.World.Step();
        Assert.Equal((fast, someone, 1500), (host.Pace.Speed, host.Pace.FollowedId, host.Pace.JobMs));
    }

    [Fact]
    public void TheValuesThemselvesAreNoWorldState()
    {
        // Who the camera follows changes nothing by itself (it ranks requests in step 3): the hash stays. Speed and
        // job time pace the AI (AiBudgetTests), so their effects are in the world, not the values.
        var host = Host();
        var plain = Host();
        host.Decisions.Submit(Followed(host.Inhabitants.All.First().Id));
        host.World.Step();
        plain.World.Step();
        Assert.Equal(plain.World.StateHash(), host.World.StateHash());
    }

    [Fact]
    public void ASaveGameGoesOnWithTheLastLoggedValues()
    {
        var host = Host();
        var someone = host.Inhabitants.All.First().Id;
        var fast = Content.Value.Calendar.Settings.FastForwardFactors.Max();
        host.Decisions.Submit(Speed(fast));
        host.Decisions.Submit(Followed(someone));
        host.Decisions.Submit(Rate(2000));
        host.World.Run(2);
        host.Decisions.Submit(Speed(1));
        host.Decisions.Submit(Rate(1200));
        host.World.Run(2);

        var path = Path.Combine(_directory, "pace.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loaded = Restore(SaveGameStore.Load(path));
        Assert.Equal((1, someone, 1200), (loaded.Pace.Speed, loaded.Pace.FollowedId, loaded.Pace.JobMs));
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());

        // Nobody followed any more: also that is restored.
        loaded.Decisions.Submit(Followed(null));
        loaded.World.Step();
        SaveGameStore.Save(path, StateCodec.Capture(loaded));
        Assert.Null(Restore(SaveGameStore.Load(path)).Pace.FollowedId);
    }

    private static SimulationHost Restore(SaveState state)
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return StateCodec.Restore(new SimulationHostFactory(Content.Value, settings, new MockDecisionPort(), "de"), state);
    }
}
