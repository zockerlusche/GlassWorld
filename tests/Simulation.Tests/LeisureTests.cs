using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// The leisure trigger (B2663, A1601, D871): once everything in reach is known nobody asked the decider any more and
/// the days went silent. Now an awake inhabitant with nothing pressing who is bored asks what to do - at most every
/// few game hours (data); the options are the core's, the choice the AI's.
/// </summary>
public sealed class LeisureTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private static readonly Lazy<DeciderSettings> Settings = new(() => ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json"))));

    [Fact]
    public void ABoredInhabitantWithNothingPressingAsksAtMostEveryFewHours()
    {
        var leisure = Settings.Value.Leisure!;
        var (host, port, a) = Alone();
        host.World.Step();
        Assert.Equal(1, port.Sent);

        // Answered and done, still bored: not again before the data's hours have passed.
        var perHour = host.World.Calendar.Settings.TicksPerHour;
        for (var t = 0; t < (long)(leisure.EveryHours * perHour) - 2; t++)
        {
            Bored(a);
            host.World.Step();
        }

        Assert.Equal(1, port.Sent);
        for (var t = 0; t < 4 * perHour && port.Sent < 2; t++)
        {
            Bored(a);
            host.World.Step();
        }

        Assert.Equal(2, port.Sent);
    }

    [Fact]
    public void APressingNeedOrSleepKeepsItFromAsking()
    {
        var leisure = Settings.Value.Leisure!;

        var (hungry, hungryPort, h) = Alone();
        h.Needs["hunger"] = leisure.NeedsAtLeast / 2;
        hungry.World.Step();
        Assert.Equal(0, hungryPort.Sent);

        var (sleeping, sleepingPort, s) = Alone();
        s.Activity = new Activity(Content.Value.InhabitantModel.Togetherness!.SleepAction, null, s.Position, 10_000);
        sleeping.World.Step();
        Assert.Equal(0, sleepingPort.Sent);
    }

    [Fact]
    public void AFreeMomentIsNoQuestionTheGameListsAndABusyInhabitantHasNone()
    {
        // Review GW2 #309: the inhabitant wonders about nothing - the leisure request is not an open question of the
        // game's questions window; and while something is planned it has no free moment.
        var (host, port, a) = Alone();
        host.World.Step();
        Assert.Equal(1, port.Sent);
        Assert.Empty(host.Decisions.OpenQuestionIds(a.Id));

        var (busy, busyPort, b) = Alone();
        b.Planned = new Activity(Content.Value.InhabitantModel.Togetherness!.SleepAction, null, b.Position, null);
        busy.World.Step();
        Assert.Equal(0, busyPort.Sent);
    }

    [Fact]
    public void LowFunDoesNotKeepItFromLeisureItIsWhatMakesItBored()
    {
        // Boredom rises while fun is low (mood driver); fun, company and purpose are no pressing needs.
        var (host, port, a) = Alone();
        a.Needs["fun"] = 0.1;
        a.Needs["company"] = 0.1;
        host.World.Step();
        Assert.Equal(1, port.Sent);
    }

    [Fact]
    public void TheDataSwitchItOnWithTheTlsValues()
    {
        // B2663 with the TL's choice A (#22240): awake, every pressing need at least 0.3, at most every 6 game hours -
        // no mood condition (boredom >= 0.3 never came: the routine fills fun first, L1137).
        Assert.Contains(Settings.Value.Triggers, t => t.Kind == TriggerKind.Leisure && t.Enabled);
        var leisure = Settings.Value.Leisure!;
        Assert.Equal((0.3, (string?)null, 6.0), (leisure.NeedsAtLeast, leisure.Mood, leisure.EveryHours));
    }

    /// <summary>One inhabitant alone on empty ground: no thing and nobody to ask about, every need met, bored.</summary>
    private static (SimulationHost Host, CountingPort Port, Inhabitant A) Alone()
    {
        var port = new CountingPort();
        var host = SimulationHost.Assemble(Content.Value, Settings.Value, new World(17, Content.Value.Calendar), port, "de");
        var model = Content.Value.InhabitantModel;
        var a = new Inhabitant(
            "inh-000001",
            "female",
            1,
            new Position(0, 0),
            model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
            model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal),
            model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal),
            model.WillToLive.Start);
        Bored(a);
        host.Inhabitants.Add(a);
        return (host, port, a);
    }

    private static void Bored(Inhabitant a)
    {
        foreach (var need in a.Needs.Keys.ToList())
        {
            a.Needs[need] = 1.0;
        }

        if (Settings.Value.Leisure!.Mood is { } mood)
        {
            a.Moods[mood] = 0.6;
        }
    }

    /// <summary>Counts the requests and answers each like the mock decider.</summary>
    private sealed class CountingPort : IDecisionPort
    {
        private readonly MockDecisionPort _mock = new();

        public int Sent { get; private set; }

        public void Send(string requestId, string requestJson)
        {
            Sent++;
            _mock.Send(requestId, requestJson);
        }

        public IReadOnlyList<PortMessage> Receive(long tick) => _mock.Receive(tick);
    }
}
