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
/// B4a R6: the core only counts repeated experiences (action, what it is about, time of day); counters grow,
/// fade and are forgotten by the data, and from the data's threshold on they are habits in the decider
/// context, strongest first. Counters and meetings are saved and hashed; older saves load without counters.
/// </summary>
public sealed class ImprintingTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly ContentSet Content = ContentSet.Load(ContentDirectory);
    private static readonly KnowledgeImprinting Imprinting = Content.Knowledge.Imprinting!;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gw-imprint-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void RepeatedDrinkingAtTheSameTimeOfDayBecomesAHabitInTheDeciderContext()
    {
        var (world, objects, inhabitants) = NewWorld();
        var a = inhabitants.Get("inh-000001");
        var spring = objects.Spawn("spring_water", new Position(1, 0));
        var phase = world.Now.Phase.ToString().ToLower(System.Globalization.CultureInfo.InvariantCulture);
        var key = new ImprintKey("drink", InhabitantsSystem.AboutType, "spring_water", phase);
        var times = (int)Math.Ceiling(Imprinting.HabitFrom) + 1;
        for (var i = 0; i < times; i++)
        {
            a.Needs["thirst"] = 0.1;
            a.Activity = new Activity("drink", spring.Id, spring.Position, null);
            for (var t = 0; t < 1_000 && a.Activity is not null; t++)
            {
                world.Step();
            }
        }

        Assert.Equal(phase, world.Now.Phase.ToString().ToLower(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(a.Imprints.TryGetValue(key, out var count));
        Assert.InRange(count, Imprinting.HabitFrom, times);

        var state = InhabitantStates.Of(a, Content.Knowledge, [], []);
        var habit = Assert.Single(state.Habits);
        Assert.Equal("drink", habit.Action);
        Assert.Equal(phase, habit.TimeOfDay);
        Assert.Equal(SubjectKind.Type, habit.About!.Kind);
        Assert.Equal("spring_water", habit.About.Id);
        Assert.Equal(Math.Min(1.0, count / Imprinting.FullAt), habit.Strength, 12);
        Assert.Empty(ContractValidator.Validate("decision-request", ContractSerializer.Serialize(new DecisionRequest
        {
            ContractVersion = ContractInfo.Version,
            RequestId = DecisionIds.RequestId(1, world.Tick, a.Id, 1),
            Tick = world.Tick,
            Seed = 1,
            DeadlineMs = 1000,
            DisplayLocale = Locale.De,
            Inhabitant = state,
            Environment = new EnvironmentState { TimeOfDay = phase, TemperatureC = 18, Weather = ["clear"] },
            Perception = new Perception { Objects = [], Inhabitants = [] },
            Options = [new Option { OptionId = "o1", Action = "idle", Target = null, Partners = [] }],
        })));
    }

    [Fact]
    public void HabitsAreTheStrongestCountersUpToTheCapAndCountersFadeAndAreForgotten()
    {
        var (world, _, inhabitants) = NewWorld();
        var a = inhabitants.Get("inh-000001");
        for (var i = 0; i < Imprinting.MaxHabits + 2; i++)
        {
            a.Imprints[new ImprintKey("act" + i, string.Empty, string.Empty, "day")] = Imprinting.HabitFrom + i;
        }

        a.Imprints[new ImprintKey("weak", string.Empty, string.Empty, "day")] = Imprinting.HabitFrom - 0.01;
        var habits = InhabitantsSystem.Habits(Content.Knowledge, a);
        Assert.Equal(Imprinting.MaxHabits, habits.Count);
        Assert.Equal("act" + (Imprinting.MaxHabits + 1), habits[0].Key.Action);
        Assert.True(habits.Zip(habits.Skip(1)).All(p => p.First.Strength >= p.Second.Strength));
        Assert.DoesNotContain(habits, h => h.Key.Action == "weak");

        var faint = new ImprintKey("faint", string.Empty, string.Empty, "day");
        a.Imprints[faint] = Imprinting.ForgetBelow + (Imprinting.FadePerHour * 0.5);
        var before = a.Imprints[new ImprintKey("act0", string.Empty, string.Empty, "day")];
        world.Run(world.Calendar.Settings.TicksPerHour);
        Assert.Equal(before - Imprinting.FadePerHour, a.Imprints[new ImprintKey("act0", string.Empty, string.Empty, "day")], 12);
        Assert.False(a.Imprints.ContainsKey(faint));
    }

    [Fact]
    public void CountersAndMeetingsSurviveSaveAndLoadAndChangeTheHash()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 11, new MockDecisionPort(), "de");
        host.World.Run(10);
        var a = host.Inhabitants.All.First();
        var hash = host.World.StateHash();
        a.Imprints[new ImprintKey("eat", InhabitantsSystem.AboutType, "apple", "day")] = 4.5;
        Assert.NotEqual(hash, host.World.StateHash());
        Assert.NotEmpty(a.Met);

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "world.sqlite");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var loaded = StateCodec.Restore(Factory(), SaveGameStore.Load(path));

        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
        Assert.Equal(a.Met, loaded.Inhabitants.Get(a.Id).Met);
        Assert.Equal(a.Imprints, loaded.Inhabitants.Get(a.Id).Imprints);
    }

    [Fact]
    public void ASaveFromBeforeImprintingLoadsWithoutCountersAndWithTheStartGroupsMet()
    {
        var host = SimulationHost.CreatePlaceholderWorld(ContentDirectory, 11, new MockDecisionPort(), "de");
        host.World.Run(10);
        var a = host.Inhabitants.All.First();
        a.Imprints[new ImprintKey("eat", InhabitantsSystem.AboutType, "apple", "day")] = 4.5;
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "old.sqlite");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE imprints; DROP TABLE met; PRAGMA user_version = 9;";
            command.ExecuteNonQuery();
        }

        var loaded = StateCodec.Restore(Factory(), SaveGameStore.Load(path));

        Assert.All(loaded.Inhabitants.All, i => Assert.Empty(i.Imprints));
        foreach (var i in loaded.Inhabitants.All)
        {
            Assert.Equal(loaded.Inhabitants.All.Where(o => o != i && o.Group == i.Group).Select(o => o.Id), i.Met);
            Assert.Equal(host.Inhabitants.Get(i.Id).Relationships.All, i.Relationships.All);
        }
    }

    private static SimulationHostFactory Factory() => new(
        ContentSet.Load(ContentDirectory),
        ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json"))),
        new MockDecisionPort(),
        "de");

    /// <summary>The start inhabitants far apart, idling until told otherwise.</summary>
    private static (World World, WorldObjects Objects, InhabitantsSystem Inhabitants) NewWorld()
    {
        var world = new World(3, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Knowledge = Content.Knowledge,
        };
        world.Add(objects).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, [new(0, 0), new(300, 0), new(0, 300), new(300, 300)]);
        foreach (var inhabitant in inhabitants.All)
        {
            inhabitant.Activity = new Activity("idle", null, inhabitant.Position, 100_000);
        }

        return (world, objects, inhabitants);
    }
}
