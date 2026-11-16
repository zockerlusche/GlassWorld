using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1785 (B2823): what an inhabitant carries sits in its hands, its pose per kind from look/carried.json.</summary>
public sealed class CarriedPoseTests
{
    private static readonly LookCarried Data = Read<LookCarried>("look", "carried.json");

    private static T Read<T>(params string[] path)
        where T : class =>
        ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine([TestPaths.Root, "content", .. path])));

    private static HashSet<string> Props(params string[] ids) => new(ids, StringComparer.Ordinal);

    [Fact]
    public void AStoneGoesInBothHandsAtTheHipReedsSlantAndUnknownThingsLieAcrossTheChest()
    {
        var stone = CarriedPose.For(Data, "fieldstone", Props("stone", "hard", "portable"));
        Assert.Equal((true, CarryWayLie.Across, Data.Heights.Hip), (stone.TwoHands, stone.Lie, stone.HeightM));

        var reed = CarriedPose.For(Data, "reed", Props("fibrous", "portable"));
        Assert.Equal(CarryWayLie.Slanted, reed.Lie); // never upright as it grew (Marcel 15a)

        var other = CarriedPose.For(Data, "something_new", Props("portable"));
        Assert.Equal((true, CarryWayLie.Across, Data.Heights.Chest), (other.TwoHands, other.Lie, other.HeightM));
        Assert.True(other.HeightM > 0.5, "never at the knees");
    }

    [Fact]
    public void EveryKindNamesKnownTypesAndBothCarryGesturesExistAndHold()
    {
        var figures = Read<LookFigures>("look", "figures.json");
        foreach (var name in new[] { "carry_two", "carry_one" })
        {
            var gesture = Assert.Single(figures.Gestures!, g => g.Id == name);
            Assert.True(gesture.Steady);
            Assert.Equal(gesture.Influence, GestureCadence.At(gesture, 60).Weight, 6); // still held a minute later
        }

        var catalog = Read<ObjectTypes>("objects", "start-catalog.json").Types.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(Data.Kinds.SelectMany(k => k.Types ?? []), t => Assert.Contains(t, catalog));
    }
}
