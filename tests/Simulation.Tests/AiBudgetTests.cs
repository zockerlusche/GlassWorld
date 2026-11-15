using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1587 step 2 (B7 R24, R25, R28; B2804, D1161): the day requests per game hour follow from the data, the game's
/// speed and the AI's job time; what is over keeps its question mark and goes out later, what the player raises
/// always goes, and the service's time budget of a request follows the speed.
/// </summary>
public sealed class AiBudgetTests
{
    private const string Thing = "flint";

    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);
    private static readonly Position[] Places = [new(0, 0), new(5000, 5000), new(10000, 0), new(0, 10000)];

    private static ThroughputSettings Throughput(int minPerHour = 1) =>
        new() { DayShare = 0.7, FallbackJobMs = 2000, MinPerHour = minPerHour, AnswerWithinMinutes = 60 };

    private static int Fastest => Content.Calendar.Settings.FastForwardFactors.Max();

    [Fact]
    public void TheShippedDataLimitTheDayRequests()
    {
        Assert.NotNull(Settings().Throughput);
    }

    [Fact]
    public void TheQuotaFollowsTheSpeedAndTheMeasuredJobTime()
    {
        var pace = new AiPace(Content.Calendar.Settings.FastForwardFactors);
        var budget = new AiBudget(Throughput(), pace, Content.Calendar);

        // Normal speed: a game hour is 50 s; 0.7 of it at 2 s a job (not measured yet: the data's fallback).
        Assert.Equal(50.0, budget.RealSecondsPerHour, 6);
        Assert.Equal(17, budget.PerHour);
        pace.Apply(new PlayerCommand(AiPace.SetAiRate, null) { Amount = 1500 });
        Assert.Equal(23, budget.PerHour);

        // The fastest speed: 3.125 s a game hour - one request, and never fewer than min_per_hour.
        pace.Apply(new PlayerCommand(AiPace.SetSpeed, null) { Amount = Fastest });
        Assert.Equal(1, budget.PerHour);
        pace.Apply(new PlayerCommand(AiPace.SetAiRate, null) { Amount = 5000 });
        Assert.Equal(1, budget.PerHour);
        Assert.Equal(0, new AiBudget(Throughput(minPerHour: 0), pace, Content.Calendar).PerHour);

        // Without the data block nothing is limited.
        Assert.Equal(int.MaxValue, new AiBudget(null, pace, Content.Calendar).PerHour);
    }

    [Fact]
    public void TheDeadlineIsTheRealTimeOfTheAnswerWindowAtTheSpeed()
    {
        var pace = new AiPace(Content.Calendar.Settings.FastForwardFactors);
        var budget = new AiBudget(Throughput(), pace, Content.Calendar);
        Assert.Equal(50_000, budget.DeadlineMs(60_000));
        Assert.Equal(20_000, budget.DeadlineMs(20_000));
        pace.Apply(new PlayerCommand(AiPace.SetSpeed, null) { Amount = Fastest });
        Assert.Equal(3_125, budget.DeadlineMs(60_000));
        Assert.Equal(60_000, new AiBudget(null, pace, Content.Calendar).DeadlineMs(60_000));
    }

    [Fact]
    public void OverTheBudgetQuestionsKeepTheirMarkAndGoOutInTheNextHours()
    {
        var w = NewWorld(fastest: true);
        w.SpawnAThingBesideEveryone();
        w.World.Step();

        // One request in the game hour at the fastest speed; the others are open with their question mark.
        Assert.Equal(1, w.Requests);
        Assert.Equal(Places.Length, w.Decisions.Open.Count());
        Assert.Equal(Places.Length - 1, w.Decisions.Open.Count(q => q.RequestId.Length == 0));
        Assert.Equal(3_125, ContractSerializer.Deserialize<DecisionRequest>(w.Decisions.Log.Entries.First(e => e.Kind == DecisionLogKind.Request).Json).DeadlineMs);

        // Each further game hour lets one more go, never two in an hour. A question whose inhabitant walked away from
        // the thing meanwhile lapses as every held question does (A1487: raised anew when the thing is in sight again).
        var hour = Content.Calendar.Settings.TicksPerHour;
        w.World.Run((Places.Length * hour) + 2);
        Assert.True(w.Requests >= 2, $"requests {w.Requests}");
        var ticks = w.Decisions.Log.Entries.Where(e => e.Kind == DecisionLogKind.Request).Select(e => e.Tick).ToList();
        Assert.All(ticks.Zip(ticks.Skip(1)), pair => Assert.True(pair.Second - pair.First >= hour, $"{pair.First} -> {pair.Second}"));
        Assert.All(w.Events.Where(e => e.Kind == "question_failed"), e => Assert.Equal(DecisionsSystem.Lapsed, e.Detail));
    }

    [Fact]
    public void WhatThePlayerRaisesGoesWhateverTheBudget()
    {
        var w = NewWorld(fastest: true);
        w.SpawnAThingBesideEveryone();
        w.World.Step();
        Assert.Equal(1, w.Requests);

        // The player clicks someone whose question waits: being noticed goes out in this hour all the same.
        var waiting = w.Decisions.Open.First(q => q.RequestId.Length == 0).InhabitantId;
        w.Decisions.Submit(new PlayerCommand("inhabitant_clicked", waiting));
        w.World.Step();
        Assert.Equal(2, w.Requests);
        Assert.Contains(w.Decisions.Open, q => q.InhabitantId == waiting && q.Kind == DecisionsSystem.NoticedPlayer && q.RequestId.Length > 0);
    }

    [Fact]
    public void TheSameSeedGivesTheSameHashesWithTheBudget()
    {
        static List<string> Run()
        {
            var w = NewWorld(fastest: true);
            w.SpawnAThingBesideEveryone();
            var hashes = new List<string>();
            for (var t = 0; t < 150; t++)
            {
                w.World.Step();
                hashes.Add(w.World.StateHash());
            }

            return hashes;
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void ASaveGameKeepsTheCountedRequestsOfTheHour()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 1, new SilentPort(), "de", "start_island");
        var limit = 2 * host.World.Calendar.TicksPerDay;
        while (host.World.Tick < limit && !host.Budget.HasState)
        {
            host.World.Step();
        }

        Assert.True(host.Budget.HasState, "the run needs a request within the hour");
        var restored = StateCodec.Restore(new SimulationHostFactory(Content, Settings(), new SilentPort(), "de"), StateCodec.Capture(host));
        Assert.Equal(host.Budget.Capture().Sent, restored.Budget.Capture().Sent);
        Assert.Equal(host.World.StateHash(), restored.World.StateHash());
    }

    [Fact]
    public void TheRankFollowsTheCameraTheDistanceAndTheKind()
    {
        var pace = new AiPace(Content.Calendar.Settings.FastForwardFactors);
        var budget = new AiBudget(Settings().Throughput, pace, Content.Calendar);
        var where = new Dictionary<string, Position> { ["a"] = new(0, 0), ["b"] = new(10, 0), ["c"] = new(100, 0) };
        Position? At(string id) => where.TryGetValue(id, out var p) ? p : null;

        // Nobody followed: the player's 1, a first-time kind 3, the rest 4.
        Assert.Equal(AiBudget.RankPlayer, budget.RankOf("c", DecisionsSystem.NoticedPlayer, At));
        Assert.Equal(AiBudget.RankFirst, budget.RankOf("c", DecisionsSystem.UnknownObject, At));
        Assert.Equal(AiBudget.RankOther, budget.RankOf("c", DecisionsSystem.Leisure, At));

        // The followed one and who is near it rank 2, whatever the kind; one far away does not.
        pace.Apply(new PlayerCommand(AiPace.SetFollowed, "a"));
        Assert.Equal(AiBudget.RankFollowed, budget.RankOf("a", DecisionsSystem.Leisure, At));
        Assert.Equal(AiBudget.RankFollowed, budget.RankOf("b", DecisionsSystem.Leisure, At));
        Assert.Equal(AiBudget.RankOther, budget.RankOf("c", DecisionsSystem.Leisure, At));

        // A followed one no longer in the world ranks nobody (review GW1 #487).
        where.Remove("a");
        Assert.Equal(AiBudget.RankOther, budget.RankOf("b", DecisionsSystem.Leisure, At));
    }

    [Fact]
    public void TheFasterTheGameTheFewerRanksGoToTheAi()
    {
        var pace = new AiPace(Content.Calendar.Settings.FastForwardFactors);
        var budget = new AiBudget(Settings().Throughput, pace, Content.Calendar);
        Assert.Equal(AiBudget.RankOther, budget.UpToRank);
        foreach (var (speed, upTo) in Settings().Throughput!.LevelsBySpeed!.Select(l => (l.Speed, l.UpToLevel)))
        {
            pace.Apply(new PlayerCommand(AiPace.SetSpeed, null) { Amount = speed });
            Assert.Equal(upTo, budget.UpToRank);
        }

        // B2804: at the fastest speed the followed inhabitant still gets the AI, the player always.
        pace.Apply(new PlayerCommand(AiPace.SetSpeed, null) { Amount = Fastest });
        Assert.True(budget.Allows(AiBudget.RankFollowed));
        Assert.True(budget.Allows(AiBudget.RankPlayer));
        Assert.False(budget.Allows(AiBudget.RankOther));
    }

    [Fact]
    public void AtTheFastestSpeedOnlyTheFollowedOneAsksTheOthersKeepTheirMark()
    {
        var w = NewWorld(fastest: true, ranked: true);
        var followed = w.Inhabitants.All.First().Id;
        w.Pace.Apply(new PlayerCommand(AiPace.SetFollowed, followed));
        w.SpawnAThingBesideEveryone();
        w.World.Run((3 * Content.Calendar.Settings.TicksPerHour) + 2);

        // Every request went out for the followed one; the others' questions wait with their mark (or lapsed).
        var asked = w.Decisions.Log.Entries.Where(e => e.Kind == DecisionLogKind.Request).Select(e => ContractSerializer.Deserialize<DecisionRequest>(e.Json).Inhabitant.Id).ToList();
        Assert.NotEmpty(asked);
        Assert.All(asked, id => Assert.Equal(followed, id));
        Assert.Contains(w.Decisions.Open, q => q.InhabitantId != followed && q.RequestId.Length == 0);
    }

    [Fact]
    public void WaitingQuestionsOfTheFollowedOneGoFirst()
    {
        // Normal speed, but a slow AI: one request per game hour. The followed one's question waits behind none.
        var w = NewWorld(fastest: false, ranked: true);
        w.Pace.Apply(new PlayerCommand(AiPace.SetAiRate, null) { Amount = 35_000 });
        w.SpawnAThingBesideEveryone();
        w.World.Step();
        var first = w.Decisions.Open.Single(q => q.RequestId.Length > 0).InhabitantId;
        var followed = w.Decisions.Open.Where(q => q.RequestId.Length == 0).Select(q => q.InhabitantId).Last();
        w.Pace.Apply(new PlayerCommand(AiPace.SetFollowed, followed));

        w.World.Run(Content.Calendar.Settings.TicksPerHour + 2);
        var second = w.Decisions.Log.Entries.Where(e => e.Kind == DecisionLogKind.Request).Skip(1).Select(e => ContractSerializer.Deserialize<DecisionRequest>(e.Json).Inhabitant.Id).FirstOrDefault();
        Assert.NotEqual(first, followed);
        Assert.Equal(followed, second);
    }

    private static DeciderSettings Settings(Action<DeciderSettings>? change = null)
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        change?.Invoke(settings);
        return settings;
    }

    /// <summary>The start inhabitants far apart on an empty plain, a decider that never answers and a budget for day requests.</summary>
    private static TestWorld NewWorld(bool fastest, bool ranked = false)
    {
        var settings = Settings(s =>
        {
            s.MaxRequestsPerStep = null;
            if (!ranked)
            {
                s.Throughput = Throughput();
            }
        });
        var world = new World(1, Content.Calendar);
        var pace = new AiPace(Content.Calendar.Settings.FastForwardFactors);
        if (fastest)
        {
            pace.Apply(new PlayerCommand(AiPace.SetSpeed, null) { Amount = Fastest });
        }

        var budget = new AiBudget(settings.Throughput, pace, world.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour);
        var decisions = new DecisionsSystem(settings, inhabitants, objects, Content.Actions, Content.Brain, Content.Vocabularies[VocabularyKind.Action], new SilentPort(), Content.Inventions.Guard, "de") { Budget = budget };
        world.Add(objects).Add(budget).Add(decisions).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, Places);
        var events = new List<WorldEvent>();
        inhabitants.Reported += events.Add;
        return new TestWorld(world, objects, inhabitants, decisions, events, pace);
    }

    private sealed record TestWorld(World World, WorldObjects Objects, InhabitantsSystem Inhabitants, DecisionsSystem Decisions, List<WorldEvent> Events, AiPace Pace)
    {
        public int Requests => Decisions.Log.Entries.Count(e => e.Kind == DecisionLogKind.Request);

        public void SpawnAThingBesideEveryone()
        {
            foreach (var place in Places)
            {
                Objects.Spawn(Thing, new Position(place.X, place.Z + 10));
            }
        }
    }

    private sealed class SilentPort : IDecisionPort
    {
        public void Send(string requestId, string requestJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];
    }
}
