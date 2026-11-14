using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1694 Tatendrang (D1086, B2728): a ninth trait that changes only how often and how readily an inhabitant starts
/// something on its own - free moments, ripening ideas - never what it does; success strengthens it. Every number
/// comes from the data.
/// </summary>
public sealed class DriveTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private static readonly Lazy<DeciderSettings> Settings = new(() => ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json"))));

    private static string Drive => Settings.Value.Leisure!.EveryHoursByTrait!.Trait;

    [Fact]
    public void TheTraitIsDrawnBehindTheOthersSoTheirValuesStay()
    {
        var traits = Content.Value.InhabitantModel.Start.Traits;
        Assert.Equal(Drive, traits.Last().Id);
        Assert.Contains(Content.Value.Vocabularies[VocabularyKind.Trait].Entries, e => e.Id == Drive);

        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 5, new MockDecisionPort(), "de", "start_island");
        var check = new World(5, Content.Value.Calendar);
        foreach (var inhabitant in host.Inhabitants.All)
        {
            // The same stream, the same order: the earlier traits are what they were before drive came.
            var rng = check.Random.Get($"inhabitant/{inhabitant.Id}/start");
            foreach (var range in traits)
            {
                Assert.Equal(range.Min + ((range.Max - range.Min) * rng.NextDouble()), inhabitant.Traits[range.Id], 12);
            }
        }
    }

    [Fact]
    public void ACurveIsAFactorOverTheTraitAndOneWithoutIt()
    {
        var curve = Settings.Value.Leisure!.EveryHoursByTrait!;
        var points = curve.Curve.Select(p => p.ToArray()).ToList();
        foreach (var p in points)
        {
            Assert.Equal(p[1], TraitFactor.Of(curve, new Dictionary<string, double> { [Drive] = p[0] }), 12);
        }

        Assert.Equal(1.0, TraitFactor.Of(curve, new Dictionary<string, double>()));
        Assert.Equal(1.0, TraitFactor.Of(null, new Dictionary<string, double> { [Drive] = 0.9 }));
    }

    [Fact]
    public void MuchDriveMakesFreeMomentsComeSoonerLittleDriveLater()
    {
        // The second free moment comes sooner with much drive, later with little - by the data's factor.
        var limit = 4 * (long)(Settings.Value.Leisure!.EveryHours * Content.Value.Calendar.Settings.TicksPerHour);
        var keen = SecondAskAt(0.9, limit);
        var middle = SecondAskAt(0.55, limit);
        var slow = SecondAskAt(0.2, limit);
        Assert.True(keen < middle && middle < slow && slow < limit, $"{keen} {middle} {slow}");
    }

    [Fact]
    public void MuchDriveFindsAFreeMomentWithLessStrength()
    {
        var leisure = Settings.Value.Leisure!;
        var keen = leisure.NeedsAtLeast * TraitFactor.Of(leisure.NeedsAtLeastByTrait, new Dictionary<string, double> { [Drive] = 0.9 });
        Assert.True(keen < leisure.NeedsAtLeast);

        // A pressing need just below the plain limit but above the keen one's.
        var need = Content.Value.InhabitantModel.Needs.First(n => n.NeglectBelow > 0).Id;
        Assert.Equal(1, Asked(0.9, a => a.Needs[need] = (keen + leisure.NeedsAtLeast) / 2));
        Assert.Equal(0, Asked(0.55, a => a.Needs[need] = (keen + leisure.NeedsAtLeast) / 2));
    }

    [Fact]
    public void SuccessStrengthensDriveUpToItsMax()
    {
        var gain = Content.Value.Actions.EventEffects(InventionSystem.Built, null).Where(e => e.Kind == EffectKind.Trait && e.Id == Drive).Sum(e => e.Delta);
        Assert.True(gain > 0);
        var (host, _, a) = Alone(0.5);
        host.Inhabitants.Report(new WorldEvent(InventionSystem.Built, 1, a.Id, null, null));
        Assert.Equal(0.5 + gain, a.Traits[Drive], 12);

        var max = Content.Value.InhabitantModel.Start.Traits.Single(t => t.Id == Drive).Max;
        a.Traits[Drive] = max - (gain / 2);
        host.Inhabitants.Report(new WorldEvent(InventionSystem.Built, 2, a.Id, null, null));
        Assert.Equal(max, a.Traits[Drive], 12);
    }

    private static long SecondAskAt(double drive, long limit)
    {
        var (host, port, a) = Alone(drive);
        while (port.Sent < 2 && host.World.Tick < limit)
        {
            // Only the cool-down holds it back: the answer's action is not carried out.
            Bored(a);
            a.Activity = null;
            a.Planned = null;
            host.World.Step();
        }

        return host.World.Tick;
    }

    private static int Asked(double drive, Action<Inhabitant> set)
    {
        var (host, port, a) = Alone(drive);
        set(a);
        host.World.Step();
        return port.Sent;
    }

    private static (SimulationHost Host, CountingPort Port, Inhabitant A) Alone(double drive)
    {
        var port = new CountingPort();
        var host = SimulationHost.Assemble(Content.Value, Settings.Value, new World(17, Content.Value.Calendar), port, "de");
        var model = Content.Value.InhabitantModel;
        var traits = model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal);
        traits[Drive] = drive;
        var a = new Inhabitant(
            "inh-000001",
            "female",
            1,
            new Position(0, 0),
            model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
            model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal),
            traits,
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
