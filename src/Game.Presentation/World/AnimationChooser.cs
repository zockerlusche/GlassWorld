using System.Text;
using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>What a figure plays: the library clip, the asset clip in the GLB and whether it fell back.</summary>
/// <param name="ClipId">Library clip id.</param>
/// <param name="Asset">Clip name in the figure GLB.</param>
/// <param name="Speed">Playback speed factor.</param>
/// <param name="Loop">Whether the clip loops.</param>
/// <param name="FellBack">True when the wanted clip is unknown or not made yet (log it, B2 R28).</param>
public sealed record ClipChoice(string ClipId, string Asset, double Speed, bool Loop, bool FellBack);

/// <summary>
/// Chooses figure clips from <c>content/animation/library.json</c> without Godot (B2 R27–R29): the clip of an
/// action or expression, the gait by speed and the idle behaviour between tasks. Idle choice is weighted by
/// core values (needs, moods, traits 0..1) and drawn from a hash of inhabitant id and time slot, so
/// screenshots repeat exactly; it never touches the world (not in the hash).
/// </summary>
public sealed class AnimationChooser
{
    private readonly Dictionary<string, AnimationClip> _clips;
    private readonly Dictionary<(string Kind, string Id), string> _mappings;
    private readonly AnimationClip _fallback;
    private readonly IReadOnlyList<AnimationGait> _gaits;
    private readonly IReadOnlyList<AnimationIdle> _idle;

    /// <summary>Creates the chooser over the library data.</summary>
    public AnimationChooser(AnimationLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        _clips = library.Clips.ToDictionary(c => c.Id, StringComparer.Ordinal);
        _mappings = library.Mappings.ToDictionary(m => (m.Kind.ToString().ToLowerInvariant(), m.Id), m => m.Clip);
        _fallback = _clips.TryGetValue(library.FallbackClip, out var f) && f.Source == AnimationClipSource.Asset
            ? f
            : throw new InvalidDataException($"Fallback clip '{library.FallbackClip}' must be an asset clip.");
        _gaits = [.. library.Gaits.OrderBy(g => g.MinSpeedCmS)];
        _idle = [.. library.Idle];
    }

    /// <summary>The clip for an action id from the vocabulary.</summary>
    public ClipChoice ForAction(string action) => Resolve(_mappings.GetValueOrDefault(("action", action)));

    /// <summary>The clip for an expression id from the vocabulary.</summary>
    public ClipChoice ForExpression(string expression) => Resolve(_mappings.GetValueOrDefault(("expression", expression)));

    /// <summary>The clip for a work activity of the library (carry, hammer, chop …).</summary>
    public ClipChoice ForWork(string work) => Resolve(_mappings.GetValueOrDefault(("work", work)));

    /// <summary>The gait clip for a walking speed in cm/s (the fastest gait whose minimum is reached).</summary>
    public ClipChoice ForSpeed(double speedCmS)
    {
        AnimationGait? gait = _gaits.Count > 0 ? _gaits[0] : null;
        foreach (var g in _gaits)
        {
            if (g.MinSpeedCmS <= speedCmS)
            {
                gait = g;
            }
        }

        return Resolve(gait?.Clip);
    }

    /// <summary>
    /// The idle behaviour for an inhabitant in a time slot: weight = base + Σ factor weight × closeness to the
    /// extreme ("high": value, "low": 1 − value); the draw comes from a hash of id and slot.
    /// </summary>
    public ClipChoice ForIdle(string inhabitantId, long slot, IReadOnlyDictionary<string, double> values)
    {
        ArgumentNullException.ThrowIfNull(inhabitantId);
        ArgumentNullException.ThrowIfNull(values);
        var weights = _idle.Select(i => i.Weight + i.Factors.Sum(f =>
        {
            var v = Math.Clamp(values.GetValueOrDefault(f.Value, 0.5), 0, 1);
            return f.Weight * (f.When == AnimationIdleFactorWhen.High ? v : 1 - v);
        })).ToList();
        var total = weights.Sum();
        if (total <= 0)
        {
            return Resolve(null);
        }

        var pick = Unit(inhabitantId, slot) * total;
        for (var i = 0; i < weights.Count; i++)
        {
            pick -= weights[i];
            if (pick < 0)
            {
                return Resolve(_idle[i].Clip);
            }
        }

        return Resolve(_idle[^1].Clip);
    }

    /// <summary>A number in [0, 1) from inhabitant id and slot (FNV-1a, then SplitMix64): same input, same number.</summary>
    public static double Unit(string inhabitantId, long slot)
    {
        ArgumentNullException.ThrowIfNull(inhabitantId);
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(inhabitantId))
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        var z = hash ^ (ulong)slot;
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }

    private ClipChoice Resolve(string? clipId)
    {
        if (clipId is not null && _clips.TryGetValue(clipId, out var clip) && clip.Source == AnimationClipSource.Asset && clip.Asset is not null)
        {
            return new ClipChoice(clip.Id, clip.Asset, clip.Speed ?? 1.0, clip.Loop, FellBack: false);
        }

        // Unknown, not made yet, a blend or procedural: the fallback clip plays (the caller logs it once).
        return new ClipChoice(clipId ?? _fallback.Id, _fallback.Asset!, _fallback.Speed ?? 1.0, _fallback.Loop, FellBack: true);
    }
}
