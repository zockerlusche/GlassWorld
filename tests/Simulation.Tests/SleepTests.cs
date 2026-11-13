using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B2681 (W1938, Marcel live): sleep works while sleeping (the value rises instead of sinking and jumping at the
/// end), a pressing need wakes a sleeper (nobody dies of thirst in its sleep), and an answer asked for while sleepy
/// is not carried out once rested. Values come from the data.
/// </summary>
public sealed class SleepTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);
    private static readonly string SleepAction = Content.InhabitantModel.Togetherness!.SleepAction;
    private static readonly double SleepNeglect = Content.InhabitantModel.Needs.Single(n => n.Id == "sleep").NeglectBelow;

    [Fact]
    public void SleepRisesWhileSleepingAndAWholeNightFillsIt()
    {
        var (host, sleeper) = Alone();
        sleeper.Needs["sleep"] = 0.4;
        Start(host, sleeper);
        var duration = host.Content.Actions.Timing(SleepAction).DurationTicks;

        host.World.Run(duration / 2);
        Assert.True(host.Inhabitants.IsAsleep(sleeper));
        Assert.True(sleeper.Needs["sleep"] > 0.6, $"half a night: sleep {sleeper.Needs["sleep"]:0.000}");

        host.World.Run((duration / 2) + 2);
        Assert.False(host.Inhabitants.IsAsleep(sleeper));
        Assert.True(sleeper.Needs["sleep"] >= 0.95, $"a night: sleep {sleeper.Needs["sleep"]:0.000}");
    }

    [Fact]
    public void ThirstBelowItsThresholdWakesTheSleeperAndWhatItSleptStays()
    {
        var (host, sleeper) = Alone();
        var thirst = Content.InhabitantModel.Needs.Single(n => n.Id == "thirst");
        sleeper.Needs["sleep"] = 0.2;
        sleeper.Needs["thirst"] = thirst.NeglectBelow + 0.02;
        Start(host, sleeper);
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;

        bool Woken(WorldEvent e) => e.Kind == InhabitantsSystem.ActionCancelled && e.InhabitantId == sleeper.Id && e.Action == SleepAction && e.Detail!.StartsWith(InhabitantsSystem.WokenByNeed, StringComparison.Ordinal);
        var limit = host.Content.Actions.Timing(SleepAction).DurationTicks;
        for (var i = 0; i < limit && !events.Any(Woken); i++)
        {
            host.World.Step();
        }

        var woken = Assert.Single(events, Woken);
        Assert.Equal(InhabitantsSystem.WokenByNeed + "/thirst", woken.Detail);
        Assert.DoesNotContain(events, e => e.Kind == InhabitantsSystem.ActionFinished && e.Action == SleepAction);
        Assert.True(sleeper.Needs["sleep"] > 0.2, "the hours it slept count");

        // Awake, still thirsty and not exhausted, it does not lie down again (no sleep-wake loop without water):
        // the next hours while its sleep stays above the exhaustion threshold.
        var exhausted = Content.InhabitantModel.Needs.Single(n => n.Id == "sleep").NeglectBelow;
        for (var i = 0; i < 2 * host.World.Calendar.Settings.TicksPerHour && sleeper.Needs["sleep"] >= exhausted; i++)
        {
            host.World.Step();
        }

        Assert.DoesNotContain(events, e => e.Kind == InhabitantsSystem.ActionStarted && e.Action == SleepAction && e.Tick > woken.Tick);
    }

    [Fact]
    public void AQueuedSleepIsDroppedWhenTheInhabitantIsRested()
    {
        var (host, rested) = Alone();
        rested.Needs["sleep"] = 0.96;
        rested.Planned = new Activity(SleepAction, null, rested.Position, null, DecidedTick: host.World.Tick);
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;

        host.World.Step();
        Assert.Contains(events, e => e.Kind == InhabitantsSystem.AnswerExpired && e.Action == SleepAction && e.Detail == InhabitantsSystem.NeedMet);
        Assert.False(host.Inhabitants.IsAsleep(rested));
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    public void ThreeDaysNoSleeperBelowThePressingThresholdsAndNoSleepWhenRested(ulong seed)
    {
        // The TL's check (#22587): over three days nobody sleeps on with a pressing need below its threshold (one
        // tick of decay as tolerance), and no sleep starts while the sleep need is above the point where the
        // routine stops offering it.
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, seed, new MockDecisionPort(), "de", "start_island");
        var pressing = Content.InhabitantModel.Needs.Where(n => n.NeglectBelow > 0 && n.Id != "sleep").ToList();
        var perTick = pressing.ToDictionary(n => n.Id, n => n.DecayPerHour / host.World.Calendar.Settings.TicksPerHour);
        var sleepStarts = new List<double>();
        host.Inhabitants.Reported += e =>
        {
            if (e.Kind == InhabitantsSystem.ActionStarted && e.Action == SleepAction)
            {
                sleepStarts.Add(host.Inhabitants.All.Single(i => i.Id == e.InhabitantId).Needs["sleep"]);
            }
        };

        var violations = new List<string>();
        for (var t = 0; t < host.World.Calendar.TicksPerDay * 3; t++)
        {
            host.World.Step();
            // One exhausted itself (sleep below its own threshold) sleeps until it is not; then, after the data's
            // least minutes of sleep (wake_after_minutes), it is woken.
            var timing = host.Content.Actions.Timing(SleepAction);
            foreach (var sleeper in host.Inhabitants.All.Where(i => host.Inhabitants.IsAsleep(i) && i.Needs["sleep"] >= SleepNeglect
                && timing.DurationTicks - i.Activity!.RemainingTicks!.Value > timing.WakeAfterTicks))
            {
                violations.AddRange(pressing.Where(n => sleeper.Needs[n.Id] < n.NeglectBelow - perTick[n.Id] - 1e-9).Select(n => $"{sleeper.Id} asleep with {n.Id} {sleeper.Needs[n.Id]:0.000} at tick {host.World.Tick}"));
            }
        }

        Assert.Empty(violations.Take(5));
        Assert.NotEmpty(sleepStarts);
        Assert.All(sleepStarts, s => Assert.True(s <= 0.5, $"a sleep started at sleep {s:0.000}"));
    }

    private static (SimulationHost Host, Inhabitant Sleeper) Alone()
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 3, new MockDecisionPort(), "de");
        var model = Content.InhabitantModel;
        var sleeper = new Inhabitant(
            "inh-000001",
            "female",
            1,
            new Position(0, 0),
            model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
            model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal),
            model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal),
            model.WillToLive.Start);
        host.Inhabitants.Add(sleeper);
        return (host, sleeper);
    }

    /// <summary>Puts the inhabitant to sleep as the routine would start it (the countdown begins on the next step).</summary>
    private static void Start(SimulationHost host, Inhabitant sleeper)
    {
        sleeper.Activity = new Activity(SleepAction, null, sleeper.Position, null);
        host.World.Step();
        Assert.True(host.Inhabitants.IsAsleep(sleeper));
    }
}
