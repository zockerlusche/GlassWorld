using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1732 (B2716 (2)): feelings and moments show on the body from look/expressions.json.</summary>
public sealed class ExpressionLayerTests
{
    private static readonly Dictionary<string, double> Calm = new(StringComparer.Ordinal) { ["joy"] = 0.5, ["sadness"] = 0.1, ["fear"] = 0.1 };
    private static readonly Dictionary<string, double> Rested = new(StringComparer.Ordinal) { ["sleep"] = 0.9 };

    private static T Read<T>(params string[] path)
        where T : class =>
        ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine([TestPaths.Root, "content", .. path])));

    private static ExpressionLayer Layer() => new(Read<LookExpressions>("look", "expressions.json"), Read<LookFigures>("look", "figures.json").Gestures);

    [Fact]
    public void AnInventionIsCheeredForAMomentThenACalmPauseFollows()
    {
        var data = Read<LookExpressions>("look", "expressions.json");
        var layer = Layer();
        layer.Event("invented");
        var shown = layer.Update(0.1, Calm, Rested);
        Assert.Equal(("cheer", "smile", "arms_up"), (shown?.Id, shown?.Face, shown?.Gesture?.Id));

        var seconds = data.Expressions.Single(e => e.Id == "cheer").Seconds!.Value;
        Assert.Null(layer.Update(seconds, Calm, Rested)); // over, nothing else to show

        layer.Event("built"); // within the calm pause: no second cheer yet
        Assert.Null(layer.Update(0.1, Calm, Rested));

        layer.Update(data.CooldownS, Calm, Rested);
        layer.Event("built");
        Assert.Equal("cheer", layer.Update(0.1, Calm, Rested)?.Id);
    }

    [Fact]
    public void AMoodOrANeedShowsWhileItLastsAndAnEventComesFirst()
    {
        var layer = Layer();
        var sad = new Dictionary<string, double>(Calm) { ["sadness"] = 0.9 };
        Assert.Equal("sad", layer.Update(0.1, sad, Rested)?.Face);

        layer.Event("praised");
        Assert.Equal("glad", layer.Update(0.1, sad, Rested)?.Id); // the moment first, then the mood again

        var tired = new Dictionary<string, double>(StringComparer.Ordinal) { ["sleep"] = 0.1 };
        Assert.Equal("yawn", Layer().Update(0.1, Calm, tired)?.Gesture?.Id);
        Assert.Null(Layer().Update(0.1, Calm, Rested));

        var plain = Layer();
        plain.Event("action_started"); // an event without an expression in the data shows nothing
        Assert.Null(plain.Update(0.1, Calm, Rested));
    }

    [Fact]
    public void EveryTriggerAndEveryReferenceExists()
    {
        var data = Read<LookExpressions>("look", "expressions.json");
        var gestures = Read<LookFigures>("look", "figures.json").Gestures!.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        var events = Read<Vocabulary>("vocabulary", "events.json").Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var model = Read<InhabitantModel>("inhabitants", "model.json");
        var ids = data.Expressions.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        Assert.All(data.Expressions, e => Assert.True(e.Gesture is null || gestures.Contains(e.Gesture), e.Gesture));
        Assert.All(data.Events!, e => Assert.True(events.Contains(e.Event) && ids.Contains(e.Expression), e.Event));
        Assert.All(data.Moods!, m => Assert.True(model.Moods.Any(x => x.Id == m.Mood) && ids.Contains(m.Expression), m.Mood));
        Assert.All(data.Needs!, n => Assert.True(model.Needs.Any(x => x.Id == n.Need) && ids.Contains(n.Expression), n.Need));
    }
}
