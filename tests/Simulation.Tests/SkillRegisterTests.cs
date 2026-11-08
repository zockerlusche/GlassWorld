using GlassWorld.Contracts;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Simulation.Tests;

/// <summary>The skill register (A1372, B4b R11, B5 R18).</summary>
public sealed class SkillRegisterTests
{
    private static readonly ContentSet Content = ContentSet.Load(Path.Combine(TestData.Root, "content"));

    /// <summary>Skills nobody begins with (B8 R14).</summary>
    private static readonly string[] NotAtStart = ["reading", "writing"];

    [Fact]
    public void TheRegisterHoldsEverySkillOfTheVocabularyAndNobodyStartsReadingOrWriting()
    {
        var register = New();
        var vocabulary = Content.Vocabularies[VocabularyKind.Skill].Entries.Select(e => e.Id).Order(StringComparer.Ordinal);
        Assert.Equal(vocabulary, register.All.Select(s => s.Id).Order(StringComparer.Ordinal));
        foreach (var id in NotAtStart)
        {
            Assert.Equal((0.0, 0.0), (register.Find(id)!.StartLevelMin, register.Find(id)!.StartLevelMax));
        }
        Assert.False(register.HasState);
        Assert.Contains(register.PractisedBy("build_step"), p => p.Skill == "building");
    }

    [Fact]
    public void ANewSkillGrowsWithItsActions()
    {
        var register = New();
        Assert.Null(register.Add(Ability("braiding", 0.1, "combine", "use"), "rt-000001"));
        Assert.True(register.HasState);
        var added = register.Find("braiding")!;
        Assert.Equal(("Flechten", "braiding", "rt-000001"), (added.NameDe, added.NameEn, added.SourceTypeId));
        var gain = Content.InhabitantModel.SkillRules.RuntimeGain;
        Assert.Contains(new SkillPracticeRule("use", "braiding", gain), register.Practice);
        Assert.Contains(new SkillPracticeRule("combine", "braiding", gain), register.PractisedBy("combine"));
    }

    [Fact]
    public void DuplicatesOddStartLevelsAndTooManyNewSkillsAreRefused()
    {
        var rules = Content.InhabitantModel.SkillRules;
        var register = New();
        Assert.Equal(SkillRegister.ReasonTaken, register.Add(Ability("building", 0.1, "use"), "rt-000001"));
        Assert.Equal(SkillRegister.ReasonSameActions, register.Add(Ability("assembling", 0.1, "build_step"), "rt-000001"));
        Assert.Equal(SkillRegister.ReasonStartLevel, register.Add(Ability("juggling", rules.RuntimeStartLevelMax + 0.1, "use"), "rt-000001"));
        Assert.Equal(SkillRegister.ReasonNoActions, register.Add(Ability("dreaming", 0.1), "rt-000001"));
        Assert.Null(register.Add(Ability("braiding", 0.1, "combine", "use"), "rt-000001"));
        Assert.Equal(SkillRegister.ReasonTaken, register.Add(Ability("braiding", 0.1, "drop"), "rt-000002"));
        Assert.Equal(SkillRegister.ReasonSameActions, register.Add(Ability("weaving", 0.1, "use", "combine"), "rt-000002"));

        var actions = Content.Vocabularies[VocabularyKind.Action].Entries.Select(e => e.Id).ToList();
        for (var i = register.Added.Count; i < rules.MaxRuntimeSkills; i++)
        {
            Assert.Null(register.Add(Ability("skill_" + (char)('a' + i), 0.1, actions[i], "wander"), "rt-00001" + i));
        }

        Assert.Equal(SkillRegister.ReasonLimit, register.Add(Ability("one_more", 0.1, "sleep", "eat"), "rt-000099"));
        Assert.Equal(rules.MaxRuntimeSkills, register.Added.Count);
    }

    [Fact]
    public void NewWorldsHaveNoNewSkillsAndKeepTheirHash()
    {
        var host = SimulationHost.CreatePlaceholderWorld(Path.Combine(TestData.Root, "content"), 1, new MockDecisionPort(), "de");
        Assert.Empty(host.Skills.Added);
        var before = host.World.StateHash();
        Assert.Null(host.Skills.Add(Ability("braiding", 0.1, "combine", "use"), "rt-000001"));
        Assert.NotEqual(before, host.World.StateHash());
    }

    [Fact]
    public void ASaveGameWithSkillsTheDataCannotCarryFailsCleanly()
    {
        var register = New();
        Assert.Null(register.Add(Ability("braiding", 0.1, "combine", "use"), "rt-000001"));
        var good = register.Capture();
        var copy = New();
        copy.Restore(good);
        Assert.Equal(good.Skills.Single().Id, copy.Added.Single().Id);
        Assert.Throws<InvalidDataException>(() => copy.Restore(new SkillRegisterState([good.Skills[0] with { Id = "building" }])));
        Assert.Throws<InvalidDataException>(() => copy.Restore(new SkillRegisterState([good.Skills[0], good.Skills[0]])));
        Assert.Throws<InvalidDataException>(() => copy.Restore(new SkillRegisterState([good.Skills[0] with { SourceTypeId = null }])));
        Assert.Empty(copy.Added);
    }

    private static SkillRegister New() => new(Content.InhabitantModel.Skills, Content.InhabitantModel.SkillRules, Content.SkillPractice);

    private static NewAbility Ability(string id, double start, params string[] actions) =>
        new() { Id = id, NameDe = id == "braiding" ? "Flechten" : id, NameEn = id, StartLevel = start, Actions = actions };
}
