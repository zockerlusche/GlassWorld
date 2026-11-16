using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B2680 core part (Marcel W1939): in the compute night the world sleeps. At bedtime (hours after nightfall, data) an
/// awake inhabitant lies down unless a pressing need keeps it awake (B2681); the service hears 'night' when everyone
/// sleeps, at the latest the data's hours after nightfall - the same limit as the game's night scene.
/// </summary>
public sealed class BedtimeTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);
    private static readonly double Bedtime = Content.InhabitantModel.Togetherness!.BedtimeAfterNightfallHours!.Value;
    private static readonly double Wait = Content.Inventions.Build.WaitForSleepersHours!.Value;

    [Fact]
    public void TheCoreWaitsAsLongAsTheNightScene()
    {
        var look = ContractSerializer.Deserialize<LookNight>(File.ReadAllText(Path.Combine(ContentDirectory, "look", "night.json")));
        Assert.Equal((double)look.WaitForSleepersMaxH!, Wait, 6);
        Assert.True(Bedtime < Wait, "everyone awake lies down before the scene begins at the latest");
    }

    [Fact]
    public void MinutesSinceNightfallCountOverMidnight()
    {
        var calendar = new World(1, Content.Calendar).Calendar;
        var tick = FirstNightTick(calendar);
        Assert.Equal(0, calendar.MinutesSinceNightfall(tick));
        Assert.Equal(90, calendar.MinutesSinceNightfall(tick + (90 * calendar.Settings.TicksPerHour / 60)));
        var midnight = ((tick / calendar.TicksPerDay) + 1) * calendar.TicksPerDay;
        Assert.True(calendar.MinutesSinceNightfall(midnight) > calendar.MinutesSinceNightfall(midnight - 1));
        Assert.Null(calendar.MinutesSinceNightfall(tick - 1));
    }

    [Fact]
    public void AtBedtimeTheRestedLieDownAndTheThirstyStayAwake()
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 9, new MockDecisionPort(), "de");
        var rested = Person("inh-000001", new Position(0, 0));
        var thirsty = Person("inh-000002", new Position(20, 0));
        thirsty.Needs["thirst"] = 0.01;
        host.Inhabitants.Add(rested);
        host.Inhabitants.Add(thirsty);
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;

        var bedtime = FirstNightTick(host.World.Calendar) + (long)(Bedtime * host.World.Calendar.Settings.TicksPerHour);
        host.World.Run(bedtime - 1 - host.World.Tick);

        // Just before bedtime: one rested and idle, one rested but thirsty.
        foreach (var who in new[] { rested, thirsty })
        {
            foreach (var need in who.Needs.Keys.ToList())
            {
                who.Needs[need] = 1.0;
            }

            who.Activity = null;
            who.Planned = null;
        }

        thirsty.Needs["thirst"] = 0.01;
        events.Clear();

        // Two hours: one busy with something lies down when that is done (still before the limit of the scene).
        host.World.Run(2 * host.World.Calendar.Settings.TicksPerHour);
        var lay = events.Where(e => e.Kind == InhabitantsSystem.ActionStarted && e.Action == "sleep" && e.Tick >= bedtime).ToList();
        Assert.Contains(lay, e => e.InhabitantId == rested.Id && e.Detail == InhabitantsSystem.Bedtime);
        Assert.DoesNotContain(lay, e => e.InhabitantId == thirsty.Id);
        Assert.True(host.Inhabitants.IsAsleep(rested));
    }

    [Fact]
    public void AfterTheWindowNobodyIsSentBackToBedRested()
    {
        // A1711 (D1112 #4): woken rested in the small hours, it stays up; only tiredness would lie it down.
        var window = Content.InhabitantModel.Togetherness!.BedtimeWindowHours!.Value;
        var host = SimulationHost.CreateEmpty(ContentDirectory, 9, new MockDecisionPort(), "de");
        var rested = Person("inh-000001", new Position(0, 0));
        host.Inhabitants.Add(rested);
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        var ticksPerHour = host.World.Calendar.Settings.TicksPerHour;
        var late = FirstNightTick(host.World.Calendar) + (long)((Bedtime + window) * ticksPerHour);
        host.World.Run(late - host.World.Tick);
        Assert.True(host.Inhabitants.AtBedtime(host.World) is false);
        Assert.NotNull(host.World.Calendar.MinutesSinceNightfall(host.World.Tick)); // still night

        foreach (var need in rested.Needs.Keys.ToList())
        {
            rested.Needs[need] = 1.0;
        }

        rested.Activity = null;
        rested.Planned = null;
        events.Clear();
        host.World.Run(ticksPerHour);
        Assert.DoesNotContain(events, e => e.Kind == InhabitantsSystem.ActionStarted && e.Action == "sleep" && e.Detail == InhabitantsSystem.Bedtime);
    }

    [Fact]
    public void ByDayASleeperWakesOnceRestedAtNightItSleepsOn()
    {
        // B2778 (A1711): a nap ends when the sleeper is rested; the night's sleep keeps its length.
        var rested = Content.InhabitantModel.Togetherness!.WakeRested!;
        var host = SimulationHost.CreateEmpty(ContentDirectory, 9, new MockDecisionPort(), "de");
        var napper = Person("inh-000001", new Position(0, 0));
        host.Inhabitants.Add(napper);
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        var calendar = host.World.Calendar;
        var noon = FirstNightTick(calendar) - (6 * calendar.Settings.TicksPerHour);
        host.World.Run(noon - host.World.Tick);
        Assert.Null(calendar.MinutesSinceNightfall(host.World.Tick));

        foreach (var need in napper.Needs.Keys.ToList())
        {
            napper.Needs[need] = 1.0;
        }

        napper.Needs[rested.Need] = rested.At - 0.05;
        napper.Activity = new Activity("sleep", null, napper.Position, null);
        napper.Planned = null;
        double? atWaking = null;
        host.Inhabitants.Reported += e =>
        {
            if (e.InhabitantId == napper.Id && e.Detail == InhabitantsSystem.WokenRested)
            {
                atWaking ??= napper.Needs[rested.Need];
            }
        };
        host.World.Run(3 * calendar.Settings.TicksPerHour);
        Assert.Contains(events, e => e.Kind == InhabitantsSystem.ActionCancelled && e.Detail == InhabitantsSystem.WokenRested && e.InhabitantId == napper.Id);
        Assert.True(atWaking >= rested.At, $"woken at {atWaking:0.000}");

        // At night the same sleeper sleeps on, rested or not, until the morning (sleep_through_night).
        var night = FirstNightTick(calendar) + (3 * calendar.Settings.TicksPerHour);
        host.World.Run(night - host.World.Tick);
        napper.Needs[rested.Need] = 1.0;
        napper.Activity = new Activity("sleep", null, napper.Position, null);
        host.World.Run(2 * calendar.Settings.TicksPerHour);
        Assert.True(host.Inhabitants.IsAsleep(napper));
    }

    [Fact]
    public void ByDayANapEndsAfterTheDataHoursEvenIfNotRested()
    {
        // A1498 (B2828, Marcel 15a: Kira slept six hours of the afternoon): a nap by day ends after max_nap_hours.
        var rested = Content.InhabitantModel.Togetherness!.WakeRested!;
        var host = SimulationHost.CreateEmpty(ContentDirectory, 9, new MockDecisionPort(), "de");
        var napper = Person("inh-000001", new Position(0, 0));
        host.Inhabitants.Add(napper);
        var calendar = host.World.Calendar;
        var noon = FirstNightTick(calendar) - (7 * calendar.Settings.TicksPerHour);
        host.World.Run(noon - host.World.Tick);
        foreach (var need in napper.Needs.Keys.ToList())
        {
            napper.Needs[need] = 1.0;
        }

        // Very tired, but not below the line that wakes for another need: only the nap's hours end it.
        napper.Needs[rested.Need] = 0.3;
        napper.Activity = new Activity("sleep", null, napper.Position, null);
        napper.Planned = null;
        long? wokeAt = null;
        double? atWaking = null;
        host.Inhabitants.Reported += e =>
        {
            if (e.InhabitantId == napper.Id && e.Detail == InhabitantsSystem.WokenRested)
            {
                wokeAt ??= e.Tick;
                atWaking ??= napper.Needs[rested.Need];
            }
        };
        var started = host.World.Tick;
        host.World.Run(4 * calendar.Settings.TicksPerHour);

        Assert.NotNull(wokeAt);
        var hours = (double)(wokeAt!.Value - started) / calendar.Settings.TicksPerHour;
        Assert.InRange(hours, rested.MaxNapHours!.Value - 0.1, rested.MaxNapHours.Value + 0.1);
        Assert.True(atWaking < rested.At, $"woken by the nap's hours, not rested: {atWaking:0.000}");
    }

    [Fact]
    public void TheServiceHearsNightWhenAllSleepOrAtTheLimitNotAtNightfall()
    {
        var port = new PhasePort();
        var host = SimulationHost.CreateEmpty(ContentDirectory, 9, new MockDecisionPort(), "de", port);
        var thirsty = Person("inh-000001", new Position(0, 0));
        host.Inhabitants.Add(thirsty);
        var nightfall = FirstNightTick(host.World.Calendar);
        host.World.Run(nightfall + 2 - host.World.Tick);

        // Thirsty from nightfall on (set earlier, the long first day would end it before the night - A1798's later nightfall).
        thirsty.Needs["thirst"] = 0.01;
        Assert.NotEqual(JobPhase.Night, port.Phases[^1].Phase);

        // The thirsty one stays awake: night comes at the limit - but not before the data's clock minute (B2839).
        host.World.Run((long)((Wait + 2) * host.World.Calendar.Settings.TicksPerHour));
        var night = port.Phases.Last(p => p.Phase == JobPhase.Night);
        var since = host.World.Calendar.MinutesSinceNightfall(night.Tick)!.Value;
        var at = host.World.Calendar.At(night.Tick);
        var clock = (at.Hour * 60) + at.Minute;
        Assert.True(since >= (int)(Wait * 60), $"night after {since} min");
        Assert.True(clock >= Content.Inventions.Build.NotBeforeMinute!.Value || clock < 12 * 60, $"night at {clock}");
        Assert.True(since == (int)(Wait * 60) || clock == Content.Inventions.Build.NotBeforeMinute!.Value, $"night at {clock}, {since} min after nightfall");
    }

    [Fact]
    public void AllAsleepEarlyTheComputeNightStillWaitsForItsHour()
    {
        // A1798 (Marcel W2243): the compute night falls about midnight - not right at nightfall when all lie down early.
        var notBeforeMinute = Content.Inventions.Build.NotBeforeMinute!.Value;
        var port = new PhasePort();
        var host = SimulationHost.CreateEmpty(ContentDirectory, 9, new MockDecisionPort(), "de", port);
        var sleeper = Person("inh-000001", new Position(0, 0));
        host.Inhabitants.Add(sleeper);
        var nightfall = FirstNightTick(host.World.Calendar);
        host.World.Run(nightfall + 2 - host.World.Tick);
        var atNightfall = host.World.Calendar.At(nightfall);
        var notBefore = (notBeforeMinute - ((atNightfall.Hour * 60) + atNightfall.Minute)) / 60.0;
        Assert.True(notBefore > 0.5, $"nightfall {atNightfall.Hour}:{atNightfall.Minute:00} lies before {notBeforeMinute} minutes");

        // Fed and tired after the long first day: nothing but the clock decides the night here.
        foreach (var need in sleeper.Needs.Keys.ToList())
        {
            sleeper.Needs[need] = 1.0;
        }

        sleeper.Needs["sleep"] = 0.3;
        sleeper.Activity = new Activity("sleep", null, sleeper.Position, null);
        sleeper.Planned = null;
        host.World.Run((long)((notBefore - 0.25) * host.World.Calendar.Settings.TicksPerHour));
        Assert.True(host.Inhabitants.IsAsleep(sleeper));
        Assert.NotEqual(JobPhase.Night, port.Phases[^1].Phase);

        host.World.Run((long)(0.5 * host.World.Calendar.Settings.TicksPerHour));
        var night = port.Phases.Last(p => p.Phase == JobPhase.Night);
        var at = host.World.Calendar.At(night.Tick);
        Assert.Equal(notBeforeMinute, (at.Hour * 60) + at.Minute);
    }

    [Fact]
    public void OnceAllSleepTheNightHoldsEvenIfOneWakes()
    {
        // Review GW2 #339: the night is signalled when all sleep and holds until dawn, whoever wakes in between.
        var port = new PhasePort();
        var host = SimulationHost.CreateEmpty(ContentDirectory, 9, new MockDecisionPort(), "de", port);
        var a = Person("inh-000001", new Position(0, 0));
        var b = Person("inh-000002", new Position(20, 0));
        host.Inhabitants.Add(a);
        host.Inhabitants.Add(b);
        var bedtime = FirstNightTick(host.World.Calendar) + (long)(Bedtime * host.World.Calendar.Settings.TicksPerHour);
        host.World.Run(bedtime - host.World.Tick);
        for (var i = 0; i < 4 * host.World.Calendar.Settings.TicksPerHour && port.Phases[^1].Phase != JobPhase.Night; i++)
        {
            host.World.Step();
        }

        Assert.Equal(JobPhase.Night, port.Phases[^1].Phase);
        var signals = port.Phases.Count;
        a.Activity = null; // woken
        host.World.Run(host.World.Calendar.Settings.TicksPerHour / 2);
        Assert.Equal(signals, port.Phases.Count); // no Day and Night again in between (review GW2 #339)
    }

    private static long FirstNightTick(GlassWorld.Simulation.Time.WorldCalendar calendar)
    {
        for (long t = 1; t < calendar.TicksPerDay * 2; t++)
        {
            if (calendar.MinutesSinceNightfall(t) == 0)
            {
                return t;
            }
        }

        throw new InvalidOperationException("no nightfall in two days");
    }

    private static Inhabitant Person(string id, Position at)
    {
        var model = Content.InhabitantModel;
        return new Inhabitant(id, "female", 1, at, model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
            model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal), model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal), model.WillToLive.Start);
    }

    private sealed class PhasePort : IJobPort
    {
        public List<(string Phase, long Tick, bool Force)> Phases { get; } = [];

        public void Submit(string requestId, string jobJson)
        {
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => [];

        public void SetPhase(string phase, long tick, bool force) => Phases.Add((phase, tick, force));
    }
}
