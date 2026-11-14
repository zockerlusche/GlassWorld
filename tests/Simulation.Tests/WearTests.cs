using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B4b K4 part 2 (B2752): an inhabitant wears one thing on its body - like a held thing (AK1a) it stays in the world and
/// follows it, nobody else can act on it. Which actions put on and take off is data (timing wears / unwears); warmth comes
/// only through the worn thing's material (insulating), never through its function.
/// </summary>
public sealed class WearTests : IDisposable
{
    private const string Wrap = "test_wrap";
    private const string Band = "test_band";
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gw-wear-" + Guid.NewGuid().ToString("N"));

    public WearTests() => Directory.CreateDirectory(_directory);

    private static string PutOn => Content.Value.Actions.OfferedActions.Concat(["put_on"]).First(a => Content.Value.Actions.Timing(a).Wears);

    private static string TakeOff => Content.Value.Actions.OfferedActions.Concat(["take_off"]).First(a => Content.Value.Actions.Timing(a).Unwears);

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void AWornThingFollowsTheWearerAndIsPutDownAtItsFeet()
    {
        var (host, a) = World();
        var wrap = host.Objects.Spawn(Wrap, new Position(0.5, 0));
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;

        Do(host, a, PutOn, wrap.Id);
        Assert.Equal(wrap.Id, a.Worn);
        Assert.Null(a.Held); // the hand stays free
        Assert.Equal(a.Id, host.Objects.HeldBy(wrap.Id));
        Assert.Contains(events, e => e.Kind == InhabitantsSystem.PutOn && e.TargetId == wrap.Id);

        a.Position = new Position(4, 3);
        host.World.Step();
        Assert.True(host.Objects.TryGet(wrap.Id, out var worn));
        Assert.Equal(a.Position, worn.Position);
        var near = new List<WorldObject>();
        host.Objects.Within(a.Position, 1, near);
        Assert.DoesNotContain(near, o => o.Id == wrap.Id); // not on the ground

        Do(host, a, TakeOff, wrap.Id);
        Assert.Null(a.Worn);
        Assert.Null(host.Objects.HeldBy(wrap.Id));
        Assert.Contains(events, e => e.Kind == InhabitantsSystem.TakenOff && e.TargetId == wrap.Id);
        near.Clear();
        host.Objects.Within(a.Position, 1, near);
        Assert.Contains(near, o => o.Id == wrap.Id);
    }

    [Fact]
    public void OneThingOnTheBodyOnlyAWearableOneAndNotAnotherOnes()
    {
        var (host, a) = World();
        var b = Person(host, "inh-000002", new Position(1, 0));
        var first = host.Objects.Spawn(Wrap, new Position(0.5, 0));
        var second = host.Objects.Spawn(Wrap, new Position(0, 0.5));
        var plain = host.Objects.Spawn("apple", new Position(-0.5, 0)); // portable, not wearable

        Do(host, a, PutOn, plain.Id);
        Assert.Null(a.Worn);

        Do(host, a, PutOn, first.Id);
        Do(host, a, PutOn, second.Id);
        Assert.Equal(first.Id, a.Worn);
        Assert.Null(host.Objects.HeldBy(second.Id));

        Do(host, b, PutOn, first.Id); // what another wears is no thing for it
        Assert.Null(b.Worn);
        Assert.Equal(a.Id, host.Objects.HeldBy(first.Id));
    }

    [Fact]
    public void AThingInTheOwnHandCanBePutOnAndLeavesTheHandFree()
    {
        var (host, a) = World();
        var wrap = host.Objects.Spawn(Wrap, new Position(0.5, 0));
        var take = Content.Value.Actions.OfferedActions.Concat(["pick_up"]).First(x => Content.Value.Actions.Timing(x).Holds);
        Do(host, a, take, wrap.Id);
        Assert.Equal(wrap.Id, a.Held);

        Do(host, a, PutOn, wrap.Id);
        Assert.Equal(wrap.Id, a.Worn);
        Assert.Null(a.Held);
        Assert.Equal(a.Id, host.Objects.HeldBy(wrap.Id));
    }

