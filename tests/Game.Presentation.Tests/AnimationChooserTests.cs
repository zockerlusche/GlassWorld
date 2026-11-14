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
    public void TheCoreGaitPlaysItsOwnClipAndEveryCoreGaitIsInTheLibrary()
    {
        var library = Library();
        var chooser = new AnimationChooser(library);
        foreach (var gait in library.Gaits)
        {
            var core = Assert.NotNull(chooser.ForGait(gait.Id));
            Assert.Equal((gait.Clip, (double)gait.MinSpeedCmS, (double)gait.GroundSpeedCmS), (core.Clip.ClipId, core.Blend, core.GroundSpeedCmS));
        }

        Assert.Null(chooser.ForGait("no_such_gait"));

        // Every gait the core can report (content/behaviour/gaits.json) has a clip in the library.
        var coreGaits = GlassWorld.Contracts.Validation.ContractSerializer.Deserialize<GaitSettings>(
            File.ReadAllText(Path.Combine(TestPaths.Root, "content", "behaviour", "gaits.json")));
        Assert.All(coreGaits.Gaits, g => Assert.NotNull(chooser.ForGait(g.Id)));
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

        // A clip not made yet (here: sleep as it was before W1695) lets the idle behaviour stand in.
        var unmade = Library();
        var sleepClip = unmade.Clips.Single(c => c.Id == "sleep");
        sleepClip.Source = AnimationClipSource.Placeholder;
        sleepClip.Asset = null;
        var withoutSleep = new AnimationChooser(unmade);
        for (long slot = 0; slot < 40; slot++)
        {
            var expected = chooser.Idle("inh-000001", slot, values);
            Assert.Equal(chooser.ForIdle("inh-000001", slot, values), expected.Clip);
            Assert.Equal(expected, chooser.ForInhabitant("idle", false, "inh-000001", slot, values));
            Assert.Equal(expected, chooser.ForInhabitant(null, false, "inh-000001", slot, values));

            // The idle behaviour stands in and the missing clip is named for the log.
            var sleeping = withoutSleep.ForInhabitant("sleep", false, "inh-000001", slot, values);
            Assert.Equal(expected.Clip, sleeping.Clip);
            Assert.Equal((expected.Speed, expected.Mirror), (sleeping.Speed, sleeping.Mirror));
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
        Assert.Equal(chooser.Idle("inh-000001", 3, values), waiting);
        Assert.Equal((eat.Clip.Speed, false), (eat.Speed, eat.Mirror));
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

    [Fact]
    public void AnActionOnAThingOnTheGroundCrouchesOnABushItReachesForward()
    {
        // A1632 (D958 D1): the mapping by the thing's reach wins over the target kind alone; without a reach the general clip.
        var library = GlassWorld.Contracts.Validation.ContractSerializer.Deserialize<AnimationLibrary>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "animation", "library.json")));
        var chooser = new AnimationChooser(library);
        Assert.Equal("crouch_act", chooser.ForAction("inspect", AnimationMappingTarget.Object, AnimationMappingReach.Low).ClipId);
        // An action at the ground is an action, not idle variety: it shows at once and is not held min_pose_s (D958 D2).
        Assert.False(chooser.IsIdleClip("crouch_act"));
        Assert.Equal("handle", chooser.ForAction("inspect", AnimationMappingTarget.Object, AnimationMappingReach.Mid).ClipId);
        Assert.Equal("handle", chooser.ForAction("inspect", AnimationMappingTarget.Object).ClipId);
        Assert.Equal("talk", chooser.ForAction("show", AnimationMappingTarget.Inhabitant, AnimationMappingReach.Low).ClipId);
    }
}
