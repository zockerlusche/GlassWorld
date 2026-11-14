using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Player;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// A1714 (D1112 #7, B2608): nobody knocks at the glass in its sleep - Marcel heard a knock at night with everyone asleep.
/// A knock of a sleeping inhabitant waits until it is awake, and comes then only if it still holds.
/// </summary>
public sealed class KnockWhileAsleepTests : IDisposable
{
    private const string Invention = "inv_test";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gw-knock-" + Guid.NewGuid().ToString("N"));

    public KnockWhileAsleepTests() => Directory.CreateDirectory(_directory);

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void AKnockInSleepWaitsIsSavedAndComesOnWaking()
    {
        var (host, a) = World();
        var stuck = true;
        host.Mailbox.StillStuck = (_, _) => stuck;
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;
        Sleep(a);

        StuckNow(host, a);
        Assert.DoesNotContain(events, e => e.Kind == MailboxSystem.MailArrived);
        Assert.Empty(host.Mailbox.Open);

        // Held, saved and hashed: a loaded world knocks at the same moment.
        var path = Path.Combine(_directory, "held.db");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loaded = StateCodec.Restore(Factory(), SaveGameStore.Load(path));
        loaded.Mailbox.StillStuck = (_, _) => stuck;
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());

        foreach (var world in new[] { host, loaded })
        {
            Wake(world.Inhabitants.All.Single());
            world.World.Step();
        }

        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        var knock = Assert.Single(host.Mailbox.Open);
        Assert.Equal((MailboxSystem.KnockKind, a.Id, Invention), (knock.Kind, knock.InhabitantId, knock.Subject));
        Assert.Single(events, e => e.Kind == MailboxSystem.MailArrived && e.Detail == MailboxSystem.KnockKind);
    }

    [Fact]
    public void AKnockWhoseNeedIsOverByWakingNeverComes()
    {
        var (host, a) = World();
        var stuck = true;
        host.Mailbox.StillStuck = (_, _) => stuck;
        Sleep(a);
        StuckNow(host, a);

        stuck = false; // the material turned up while it slept
        Wake(a);
        host.World.Run(5);
        Assert.Empty(host.Mailbox.Open);
        Assert.False(host.Mailbox.HasState); // nothing held, nothing sent: hashes as a world without knocks
    }

    [Fact]
    public void AnAwakeInhabitantKnocksAtOnceAsBefore()
    {
        var (host, a) = World();
        host.Mailbox.StillStuck = (_, _) => true;
        StuckNow(host, a);
        Assert.Equal(Invention, Assert.Single(host.Mailbox.Open).Subject);
    }

    private static void StuckNow(SimulationHost host, Inhabitant who)
    {
        host.Inhabitants.Report(new WorldEvent(InventionSystem.NeedsHelpEvent, host.World.Tick, who.Id, null, Invention) { Detail = "hard", Text = InventionSystem.InWorld });
        host.World.Step();
    }

    private static void Sleep(Inhabitant who) =>
        who.Activity = new Activity(Content.Value.InhabitantModel.Togetherness!.SleepAction, null, who.Position, 100_000_000);

    private static void Wake(Inhabitant who) => who.Activity = new Activity("idle", null, who.Position, 100_000_000);

    private static (SimulationHost Host, Inhabitant A) World()
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 43, new MockDecisionPort(), "de");
        var model = Content.Value.InhabitantModel;
        var who = new Inhabitant(
            "inh-000001",
            "female",
            1,
            new Position(0, 0),
            model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
            model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal),
            model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal),
            model.WillToLive.Start);
        host.Inhabitants.Add(who);
        Wake(who);
        return (host, who);
    }

    private static SimulationHostFactory Factory()
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new MockDecisionPort(), "de");
    }
}
