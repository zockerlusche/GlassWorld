using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Player;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// The player part of the core (A1340, F5238): futile effort, the check "really missing", calls for
/// help, favour and gifts; all values from content/player.
/// </summary>
public sealed class PlayerTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gw-player-" + Guid.NewGuid().ToString("N"));

    public PlayerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void FutileEffortOpensACallWhenTheNeededThingIsReallyMissing()
    {
        var host = WorldWithThirsty(out var inhabitant);
        var call = RunUntil(host, PlayerSystem.HelpNeeded, days: 2, detail: "thirst");

        Assert.NotNull(call);
        Assert.Equal(inhabitant.Id, call.InhabitantId);
        Assert.Equal("thirst", call.Detail);
        Assert.Single(host.Player.Open, c => c.InhabitantId == inhabitant.Id && c.Need == "thirst");
    }

    [Fact]
    public void NoCallWhileTheNeededThingIsReachable()
    {
        // Out of sight (perception 60 m) but within the search distance of the data: search on, no help.
        var host = WorldWithThirsty(out _);
        host.Objects.Spawn("spring_water", new Position(80, 0));
        // (Hunger becomes pressing after about a day and food is really missing here: that call is right.)
        Assert.Null(RunUntil(host, PlayerSystem.HelpNeeded, days: 2, detail: "thirst"));
        Assert.DoesNotContain(host.Player.Open, c => c.Need == "thirst");
    }

    [Fact]
    public void SaltWaterDoesNotCountAsHelp()
    {
        // The truth check respects what to avoid: sea water nearby does not quench thirst.
        var host = WorldWithThirsty(out _);
        host.Objects.Spawn("sea_water", new Position(80, 0));
        Assert.NotNull(RunUntil(host, PlayerSystem.HelpNeeded, days: 2, detail: "thirst"));
    }

    [Fact]
    public void AGiftCostsFavourAndAnyoneCanTakeIt()
    {
        var host = WorldWithThirsty(out _);
        var before = host.Player.Favor;
        var command = new PlayerCommand(PlayerSystem.PlaceGift, null) { TypeId = "spring_water", X = 5, Z = 0 };
        Assert.True(host.Decisions.Submit(command).Accepted);
        host.World.Step();

        var placed = Assert.Single(host.Inhabitants.Events, e => e.Kind == PlayerSystem.GiftPlaced);
        Assert.Equal("spring_water", placed.Detail);
        Assert.True(host.Objects.TryGet(placed.TargetId!, out var gift));
        Assert.Equal("spring_water", gift.TypeId);
        var regrow = host.Content.Favor.RegrowPerHour / (double)host.World.Calendar.Settings.TicksPerHour;
        Assert.Equal(before - host.Player.CostOf("gift") + regrow, host.Player.Favor, 9);
    }

    [Theory]
    [InlineData("spring_water", 0.5, PlayerSystem.ReasonTooClose)]
    [InlineData("dragon_egg", 5.0, PlayerSystem.ReasonUnknownType)]
    public void GiftsAreCheckedByTheCore(string type, double x, string reason)
    {
        var host = WorldWithThirsty(out _);
        var result = host.Decisions.Submit(new PlayerCommand(PlayerSystem.PlaceGift, null) { TypeId = type, X = x, Z = 0 });
        Assert.False(result.Accepted);
        Assert.Equal(reason, result.ReasonKey);
    }

    [Fact]
    public void WithoutEnoughFavourNoGift()
    {
        var host = WorldWithThirsty(out _);
        host.Player.SetStartFavor(host.Content.Favor.StartOptions.Min());
        var gifts = 0;
        for (var i = 0; i < 20; i++)
        {
            if (host.Decisions.Submit(new PlayerCommand(PlayerSystem.PlaceGift, null) { TypeId = "apple", X = 5 + i, Z = 0 }).Accepted)
            {
                host.World.Step();
                gifts++;
            }
        }

        Assert.Equal((int)(host.Content.Favor.StartOptions.Min() / host.Player.CostOf("gift")), gifts);
        Assert.Equal(PlayerSystem.ReasonNoFavor, host.Decisions.Submit(new PlayerCommand(PlayerSystem.PlaceGift, null) { TypeId = "apple", X = 30, Z = 0 }).ReasonKey);
    }

    [Fact]
    public void FavourGrowsBackToTheMaximum()
    {
        var host = WorldWithThirsty(out _);
        host.Player.SetStartFavor(host.Content.Favor.StartOptions.Min());
        host.World.Run(host.World.Calendar.TicksPerDay * 10);
        Assert.Equal(host.Content.Favor.Maximum, host.Player.Favor);
    }

    [Fact]
    public void SaveAndLoadKeepThePlayerState()
    {
        var host = WorldWithThirsty(out _);
        host.Decisions.Submit(new PlayerCommand(PlayerSystem.PlaceGift, null) { TypeId = "apple", X = 5, Z = 0 });
        Assert.NotNull(RunUntil(host, PlayerSystem.HelpNeeded, days: 2));
        var path = Path.Combine(_directory, "world.sqlite");
        SaveGameStore.Save(path, StateCodec.Capture(host));

        var settings = Contracts.Validation.ContractSerializer.Deserialize<Contracts.DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        var loaded = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new ReplayDecisionPort(host.Decisions.Log), "de"), SaveGameStore.Load(path));
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(host.Player.Favor, loaded.Player.Favor);
        Assert.Equal(host.Player.Open, loaded.Player.Open);
    }

    [Fact]
    public void EffortThenCallThenGiftThenSomeoneUsesIt()
    {
        // A1340 criterion 1: futile effort -> call for help -> the player places a gift -> an inhabitant
        // perceives and uses it. A second, curious inhabitant may take it first (F5238).
        var host = WorldWithThirsty(out var thirsty);
        var needs = host.Content.InhabitantModel.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal);
        host.Inhabitants.Add(new Inhabitant("inh-000002", "male", 1, new Position(3, 0), needs, new Dictionary<string, double>(StringComparer.Ordinal), new Dictionary<string, double>(StringComparer.Ordinal), 0.8));
        var call = RunUntil(host, PlayerSystem.HelpNeeded, days: 2, detail: "thirst");
        Assert.NotNull(call);

        var at = thirsty.Position;
        var result = host.Decisions.Submit(new PlayerCommand(PlayerSystem.PlaceGift, null) { TypeId = "spring_water", X = at.X, Z = at.Z + 4 });
        Assert.True(result.Accepted, $"{result.ReasonKey} at {at}, other at {host.Inhabitants.Get("inh-000002").Position}, favour {host.Player.Favor}");
        var placed = RunUntil(host, PlayerSystem.GiftPlaced, days: 0.01);
        Assert.NotNull(placed);

        var used = RunUntil(host, InhabitantsSystem.ActionStarted, days: 1, target: placed.TargetId);
        Assert.NotNull(used);
        Assert.True(used.InhabitantId is "inh-000001" or "inh-000002");
    }

    [Fact]
    public void StartFavourIsPartOfTheStartState()
    {
        // GW1 review #67 S1: the same world code with another favour is another start state.
        var low = WorldWithThirsty(out _);
        var high = WorldWithThirsty(out _);
        low.Player.SetStartFavor(low.Content.Favor.StartOptions.Min());
        high.Player.SetStartFavor(high.Content.Favor.StartOptions.Max());
        Assert.NotEqual(low.World.StateHash(), high.World.StateHash());
    }

    [Fact]
    public void StartFavourComesBackFromTheLog()
    {
        // GW1 review #67 N1: a replay from seed and log alone starts with the chosen favour.
        var original = WorldWithThirsty(out _);
        var low = original.Content.Favor.StartOptions.Min();
        Assert.True(original.Decisions.Submit(new PlayerCommand(PlayerSystem.StartFavorCommand, null) { Amount = low }).Accepted);
        original.World.Run(100);

        var replay = WorldWithThirsty(out _);
        replay.Decisions.LoadReplay(original.Decisions.Log);
        replay.World.Run(100);
        Assert.Equal(original.World.StateHash(), replay.World.StateHash());
        Assert.Equal(PlayerSystem.ReasonAlreadyStarted, original.Decisions.Submit(new PlayerCommand(PlayerSystem.StartFavorCommand, null) { Amount = low }).ReasonKey);
    }

    [Fact]
    public void StartFavourIsInTheLogOfASaveMadeBeforeTheFirstTick()
    {
        // GW1 #73 F1: the world is saved right after creation; its log must already hold the favour.
        var original = WorldWithThirsty(out _);
        var low = original.Content.Favor.StartOptions.Min();
        Assert.True(original.Decisions.ApplySetup(original.World, new PlayerCommand(PlayerSystem.StartFavorCommand, null) { Amount = low }).Accepted);
        Assert.Equal(low, original.Player.Favor);
        Assert.Single(original.Decisions.Log.Entries, e => e.Kind == DecisionLogKind.Command && e.Tick == 0);
        original.World.Run(100);

        var replay = WorldWithThirsty(out _);
        replay.Decisions.LoadReplay(original.Decisions.Log);
        replay.World.Run(100);
        Assert.Equal(original.World.StateHash(), replay.World.StateHash());
    }

    [Fact]
    public void AReplayedGiftGivesTheSameWorld()
    {
        // GW1 review #67 T1: the command comes back from the log at the same tick.
        var original = WorldWithThirsty(out _);
        original.World.Run(10);
        original.Decisions.Submit(new PlayerCommand(PlayerSystem.PlaceGift, null) { TypeId = "apple", X = 6, Z = 2 });
        original.World.Run(500);

        var replay = WorldWithThirsty(out _);
        replay.Decisions.LoadReplay(original.Decisions.Log);
        replay.World.Run(510);
        Assert.Equal(original.World.StateHash(), replay.World.StateHash());
        Assert.Contains(replay.Objects.All, o => o.TypeId == "apple");
    }

    [Fact]
    public void TheDataDecidesWhenEffortCounts()
    {
        // GW1 review #67 T1: with a lower threshold for "pressing", the same thirst never counts.
        var host = WorldWithThirsty(out var inhabitant);
        inhabitant.Needs["thirst"] = host.Content.GiftRules.UrgentBelow + 0.01;
        Assert.Null(RunUntil(host, PlayerSystem.HelpNeeded, days: 0.1, detail: "thirst"));
    }

    private static SimulationHost WorldWithThirsty(out Inhabitant inhabitant)
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 1, new MockDecisionPort(), "de");
        var needs = host.Content.InhabitantModel.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal);
        needs["thirst"] = 0.05;
        inhabitant = new Inhabitant("inh-000001", "female", 1, new Position(0, 0), needs, new Dictionary<string, double>(StringComparer.Ordinal), new Dictionary<string, double>(StringComparer.Ordinal), 0.8);
        host.Inhabitants.Add(inhabitant);
        return host;
    }

    private static WorldEvent? RunUntil(SimulationHost host, string kind, double days, string? detail = null, string? target = null)
    {
        var seq = host.Inhabitants.LastEventSeq;
        for (long t = 0; t < (long)(host.World.Calendar.TicksPerDay * days); t++)
        {
            host.World.Step();
            var found = host.Inhabitants.EventsSince(seq).Select(e => e.Event).FirstOrDefault(e => e.Kind == kind && (detail is null || e.Detail == detail) && (target is null || e.TargetId == target));
            if (found is not null)
            {
                return found;
            }

            seq = host.Inhabitants.LastEventSeq;
        }

        return null;
    }
}