    [Fact]
    public void AWornThingKeepsWarmOnlyWhenItsMaterialInsulates()
    {
        var host = SimulationHost.CreateGeneratedWorld(ContentDirectory, 7, new MockDecisionPort(), "de", "start_island");
        Assert.NotNull(host.Body);
        Register(host);
        var wearer = host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).First();
        var bare = host.Body!.FeltTenthC(host.World, wearer);

        var band = host.Objects.Spawn(Band, wearer.Position);
        host.Objects.SetHeld(band.Id, wearer.Id);
        wearer.Worn = band.Id;
        Assert.Equal(bare, host.Body.FeltTenthC(host.World, wearer)); // the function alone gives no warmth

        var wrap = host.Objects.Spawn(Wrap, wearer.Position);
        host.Objects.SetHeld(wrap.Id, wearer.Id);
        wearer.Worn = wrap.Id;
        Assert.Equal(bare + Content.Value.Body!.Worn!.WarmTenthC, host.Body.FeltTenthC(host.World, wearer));
    }

    [Fact]
    public void AWornThingIsSavedAndTheWorldGoesOnAsInAnUnbrokenRun()
    {
        var (original, a) = World();
        var wrap = original.Objects.Spawn(Wrap, new Position(0.5, 0));
        Do(original, a, PutOn, wrap.Id);
        original.World.Run(20);

        var path = Path.Combine(_directory, "world.db");
        SaveGameStore.Save(path, StateCodec.Capture(original));
        var loaded = StateCodec.Restore(Factory(), SaveGameStore.Load(path));
        Register(loaded);
        Assert.Equal(original.World.StateHash(), loaded.World.StateHash());
        var b = loaded.Inhabitants.All.Single(i => i.Id == a.Id);
        Assert.Equal(wrap.Id, b.Worn);
        Assert.Equal(b.Id, loaded.Objects.HeldBy(wrap.Id));

        a.Position = b.Position = new Position(3, 2);
        original.World.Run(10);
        loaded.World.Run(10);
        Assert.Equal(original.World.StateHash(), loaded.World.StateHash());
    }

    [Fact]
    public void AColdInhabitantPutsOnAWearableThingBesideIt()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 5, new MockDecisionPort(), "de");
        Register(host);
        var wearer = host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).First();
        var wrap = host.Objects.Spawn(Wrap, wearer.Position);

        for (var hour = 0; hour < 24 && wearer.Worn is null; hour++)
        {
            wearer.Needs["warmth"] = 0.05;
            host.World.Run(host.World.Calendar.Settings.TicksPerHour);
        }

        Assert.Equal(wrap.Id, wearer.Worn);
    }

    private static void Register(SimulationHost host)
    {
        host.Types.Register(Wrap, ["wearable", "insulating", "crafted", "portable"]);
        host.Types.Register(Band, ["wearable", "crafted", "portable"]);
    }

    private static void Do(SimulationHost host, Inhabitant who, string action, string targetId)
    {
        Assert.True(host.Objects.TryGet(targetId, out var target));
        who.Activity = new Activity(action, targetId, target.Position, null);
        for (var i = 0; i < 2000 && who.Activity is { } doing && doing.Action == action; i++)
        {
            host.World.Step();
        }

        Busy(who);
    }

    private static void Busy(Inhabitant who) => who.Activity = new Activity("idle", null, who.Position, 100_000_000);

    private static (SimulationHost Host, Inhabitant A) World()
    {
        var host = SimulationHost.CreateEmpty(ContentDirectory, 41, new MockDecisionPort(), "de");
        Register(host);
        return (host, Person(host, "inh-000001", new Position(0, 0)));
    }

    private static Inhabitant Person(SimulationHost host, string id, Position at)
    {
        var model = Content.Value.InhabitantModel;
        var who = new Inhabitant(
            id,
            "female",
            1,
            at,
            model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal),
            model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal),
            model.Start.Traits.ToDictionary(t => t.Id, t => (t.Min + t.Max) / 2, StringComparer.Ordinal),
            model.WillToLive.Start);
        host.Inhabitants.Add(who);
        Busy(who);
        return who;
    }

    private static SimulationHostFactory Factory()
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new MockDecisionPort(), "de");
    }
}
