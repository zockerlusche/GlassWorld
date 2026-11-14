using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.State;
using GlassWorld.Simulation.Writing;

namespace GlassWorld.Simulation.Tests;

/// <summary>Skill levels per inhabitant: start level, talent, practice and forgetting (B5 part, TL #19589).</summary>
public sealed class SkillLevelsTests : IDisposable
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gw-skill-levels-" + Guid.NewGuid().ToString("N"));

    public SkillLevelsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void StartLevelsAndTalentsComeFromTheDataAndTheSeed()
    {
        var host = Host(1);
        var settings = host.Content.SkillLevels;
        foreach (var skill in host.Skills.All)
        {
            foreach (var inhabitant in host.Inhabitants.All)
            {
                Assert.InRange(host.SkillLevels.StartLevel(inhabitant.Id, skill.Id), skill.StartLevelMin, skill.StartLevelMax);
                Assert.InRange(host.SkillLevels.Talent(inhabitant.Id, skill.Id), settings.TalentMin, settings.TalentMax);
            }
        }

        // R14: nobody starts with reading or writing.
        Assert.All(host.Inhabitants.All, i => Assert.Equal(0, host.SkillLevels.Level(i.Id, "reading")));
        Assert.All(host.Inhabitants.All, i => Assert.Equal(0, host.SkillLevels.Level(i.Id, "writing")));
        Assert.Equal(0, host.SkillLevels.Level("inh-000001", "no_such_skill"));

        // The same seed gives the same values; another seed others.
        var again = Host(1);
        var other = Host(2);
        var id = host.Inhabitants.All.First().Id;
        Assert.Equal(host.SkillLevels.Talent(id, "building"), again.SkillLevels.Talent(id, "building"));
        Assert.NotEqual(host.SkillLevels.Talent(id, "building"), other.SkillLevels.Talent(id, "building"));
    }

    [Fact]
    public void ANewWorldHasNoSkillStateAndKeepsItsHash()
    {
        var host = Host(1);
        Assert.False(host.SkillLevels.HasState);
        var before = host.World.StateHash();
        _ = host.SkillLevels.Level(host.Inhabitants.All.First().Id, "building"); // drawing creates no state
        Assert.Equal(before, host.World.StateHash());
    }

    [Fact]
    public void AFinishedActionPractisesItsSkillsByTalentUpToTheMaximum()
    {
        var host = Host(1);
        var teacher = host.Inhabitants.All.First().Id;
        var rule = host.Skills.PractisedBy("teach").Single();
        var start = host.SkillLevels.Level(teacher, rule.Skill);
        var before = host.World.StateHash();

        host.SkillLevels.Observe(new WorldEvent(InhabitantsSystem.ActionFinished, 1, teacher, "teach", null));
        Assert.Equal(start + (rule.Gain * host.SkillLevels.Talent(teacher, rule.Skill)), host.SkillLevels.Level(teacher, rule.Skill), 9);
        Assert.True(host.SkillLevels.HasState);
        Assert.NotEqual(before, host.World.StateHash());

        // Other events and actions without a practice rule change nothing.
        host.SkillLevels.Observe(new WorldEvent(InhabitantsSystem.ActionStarted, 2, teacher, "teach", null));
        host.SkillLevels.Observe(new WorldEvent(InhabitantsSystem.ActionFinished, 2, teacher, "sleep", null));
        Assert.Single(host.SkillLevels.Capture());

        for (var i = 0; i < 10_000; i++)
        {
            host.SkillLevels.Observe(new WorldEvent(InhabitantsSystem.ActionFinished, 3, teacher, "teach", null));
        }

        Assert.Equal(host.Content.SkillLevels.MaxLevel, host.SkillLevels.Level(teacher, rule.Skill), 9);
    }

    [Fact]
    public void WhatIsNotPractisedFadesButNeverBelowTheStart()
    {
        // An id that does not act in the world, so nobody practises it meanwhile (levels come from the id).
        var host = Host(1);
        var teacher = "inh-999999";
        var skill = host.Skills.PractisedBy("teach").Single().Skill;
        var start = host.SkillLevels.Level(teacher, skill);
        host.SkillLevels.Observe(new WorldEvent(InhabitantsSystem.ActionFinished, 1, teacher, "teach", null));
        var practised = host.SkillLevels.Level(teacher, skill);

        var perDay = host.World.Calendar.TicksPerDay;
        while (host.World.Tick % perDay != 0 || host.World.Tick == 0)
        {
            host.World.Step();
        }

        // Practised within the last day: the first day boundary keeps the level (review GW1 #123) ...
        Assert.Equal(practised, host.SkillLevels.Level(teacher, skill), 12);
        host.World.Run(perDay + 1); // systems see the tick before it advances: one more step reaches the next day boundary

        // ... a whole day without practice lets it fade a little.
        Assert.True(host.SkillLevels.Level(teacher, skill) < practised);
        for (var day = 0; day < 40; day++)
        {
            host.World.Run(perDay);
        }

        Assert.True(host.SkillLevels.Level(teacher, skill) >= start - 1e-12);
    }

    [Fact]
    public void LongPracticeLeavesAFloorShortPracticeHardlyAny()
    {
        // D1017 G2: forgetting stops at peak x share(days practised) - the data's table.
        var (world, levels, skill, perDay, settings) = Bare();
        var retention = settings.Retention!;
        var share = new Behaviour.Curve(retention.FloorShareByDays);
        const string Long = "inh-999998", Short = "inh-999999";
        var longStart = levels.Level(Long, skill);
        var shortStart = levels.Level(Short, skill);
        var days = (int)retention.FloorShareByDays.Last().First();
        for (var day = 0; day < days; day++)
        {
            for (var i = 0; i < 2; i++)
            {
                levels.Observe(new WorldEvent(InhabitantsSystem.ActionFinished, world.Tick, Long, "teach", null));
            }

            if (day == 0)
            {
                levels.Observe(new WorldEvent(InhabitantsSystem.ActionFinished, world.Tick, Short, "teach", null));
            }

            world.Run(perDay);
        }

        var peak = levels.Level(Long, skill) - longStart;
        var shortPeak = shortStart + (levels.Capture().Single(g => g.InhabitantId == Short).Peak);

        // Long without practice: down to its floor, not further.
        var enough = (int)Math.Ceiling(peak / settings.ForgetPerDay) + 2;
        world.Run(perDay * enough);
        Assert.Equal(longStart + (peak * share.At(days)), levels.Level(Long, skill), 9);
        Assert.True(levels.Level(Short, skill) < shortPeak);
        Assert.Equal(shortStart + ((shortPeak - shortStart) * share.At(1)), levels.Level(Short, skill), 9);
    }

    [Fact]
    public void RelearningWhatOneCouldOnceDoGoesFaster()
    {
        // Far below its peak: one practice counts relearn_factor times.
        var (world, levels, skill, perDay, settings) = Bare();
        const string Who = "inh-999999";
        var start = levels.StartLevel(Who, skill);
        levels.Observe(new WorldEvent(InhabitantsSystem.ActionFinished, world.Tick, Who, "teach", null));
        var step = levels.Level(Who, skill) - start;
        for (var i = 1; i < 10; i++)
        {
            levels.Observe(new WorldEvent(InhabitantsSystem.ActionFinished, world.Tick, Who, "teach", null));
        }

        var factor = settings.Retention!.RelearnFactor;
        world.Run(perDay * ((int)Math.Ceiling(step * factor / settings.ForgetPerDay) + 3));
        var faded = levels.Level(Who, skill);
        levels.Observe(new WorldEvent(InhabitantsSystem.ActionFinished, world.Tick, Who, "teach", null));
        Assert.Equal(step * factor, levels.Level(Who, skill) - faded, 9);
    }

    [Fact]
    public void JustBelowItsPeakPracticeCountsAsUsualBeyondIt()
    {
        // Review GW2 #407: a day off costs a little; the bonus gives back only that, then practice counts as usual.
        var (world, levels, skill, perDay, settings) = Bare();
        const string Who = "inh-999999";
        var start = levels.StartLevel(Who, skill);
        levels.Observe(new WorldEvent(InhabitantsSystem.ActionFinished, world.Tick, Who, "teach", null));
        var step = levels.Level(Who, skill) - start;
        world.Run((perDay * 3) + 1);
        var peak = start + step;
        var gap = peak - levels.Level(Who, skill);
        Assert.True(gap > 0 && gap < step);

        levels.Observe(new WorldEvent(InhabitantsSystem.ActionFinished, world.Tick, Who, "teach", null));
        Assert.Equal(peak + step - (gap / settings.Retention!.RelearnFactor), levels.Level(Who, skill), 9);
    }

    /// <summary>The skill levels alone in an empty world: nobody else practises, days pass fast.</summary>
    private static (World World, SkillLevelsSystem Levels, string Skill, long PerDay, Contracts.SkillLevelSettings Settings) Bare()
    {
        var host = Host(1);
        var world = new World(1, host.Content.Calendar);
        var perDay = world.Calendar.TicksPerDay;
        var levels = new SkillLevelsSystem(host.Content.SkillLevels, host.Skills, 1, perDay);
        world.Add(levels);
        return (world, levels, host.Skills.PractisedBy("teach").Single().Skill, perDay, host.Content.SkillLevels);
    }

    [Fact]
    public void ASkillPractisedEveryDayDoesNotFade()
    {
        var host = Host(1);
        var teacher = host.Inhabitants.All.First().Id;
        var skill = host.Skills.PractisedBy("teach").Single().Skill;
        var perDay = host.World.Calendar.TicksPerDay;
        double last = 0;
        for (var day = 0; day < 5; day++)
        {
            host.SkillLevels.Observe(new WorldEvent(InhabitantsSystem.ActionFinished, host.World.Tick, teacher, "teach", null));
            var now = host.SkillLevels.Level(teacher, skill);
            Assert.True(now > last, $"day {day}: {now} not above {last}");
            last = now;
            host.World.Run(perDay - 1);
        }
    }

    [Fact]
    public void PractisedLevelsSurviveSaveAndLoad()
    {
        var host = Host(1);
        var teacher = host.Inhabitants.All.First().Id;
        host.SkillLevels.Observe(new WorldEvent(InhabitantsSystem.ActionFinished, 1, teacher, "teach", null));
        host.World.Run(20);
        var path = Path.Combine(_directory, "skills.sqlite");
        SaveGameStore.Save(path, StateCodec.Capture(host));
        var settings = Contracts.Validation.ContractSerializer.Deserialize<Contracts.DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        var loaded = StateCodec.Restore(new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new ReplayDecisionPort(host.Decisions.Log), "de"), SaveGameStore.Load(path));
        Assert.Equal(host.SkillLevels.Capture(), loaded.SkillLevels.Capture());
        Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
    }

    [Fact]
    public void TheWritingRulesReadTheLevels()
    {
        // The seam of A1357: nobody can write at the start, because nobody has the writing skill.
        var host = Host(1);
        var writer = host.Inhabitants.All.First();
        var stone = host.Objects.Spawn("fieldstone", writer.Position);
        Assert.Equal(WritingSystem.NoWritingSkill, host.Writing.CanWriteOn(writer, stone, "rock_painting", host.SkillLevels, NothingHeld.Instance));
    }

    private static SimulationHost Host(ulong seed) => SimulationHost.CreatePlaceholderWorld(ContentDirectory, seed, new MockDecisionPort(), "de");
}
