using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Data;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// B2598, D827 R12/R35 (A1583, A1625): the core recognises the world's milestones from what is made and
/// derives the reached tech level from them. Everything is read from the data; no milestone is named here
/// except to pick one of the data by its kind (a rule or properties).
/// </summary>
public sealed class MilestoneTests
{
    private static readonly string ContentDirectory = Path.Combine(TestData.Root, "content");
    private static readonly Lazy<ContentSet> Content = new(() => ContentSet.Load(ContentDirectory));

    [Fact]
    public void EveryMilestoneHasANameInBothLanguagesAndTheEventIsInTheVocabulary()
    {
        var catalog = Content.Value.Milestones;
        Assert.NotNull(catalog);
        Assert.NotEmpty(catalog.Milestones);
        foreach (var lang in new[] { "de", "en" })
        {
            var text = File.ReadAllText(Path.Combine(ContentDirectory, "i18n", lang + ".json"));
            Assert.All(catalog.Milestones, m => Assert.Contains($"\"key\": \"{m.TextKey}\"", text, StringComparison.Ordinal));
        }

        Assert.Contains(MilestoneSystem.MilestoneReached, (IEnumerable<string>)Content.Value.EventKinds);
    }

    [Fact]
    public void AWorldStartsWithoutMilestonesAtTheStartLevelAndOutOfTheHash()
    {
        var host = World();
        Assert.False(host.Milestones.HasState);
        Assert.Empty(host.Milestones.Reached);
        Assert.Equal(Content.Value.Inventions.Settings.TechLevel, host.Milestones.TechLevel);
        Assert.Equal(Content.Value.Inventions.ReachedTechLevel, host.LookEpoch.ReachedTechLevel);
    }

    [Fact]
    public void TheFirstUseOfAProcessVerbIsAMilestoneOnceAndRaisesTheTechLevelEverywhere()
    {
        var host = World();
        var start = host.Milestones.TechLevelIndex;
        var levels = Content.Value.Inventions.TechLevels;
        var milestone = Content.Value.Milestones!.Milestones.First(m => m.Process is not null && m.RaisesTechLevel is { } l && IndexOf(levels, l) > start);
        var maker = host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).First();
        var events = new List<WorldEvent>();
        host.Inhabitants.Reported += events.Add;

        host.Inhabitants.Report(new WorldEvent(InhabitantsSystem.Combined, host.World.Tick, maker.Id, "combine", null) { TargetType = Plain, Detail = RuleOf(milestone.Process!) });
        host.World.Step();
        var reached = Assert.Single(events, e => e.Kind == MilestoneSystem.MilestoneReached);
        Assert.Equal((maker.Id, milestone.Id, Plain), (reached.InhabitantId, reached.Detail, reached.TargetType));
        Assert.True(host.Milestones.IsReached(milestone.Id));

        // The tech level rose with it: the inventions, the processes and the look epochs read the new one.
        var level = IndexOf(levels, milestone.RaisesTechLevel!);
        Assert.Equal(level, host.Milestones.TechLevelIndex);
        Assert.Equal(milestone.RaisesTechLevel, host.Milestones.TechLevel);
        Assert.Equal(level, host.LookEpoch.ReachedTechLevel);

