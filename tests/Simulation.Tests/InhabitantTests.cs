using System.Text.Json;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Tests;

public sealed class InhabitantTests
{
    private static readonly ContentSet Content = ContentSet.Load(Path.Combine(TestData.Root, "content"));
    private static readonly Position[] StartPositions = [new(0, 0), new(5, 0), new(2, 5), new(7, 5)];

    [Fact]
    public void StartInhabitantsComeFromTheSeed()
    {
        var a = NewWorld(42).Inhabitants;
        var b = NewWorld(42).Inhabitants;
        var c = NewWorld(43).Inhabitants;

        Assert.Equal(4, a.All.Count());
        Assert.Equal(["female", "male", "female", "male"], a.All.Select(i => i.Sex));
        Assert.Equal([1, 1, 2, 2], a.All.Select(i => i.Group));
        Assert.Equal(a.All.Select(i => i.Traits["curiosity"]), b.All.Select(i => i.Traits["curiosity"]));
        Assert.NotEqual(a.All.Select(i => i.Traits["curiosity"]), c.All.Select(i => i.Traits["curiosity"]));
        Assert.All(a.All.SelectMany(i => i.Traits.Values), v => Assert.InRange(v, 0.2, 0.9));
    }

    [Fact]
    public void PoisonActsThroughTheTruePropertyAndIsLearned()
    {
        var (world, inhabitants, objects) = NewWorld(1);
        var eater = inhabitants.Get("inh-000001");
        var berry = objects.Spawn("red_berry", new Position(0.5, 0));
        var fearBefore = eater.Moods["fear"];
        var willBefore = eater.WillToLive;

        eater.Activity = new Activity("eat", berry.Id, berry.Position, null);
        RunUntilIdle(world, eater);

        Assert.True(eater.Moods["fear"] > fearBefore);
        Assert.True(eater.WillToLive < willBefore);
        Assert.False(objects.TryGet(berry.Id, out _));
        Assert.True(eater.Beliefs.TryGet("red_berry", "poisonous", out var poisonous) && poisonous.Value);
        Assert.True(eater.Beliefs.TryGet("red_berry", "edible", out var edible) && !edible.Value);

        // Having learned it, the routine never offers to eat red berries again.
        objects.Spawn("red_berry", new Position(1, 0));
        eater.Needs["hunger"] = 0.0;
        var options = Content.Brain.Score(eater, [.. objects.All]);
        Assert.DoesNotContain(options, o => o.Action == "eat");
    }

    [Fact]
    public void EdibleFoodSatisfiesHungerAndBecomesKnown()
    {
        var (world, inhabitants, objects) = NewWorld(1);
        var eater = inhabitants.Get("inh-000001");
        eater.Needs["hunger"] = 0.2;
        var apple = objects.Spawn("apple", new Position(10, 0));

        eater.Activity = new Activity("eat", apple.Id, apple.Position, null);
        RunUntilIdle(world, eater);

        Assert.True(eater.Needs["hunger"] > 0.5);
        Assert.True(eater.Beliefs.TryGet("apple", "edible", out var belief) && belief.Value);
        Assert.Contains(inhabitants.Events, e => e.Kind == InhabitantsSystem.ActionFinished || e.Kind == InhabitantsSystem.ActionStarted);
    }

    [Fact]
    public void WillToLiveSinksOnlyAfterLongNeglect()
    {
        var (world, inhabitants, _) = NewWorld(1);
        var hungry = inhabitants.Get("inh-000001");
        hungry.Needs["hunger"] = 0.0;
        var start = hungry.WillToLive;
        var neglectHours = Content.InhabitantModel.WillToLive.NeglectHoursBeforeDecline;
        var ticksPerHour = world.Calendar.Settings.TicksPerHour;

        world.Run((long)(neglectHours - 1) * ticksPerHour);
        Assert.Equal(start, hungry.WillToLive);

        world.Run(2L * ticksPerHour);
        Assert.True(hungry.WillToLive < start);
        Assert.False(inhabitants.IsFading(hungry));
    }

    [Fact]
    public void ChangingDataChangesBehaviourWithoutCodeChange()
    {
        var model = Clone(Content.InhabitantModel);
        model.Needs.Single(n => n.Id == "hunger").DecayPerHour = 0.5;
        var fast = NewWorld(1, model);
        var normal = NewWorld(1);
        fast.World.Run(60);
        normal.World.Run(60);
        Assert.True(fast.Inhabitants.Get("inh-000001").Needs["hunger"] < normal.Inhabitants.Get("inh-000001").Needs["hunger"] - 0.3);

        var weights = Clone(Content.Brain.Weights);
        foreach (var c in weights.Considerations.Where(c => c.Action != "idle"))
        {
            c.Weight = 0.0;
        }

        var lazyBrain = new UtilityBrain(weights, Content.Actions, Ids(VocabularyKind.Need), Content.Objects.PropertyVocabulary);
        var tired = normal.Inhabitants.Get("inh-000001");
        tired.Needs["sleep"] = 0.0;
        Assert.Equal("sleep", Content.Brain.Score(tired, [])[0].Action);
        Assert.Equal("idle", lazyBrain.Score(tired, [])[0].Action);
    }

