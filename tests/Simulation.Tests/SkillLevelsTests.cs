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
        var host = Host(1);
        var teacher = host.Inhabitants.All.First().Id;
        var skill = host.Skills.PractisedBy("teach").Single().Skill;
        var start = host.SkillLevels.Level(teacher, skill);
        host.SkillLevels.Observe(new WorldEvent(InhabitantsSystem.ActionFinished, 1, teacher, "teach", null));
        var practised = host.SkillLevels.Level(teacher, skill);

        var perDay = host.World.Calendar.TicksPerDay;
        while (host.World.Tick % perDay != 0 || host.World.Tick == 0)
        {
            host.World.Step();
        }

        Assert.True(host.SkillLevels.Level(teacher, skill) < practised);
        for (var day = 0; day < 40; day++)
        {
            host.World.Run(perDay);
        }

        Assert.True(host.SkillLevels.Level(teacher, skill) >= start - 1e-12);
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
