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
    public void TheServiceHearsNightWhenAllSleepOrAtTheLimitNotAtNightfall()
    {
        var port = new PhasePort();
        var host = SimulationHost.CreateEmpty(ContentDirectory, 9, new MockDecisionPort(), "de", port);
        var thirsty = Person("inh-000001", new Position(0, 0));
        thirsty.Needs["thirst"] = 0.01;
        host.Inhabitants.Add(thirsty);
        var nightfall = FirstNightTick(host.World.Calendar);
        host.World.Run(nightfall + 2 - host.World.Tick);
        Assert.NotEqual(JobPhase.Night, port.Phases[^1].Phase);

        // The thirsty one stays awake: night comes at the limit.
        host.World.Run((long)(Wait * host.World.Calendar.Settings.TicksPerHour));
        var night = port.Phases.Last(p => p.Phase == JobPhase.Night);
        Assert.Equal((int)(Wait * 60), host.World.Calendar.MinutesSinceNightfall(night.Tick)!.Value);
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