    [Fact]
    public void ANeedWithNothingHelpfulInSightMakesTheInhabitantLookFurtherAway()
    {
        // Headless seed 42 (PR #118): a thirsty inhabitant whose only water in sight was salty sea water
        // stood idle for days next to it, because a need's urgency only counted for targets in sight.
        var (world, inhabitants, objects) = NewWorld(1);
        var thirsty = inhabitants.Get("inh-000001");
        thirsty.Needs["thirst"] = 0.3;
        thirsty.Needs["fun"] = 1.0;
        thirsty.Beliefs.Set("spring_water", "drinkable", true, 1.0);
        thirsty.Beliefs.Set("sea_water", "drinkable", true, 1.0);
        thirsty.Beliefs.Set("sea_water", "salty", true, 1.0);
        objects.Spawn("spring_water", new Position(200, 0));
        var sea = objects.Spawn("sea_water", new Position(-20, 0));

        var best = Content.Brain.Score(thirsty, [sea])[0];
        var search = Content.Brain.Weights.Considerations.Single(c => c.Need == "thirst" && c.TargetProperty is not null).SearchAction;
        Assert.NotNull(search);
        Assert.Equal((search, (string?)null), (best.Action, best.TargetId));

        // Only what the inhabitant believes counts: knowing nothing that quenches thirst, there is nothing to look for.
        var unknowing = inhabitants.Get("inh-000002");
        unknowing.Needs["thirst"] = 0.3;
        unknowing.Needs["fun"] = 1.0;
        Assert.DoesNotContain(Content.Brain.Score(unknowing, []), o => o.Action == search && o.Score > 0);

        var start = thirsty.Position;
        world.Run(2 * world.Calendar.Settings.TicksPerHour);
        Assert.NotEqual(start, thirsty.Position);
    }

    [Fact]
    public void SameSeedSameHistory()
    {
        var a = NewWorld(9);
        var b = NewWorld(9);
        Feed(a.Objects);
        Feed(b.Objects);
        a.World.Run(2 * a.World.Calendar.TicksPerDay);
        b.World.Run(2 * b.World.Calendar.TicksPerDay);
        Assert.Equal(a.World.StateHash(), b.World.StateHash());
    }

    [Fact]
    public void WithFoodAndWaterInhabitantsKeepTheirNeedsUpFor10Days()
    {
        var (world, inhabitants, objects) = NewWorld(5);
        Feed(objects);
        var lowest = 1.0;
        for (var day = 0; day < 10; day++)
        {
            world.Run(world.Calendar.TicksPerDay);
            lowest = Math.Min(lowest, inhabitants.All.SelectMany(i => new[] { i.Needs["hunger"], i.Needs["thirst"] }).Min());
        }

        Assert.True(lowest > 0.0, $"a need fell to 0 (lowest {lowest})");
        Assert.All(inhabitants.All, i => Assert.False(inhabitants.IsFading(i)));
    }

    private static (World World, InhabitantsSystem Inhabitants, WorldObjects Objects) NewWorld(ulong seed, InhabitantModel? model = null)
    {
        var world = new World(seed, Content.Calendar);
        var objects = new WorldObjects(Content.Objects);
        var inhabitants = new InhabitantsSystem(model ?? Content.InhabitantModel, Content.Actions, Content.Brain, Content.Gaits, objects, world.Calendar.Settings.TicksPerHour);
        world.Add(objects).Add(inhabitants);
        inhabitants.SpawnStartInhabitants(world, StartPositions);
        return (world, inhabitants, objects);
    }

    private static void Feed(WorldObjects objects)
    {
        for (var i = 0; i < 200; i++)
        {
            objects.Spawn("apple", new Position(-10 + (i % 10), 10 + (i / 10)));
        }

        objects.Spawn("spring_water", new Position(8, 8));
        objects.Spawn("red_berry", new Position(3, -3));
        objects.Spawn("sea_water", new Position(-20, -20));
    }

    private static void RunUntilIdle(World world, Inhabitant inhabitant)
    {
        for (var i = 0; i < 10_000 && inhabitant.Activity is not null; i++)
        {
            world.Step();
        }

        Assert.Null(inhabitant.Activity);
    }

    private static HashSet<string> Ids(VocabularyKind kind) =>
        Content.Vocabularies[kind].Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
