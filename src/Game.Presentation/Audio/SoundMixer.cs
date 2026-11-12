using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;

namespace GlassWorld.Presentation.Audio;

/// <summary>
/// Spatial loudness of the UI standard for sound (B8 R7, B2512): how loud a sound is at a distance
/// from the listener (camera), with zoom changing the hearing range. All numbers come from
/// <c>content/sound/mix.json</c>. Godot only pans and plays; the volume comes from here, so the rule
/// "quieter with distance, silent beyond the range" is the same in tests and in the game.
/// </summary>
public sealed class SoundMixer
{
    /// <summary>Volume Godot gets for a silent sound (dB).</summary>
    public const double SilentDb = -80;

    private readonly Dictionary<MixBusId, MixBus> _buses;

    private SoundMixer(SoundMix mix)
    {
        Mix = mix;
        _buses = mix.Buses.ToDictionary(b => b.Id);
    }

    /// <summary>The mix data.</summary>
    public SoundMix Mix { get; }

    /// <summary>Parses and validates <c>content/sound/mix.json</c>.</summary>
    public static SoundMixer FromJson(string json)
    {
        var mix = ContractSerializer.Deserialize<SoundMix>(json);
        var missing = Enum.GetValues<MixBusId>().Where(id => mix.Buses.Count(b => b.Id == id) != 1).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidDataException("every bus must exist exactly once: " + string.Join(", ", missing));
        }

        var broken = mix.Buses.Where(b => b.SilentM > 0 && b.SilentM <= b.FullVolumeM).Select(b => b.Id).ToList();
        return broken.Count == 0 ? new SoundMixer(mix) : throw new InvalidDataException("silent_m must be beyond full_volume_m: " + string.Join(", ", broken));
    }

    /// <summary>
    /// While the camera follows an inhabitant (B2605, B8 R7): nothing farther than this is heard, whatever the
    /// bus's range - the picture's reach around the inhabitant (<c>CameraModel.SeenRadiusM</c>). Null = no limit
    /// beyond the buses' own ranges (the free camera).
    /// </summary>
    public double? HearingLimitM { get; set; }

    /// <summary>A bus.</summary>
    public MixBus Bus(MixBusId id) => _buses[id];

    /// <summary>Hearing range factor for a zoom position (0 = fully in, 1 = fully out).</summary>
    public double ZoomFactor(double zoom) =>
        Mix.Zoom.NearFactor + ((Mix.Zoom.FarFactor - Mix.Zoom.NearFactor) * Math.Clamp(zoom, 0, 1));

    /// <summary>Distance from which a bus is silent at this zoom (0 = not spatial).</summary>
    public double SilentDistance(MixBusId bus, double zoom) => Limited(Bus(bus).SilentM * ZoomFactor(zoom));

    /// <summary>Gain 0..1 of a sound on <paramref name="bus"/> at <paramref name="distance"/> metres.</summary>
    public double Gain(MixBusId bus, double distance, double zoom)
    {
        var b = Bus(bus);
        if (b.SilentM <= 0)
        {
            return 1;
        }

        var factor = ZoomFactor(zoom);
        var silent = Limited(b.SilentM * factor);
        var full = Math.Min(b.FullVolumeM * factor, silent);
        if (distance <= full)
        {
            return 1;
        }

        if (distance >= silent)
        {
            return 0;
        }

        var rest = 1 - ((distance - full) / (silent - full));
        return b.Curve == MixBusCurve.Smooth ? rest * rest : rest;
    }

    private double Limited(double silent) => HearingLimitM is { } limit && limit > 0 ? Math.Min(silent, limit) : silent;

    /// <summary>Gain as decibels for Godot (silent = <see cref="SilentDb"/>).</summary>
    public static double ToDb(double gain) => gain <= 0 ? SilentDb : Math.Max(SilentDb, 20 * Math.Log10(gain));
}