        // Again: no second milestone.
        host.Inhabitants.Report(new WorldEvent(InhabitantsSystem.Combined, host.World.Tick, maker.Id, "combine", null) { TargetType = Plain, Detail = RuleOf(milestone.Process!) });
        host.World.Run(2);
        Assert.Single(events, e => e.Kind == MilestoneSystem.MilestoneReached);
    }

    [Fact]
    public void AMadeThingCountsByTheTrueTypePropertiesOfTheMilestone()
    {
        var host = World();
        var milestone = Content.Value.Milestones!.Milestones.First(m => m.MadeWith is { Count: > 0 } p && TypeWith(p) is not null);
        var type = TypeWith(milestone.MadeWith!)!;
        var other = Content.Value.Objects.TypeIds.First(t => !milestone.MadeWith!.All(p => Content.Value.Objects.HasProperty(t, p)));
        var maker = host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).First();

        // A thing without the properties is no milestone; one with them is.
        var plain = host.Objects.Spawn(other, new Position(maker.Position.X + 1, maker.Position.Z));
        host.Inhabitants.Report(new WorldEvent(InventionSystem.Built, host.World.Tick, maker.Id, null, plain.Id));
        host.World.Step();
        Assert.False(host.Milestones.IsReached(milestone.Id));

        var made = host.Objects.Spawn(type, new Position(maker.Position.X + 1, maker.Position.Z));
        host.Inhabitants.Report(new WorldEvent(InventionSystem.Built, host.World.Tick, maker.Id, null, made.Id));
        host.World.Step();
        var reached = host.Milestones.Reached.Single(r => r.Id == milestone.Id);
        Assert.Equal((maker.Id, type), (reached.InhabitantId, reached.TypeId));
        Assert.True(host.Milestones.HasState);
    }

    [Fact]
    public void ReachedMilestonesAndTheirTechLevelSurviveSavingAndLoading()
    {
        var host = World();
        var start = host.Milestones.TechLevelIndex;
        var levels = Content.Value.Inventions.TechLevels;
        var milestone = Content.Value.Milestones!.Milestones.First(m => m.Process is not null && m.RaisesTechLevel is { } l && IndexOf(levels, l) > start);
        var maker = host.Inhabitants.All.OrderBy(i => i.Id, StringComparer.Ordinal).First();
        host.Inhabitants.Report(new WorldEvent(InhabitantsSystem.Combined, host.World.Tick, maker.Id, "combine", null) { TargetType = Plain, Detail = RuleOf(milestone.Process!) });
        host.World.Run(2);

        var path = Path.Combine(Path.GetTempPath(), "gw-milestones-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            SaveGameStore.Save(path, StateCodec.Capture(host));
            var loaded = StateCodec.Restore(Factory(), SaveGameStore.Load(path));
            Assert.Equal(host.World.StateHash(), loaded.World.StateHash());
            Assert.True(loaded.Milestones.IsReached(milestone.Id));
            Assert.Equal(host.Milestones.TechLevelIndex, loaded.Milestones.TechLevelIndex);
            Assert.Equal(host.Milestones.TechLevelIndex, loaded.LookEpoch.ReachedTechLevel);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheInventionCheckAllowsTheReachedLevelNotOnlyTheStartLevel()
    {
        var rules = Content.Value.Inventions;
        var start = IndexOf(rules.TechLevels, rules.Settings.TechLevel);
        var higher = rules.TechLevels[start + 1];
        var example = File.ReadAllText(Path.Combine(TestData.Root, "contracts", "examples", "valid", "job-status.invention-approved.json"));
        var result = ContractSerializer.Deserialize<JobStatus>(example).Result!.Invention!;
        result.Invention!.TechLevel = higher;
        Assert.Contains(InventionCheck.TechLevel, InventionCheck.Check(result, new HashSet<string>(), rules));
        Assert.DoesNotContain(InventionCheck.TechLevel, InventionCheck.Check(result, new HashSet<string>(), rules, higher));
    }

    [Fact]
    public void AProcessRuleAboveTheStartLevelAppliesOnceTheWorldReachedItsLevel()
    {
        // Review GW4 #356: the data's rules (Match) follow the reached level like the candidates do.
        var processes = Content.Value.Processes!;
        var levels = Content.Value.Inventions.TechLevels;
        var start = IndexOf(levels, Content.Value.Inventions.Settings.TechLevel);
        var data = ContractSerializer.Deserialize<ProcessRules>(File.ReadAllText(Path.Combine(ContentDirectory, "processes", "rules.json")));
        var rule = data.Rules.First(r => r.HeatMin is null && IndexOf(levels, r.TechLevel) > start);
        var level = IndexOf(levels, rule.TechLevel);
        Assert.False(processes.Applies(rule));
        Assert.False(processes.Applies(rule, start));
        Assert.True(processes.Applies(rule, level));
    }

    private static string? TypeWith(IEnumerable<string> properties) =>
        Content.Value.Objects.TypeIds.FirstOrDefault(t => properties.All(p => Content.Value.Objects.HasProperty(t, p)));

    private static int IndexOf(IReadOnlyList<string> levels, string id)
    {
        for (var i = 0; i < levels.Count; i++)
        {
            if (levels[i] == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static SimulationHost World() => SimulationHost.CreatePlaceholderWorld(ContentDirectory, 1, new MockDecisionPort(), "de");

    private static SimulationHostFactory Factory()
    {
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(ContentDirectory, "decider", "settings.json")));
        return new SimulationHostFactory(ContentSet.Load(ContentDirectory), settings, new MockDecisionPort(), "de");
    }

    /// <summary>A process rule of the data with the milestone's verb (D1096 #4: milestones know verbs, rules tell theirs).</summary>
    private static string RuleOf(string process) =>
        Content.Value.Processes!.RuleIds.First(r => Content.Value.Processes.ProcessOf(r) == process);

    /// <summary>A type that fits no made_with milestone, so only the process milestone is reached by the test's event.</summary>
    private static string Plain => Content.Value.Objects.TypeIds.First(t => Content.Value.Milestones!.Milestones
        .All(m => m.MadeWith is not { Count: > 0 } props || !props.All(p => Content.Value.Objects.TrueProperties(t).Contains(p))));
}
