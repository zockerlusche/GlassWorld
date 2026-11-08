using System.Collections.Frozen;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

public sealed class AnimationChooserTests
{
    private static AnimationLibrary Library() =>
        ContractSerializer.Deserialize<AnimationLibrary>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "animation", "library.json")));

    [Fact]
    public void ActionsGetTheirAssetClipAndUnknownOnesFallBack()
    {
        var chooser = new AnimationChooser(Library());
        var walk = chooser.ForAction("move_to");
        Assert.Equal(("walk", "Walk", false), (walk.ClipId, walk.Asset, walk.FellBack));
        var unknown = chooser.ForAction("no_such_action");
        Assert.True(unknown.FellBack);
        Assert.Equal(Library().Clips.Single(c => c.Id == Library().FallbackClip).Asset, unknown.Asset);
    }

    [Fact]
    public void ClipsNotMadeYetPlayTheFallbackAndSaySo()
    {
        var library = Library();
        var placeholder = library.Clips.First(c => c.Source == AnimationClipSource.Placeholder);
        var mapping = library.Mappings.First(m => m.Clip == placeholder.Id);
        var chooser = new AnimationChooser(library);
        var choice = mapping.Kind switch
        {
            AnimationMappingKind.Action => chooser.ForAction(mapping.Id),
            AnimationMappingKind.Expression => chooser.ForExpression(mapping.Id),
            _ => chooser.ForWork(mapping.Id),
        };
        Assert.True(choice.FellBack);
        Assert.Equal(placeholder.Id, choice.ClipId);
    }

    [Fact]
    public void TheGaitFollowsTheSpeed()
    {
        var library = Library();
        var chooser = new AnimationChooser(library);
        foreach (var gait in library.Gaits)
        {
            Assert.Equal(gait.Clip, chooser.ForSpeed(gait.MinSpeedCmS + 1).ClipId);
        }

        Assert.Equal(library.Gaits.OrderBy(g => g.MinSpeedCmS).First().Clip, chooser.ForSpeed(0).ClipId);
    }

    [Fact]
    public void IdleIsReproducibleForTheSameInhabitantAndSlot()
    {
        var chooser = new AnimationChooser(Library());
        var values = new Dictionary<string, double> { ["sleep"] = 0.5 };
        for (long slot = 0; slot < 50; slot++)
        {
            Assert.Equal(chooser.ForIdle("inh-000001", slot, values), chooser.ForIdle("inh-000001", slot, values));
        }

        var a = Enumerable.Range(0, 50).Select(s => chooser.ForIdle("inh-000001", s, values).ClipId);
        var b = Enumerable.Range(0, 50).Select(s => chooser.ForIdle("inh-000002", s, values).ClipId);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void CoreValuesShiftTheIdleWeights()
    {
        // "yawn" grows when sleep is low (data): over many slots a tired inhabitant yawns more often.
        var chooser = new AnimationChooser(Library());
        int Count(double sleep) => Enumerable.Range(0, 2000)
            .Count(s => chooser.ForIdle("inh-000003", s, new Dictionary<string, double> { ["sleep"] = sleep }).ClipId == "yawn");
        Assert.True(Count(0.0) > Count(1.0) * 2, $"tired {Count(0.0)} vs rested {Count(1.0)}");
    }

    [Fact]
    public void TheIdleActionAndMissingClipsPlayTheIdleBehaviourFromCoreValues()
    {
        var library = Library();
        var chooser = new AnimationChooser(library);
        var values = new Dictionary<string, double> { ["sleep"] = 0.2, ["curiosity"] = 0.8 };
        var idleClips = library.Idle.Select(i => i.Clip).ToHashSet(StringComparer.Ordinal);
        for (long slot = 0; slot < 40; slot++)
        {
            var expected = chooser.ForIdle("inh-000001", slot, values);
            Assert.Equal(new FigureClip(expected, false, null), chooser.ForInhabitant("idle", false, "inh-000001", slot, values));
            Assert.Equal(new FigureClip(expected, false, null), chooser.ForInhabitant(null, false, "inh-000001", slot, values));

            // Sleep has no clip yet: the idle behaviour stands in and the missing clip is named for the log.
            var sleeping = chooser.ForInhabitant("sleep", false, "inh-000001", slot, values);
            Assert.Equal(expected, sleeping.Clip);
            Assert.Equal("sleep", sleeping.Missing?.ClipId);
        }

        var drawn = Enumerable.Range(0, 40).Select(s => chooser.ForInhabitant("idle", false, "inh-000001", s, values).Clip.ClipId).ToHashSet();
        Assert.True(drawn.Count > 2, string.Join(",", drawn));
        Assert.All(drawn, c => Assert.Contains(c, idleClips));
    }

    [Fact]
    public void MovingFiguresWalkWhateverTheActionAndStandingWalkersIdle()
    {
        var chooser = new AnimationChooser(Library());
        var values = new Dictionary<string, double>();
        Assert.True(chooser.ForInhabitant("eat", true, "inh-000001", 0, values).Walking);
        Assert.True(chooser.ForInhabitant(null, true, "inh-000001", 0, values).Walking);
        Assert.True(chooser.ForInhabitant("move_to", true, "inh-000001", 0, values).Walking);

        var eat = chooser.ForInhabitant("eat", false, "inh-000001", 0, values);
        Assert.Equal((false, "eat"), (eat.Walking, eat.Clip.ClipId));
        var waiting = chooser.ForInhabitant("wander", false, "inh-000001", 3, values);
        Assert.Equal(new FigureClip(chooser.ForIdle("inh-000001", 3, values), false, null), waiting);
    }

    [Fact]
    public void GroundSpeedBlendsBetweenTheGaitPoints()
    {
        var library = Library();
        var chooser = new AnimationChooser(library);
        var gaits = library.Gaits.OrderBy(g => g.MinSpeedCmS).ToList();
        Assert.Equal(gaits[0].GroundSpeedCmS, chooser.GroundSpeedCmS(-5));
        Assert.Equal(gaits[^1].GroundSpeedCmS, chooser.GroundSpeedCmS(gaits[^1].MinSpeedCmS + 500));
        var (a, b) = (gaits[0], gaits[1]);
        var middle = (a.MinSpeedCmS + b.MinSpeedCmS) / 2.0;
        Assert.Equal((a.GroundSpeedCmS + b.GroundSpeedCmS) / 2.0, chooser.GroundSpeedCmS(middle), 9);
    }

    [Fact]
    public void CoreValuesFillTheReusedBufferFirstIdWins()
    {
        var buffer = new Dictionary<string, double> { ["old"] = 1 };
        var needs = new Dictionary<string, double> { ["sleep"] = 0.1, ["joy"] = 0.3 }.ToFrozenDictionary();
        var moods = new Dictionary<string, double> { ["joy"] = 0.9 }.ToFrozenDictionary();
        var traits = new Dictionary<string, double> { ["curiosity"] = 0.7 }.ToFrozenDictionary();
        AnimationChooser.CoreValues(buffer, needs, moods, traits);
        Assert.Equal(new Dictionary<string, double> { ["sleep"] = 0.1, ["joy"] = 0.3, ["curiosity"] = 0.7 }, buffer);
    }

    [Fact]
    public void UnitIsInRangeAndStable()
    {
        Assert.Equal(AnimationChooser.Unit("inh-000001", 7), AnimationChooser.Unit("inh-000001", 7));
        Assert.All(Enumerable.Range(0, 1000), s => Assert.InRange(AnimationChooser.Unit("inh-000009", s), 0.0, 0.9999999));
    }
}
