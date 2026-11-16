using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>How a carried thing sits (A1785): both hands or one, its lie, the height of the hands and how far in front.</summary>
/// <param name="TwoHands">Held with both hands.</param>
/// <param name="Lie">across, slanted or upright.</param>
/// <param name="HeightM">Height of the hands above the feet, metres for size 1.</param>
/// <param name="ForwardM">Distance in front of the body, metres for size 1.</param>
public sealed record CarriedLook(bool TwoHands, CarryWayLie Lie, double HeightM, double ForwardM);

/// <summary>
/// What an inhabitant carries sits in its hands (A1785, B2823), without Godot: the first kind of look/carried.json whose
/// types name the thing or whose properties it all has; else the calm default (lying across in front of the chest).
/// </summary>
public static class CarriedPose
{
    /// <summary>The look of a carried thing of a type with these TRUE properties.</summary>
    public static CarriedLook For(LookCarried data, string typeId, IReadOnlySet<string> properties)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(properties);
        var way = data.Kinds.FirstOrDefault(k => (k.Types?.Contains(typeId, StringComparer.Ordinal) ?? false)
            || (k.Properties is { Count: > 0 } all && all.All(properties.Contains)))?.Way ?? data.Default;
        var height = way.At switch
        {
            CarryWayAt.Hip => data.Heights.Hip,
            CarryWayAt.Shoulder => data.Heights.Shoulder,
            _ => data.Heights.Chest,
        };
        return new CarriedLook(way.Grip == CarryWayGrip.Two, way.Lie, height, data.ForwardM);
    }

    /// <summary>The gesture that holds it: both arms or the right one (look figures carry_two / carry_one).</summary>
    public static string GestureOf(CarriedLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        return look.TwoHands ? "carry_two" : "carry_one";
    }
}
