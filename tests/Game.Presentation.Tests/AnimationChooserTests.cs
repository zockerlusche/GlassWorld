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
    public void UnitIsInRangeAndStable()
    {
        Assert.Equal(AnimationChooser.Unit("inh-000001", 7), AnimationChooser.Unit("inh-000001", 7));
        Assert.All(Enumerable.Range(0, 1000), s => Assert.InRange(AnimationChooser.Unit("inh-000009", s), 0.0, 0.9999999));
    }
}
