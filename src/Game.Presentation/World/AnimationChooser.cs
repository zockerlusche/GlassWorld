using System.Collections.Frozen;
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

/// <summary>What a figure shows after a tick.</summary>
/// <param name="Clip">The action or idle clip (when walking: the action's clip, the gait plays instead).</param>
/// <param name="Walking">True when the figure walks (the gait blend plays).</param>
/// <param name="Missing">The action's clip when it was not made yet and the idle behaviour stands in (log it once).</param>
public readonly record struct FigureClip(ClipChoice Clip, bool Walking, ClipChoice? Missing)
{
    /// <summary>Playback speed: the clip's own, varied a little for idle behaviours (data: speed_spread).</summary>
    public double Speed { get; init; } = Clip.Speed;

    /// <summary>Whether the clip plays mirrored, left and right swapped (idle variety, data: mirror).</summary>
    public bool Mirror { get; init; }
}

/// <summary>
/// Chooses figure clips from <c>content/animation/library.json</c> without Godot (B2 R27–R29): the clip of an
/// action or expression, the gait by speed and the idle behaviour between tasks. Idle choice is weighted by
/// core values (needs, moods, traits 0..1) and drawn from a hash of inhabitant id and time slot, so
/// screenshots repeat exactly; it never touches the world (not in the hash). Each draw also varies the
/// playback speed and may mirror the clip (data), and every inhabitant has its own slot length and phase
/// (<see cref="IdleSlot"/>), so figures do not change their idle behaviour in lock-step (F5282). One chooser
/// serves all figures of a scene; it keeps reusable buffers, so it is not thread-safe.
/// </summary>
public sealed class AnimationChooser
{
    // Independent draws from the same inhabitant id and slot (Unit with a salt).
    private const ulong SaltSpeed = 1;
    private const ulong SaltMirror = 2;
    private const ulong SaltSlotLength = 3;
    private const ulong SaltSlotPhase = 4;

    private readonly Dictionary<string, ClipChoice> _choices;
    private readonly Dictionary<(string Kind, string Id), string> _mappings;
    private readonly Dictionary<(string Action, AnimationMappingTarget Target), string> _actionsByTarget;
    private readonly ClipChoice _fallback;
    private readonly AnimationGait[] _gaits;
    private readonly (string Clip, double Weight, (string Value, bool High, double Weight)[] Factors, double SpeedSpread, bool Mirror)[] _idle;
    private readonly HashSet<string> _idleClips;
    private readonly HashSet<string> _gaitClips;
    private readonly double[] _weights;

    /// <summary>Creates the chooser over the library data.</summary>
    public AnimationChooser(AnimationLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var clips = library.Clips.ToDictionary(c => c.Id, StringComparer.Ordinal);
        _choices = clips.Values.Where(c => c.Source == AnimationClipSource.Asset && c.Asset is not null)
            .ToDictionary(c => c.Id, c => new ClipChoice(c.Id, c.Asset!, c.Speed ?? 1.0, c.Loop, FellBack: false), StringComparer.Ordinal);
        _mappings = library.Mappings.Where(m => m.Target is null).ToDictionary(m => (m.Kind.ToString().ToLowerInvariant(), m.Id), m => m.Clip);
        _actionsByTarget = library.Mappings.Where(m => m.Kind == AnimationMappingKind.Action && m.Target is not null)
            .ToDictionary(m => (m.Id, m.Target!.Value), m => m.Clip);
        _fallback = _choices.TryGetValue(library.FallbackClip, out var f)
            ? f with { FellBack = true }
            : throw new InvalidDataException($"Fallback clip '{library.FallbackClip}' must be an asset clip.");
        _gaits = [.. library.Gaits.OrderBy(g => g.MinSpeedCmS)];
        _idle = [.. library.Idle.Select(i => (i.Clip, i.Weight, i.Factors.Select(x => (x.Value, x.When == AnimationIdleFactorWhen.High, x.Weight)).ToArray(), i.SpeedSpread ?? 0, i.Mirror ?? false))];
        _idleClips = library.Idle.Select(i => i.Clip).ToHashSet(StringComparer.Ordinal);
        _gaitClips = library.Gaits.Select(g => g.Clip).ToHashSet(StringComparer.Ordinal);
        _weights = new double[_idle.Length];
    }

    /// <summary>
    /// The clip for an action id from the vocabulary; given the kind of its target, a mapping for that kind
    /// wins over the general one (data: showing something to an inhabitant talks instead of bending down, W1687).
    /// </summary>
    public ClipChoice ForAction(string action, AnimationMappingTarget? target = null) =>
        Resolve((target is { } kind ? _actionsByTarget.GetValueOrDefault((action, kind)) : null) ?? _mappings.GetValueOrDefault(("action", action)));

    /// <summary>The clip for an expression id from the vocabulary.</summary>
    public ClipChoice ForExpression(string expression) => Resolve(_mappings.GetValueOrDefault(("expression", expression)));

    /// <summary>The clip for a work activity of the library (carry, hammer, chop …).</summary>
    public ClipChoice ForWork(string work) => Resolve(_mappings.GetValueOrDefault(("work", work)));

    /// <summary>The gait clip for a game-time walking speed in cm per game second (the fastest gait whose minimum is reached).</summary>
    public ClipChoice ForSpeed(double speedCmS) => Resolve(GaitFor(speedCmS)?.Clip);

    /// <summary>
    /// The gait the core reports (InhabitantSnapshot.Gait, content/behaviour/gaits.json ids) as a point of the
    /// gait blend: its clip, the blend position that plays it alone and its ground speed; null if the library
    /// has no gait with this id (then the presentation falls back to the speed).
    /// </summary>
    public (ClipChoice Clip, double Blend, double GroundSpeedCmS)? ForGait(string gaitId)
    {
        ArgumentNullException.ThrowIfNull(gaitId);
        foreach (var gait in _gaits)
        {
            if (string.Equals(gait.Id, gaitId, StringComparison.Ordinal))
            {
                return (Resolve(gait.Clip), gait.MinSpeedCmS, gait.GroundSpeedCmS);
            }
        }

        return null;
    }

    /// <summary>
    /// The ground speed of the gait blend at a game-time speed (cm/s at playback 1, scale 1): linear between
    /// the two gait points around it, as the blend space mixes them; the first or last outside.
    /// </summary>
    public double GroundSpeedCmS(double speedCmS)
    {
        if (_gaits.Length == 0)
        {
            return 0;
        }

        if (speedCmS <= _gaits[0].MinSpeedCmS)
        {
            return _gaits[0].GroundSpeedCmS;
        }

        for (var i = 1; i < _gaits.Length; i++)
        {
            var (a, b) = (_gaits[i - 1], _gaits[i]);
            if (speedCmS <= b.MinSpeedCmS)
            {
                var t = b.MinSpeedCmS > a.MinSpeedCmS ? (speedCmS - a.MinSpeedCmS) / (b.MinSpeedCmS - a.MinSpeedCmS) : 1;
                return a.GroundSpeedCmS + ((b.GroundSpeedCmS - a.GroundSpeedCmS) * t);
            }
        }

        return _gaits[^1].GroundSpeedCmS;
    }

    /// <summary>
    /// What a figure shows after a tick (B2 R28): it walks (gait blend) when the core moved it, whatever the
    /// action (actions walk to their target first). Standing, it plays its action's clip; an action whose
    /// clip is an idle behaviour (the action <c>idle</c>), no action, or an action whose clip is not made yet
    /// play the idle behaviour from core values instead (the missing clip is returned for the log).
    /// </summary>
    public FigureClip ForInhabitant(string? action, bool moving, string inhabitantId, long slot, IReadOnlyDictionary<string, double> values, AnimationMappingTarget? target = null)
    {
        ClipChoice? missing = null;
        if (action is not null)
        {
            var clip = ForAction(action, target);
            if (moving)
            {
                return new FigureClip(clip, Walking: true, null);
            }

            if (clip.FellBack)
            {
                missing = clip;
            }
            else if (!_idleClips.Contains(clip.ClipId) && !_gaitClips.Contains(clip.ClipId))
            {
                // A walking action that does not move (waiting, arrived) stands in its idle behaviour too.
                return new FigureClip(clip, Walking: false, null);
            }
        }
        else if (moving)
        {
            return new FigureClip(_fallback, Walking: true, null);
        }

        return Idle(inhabitantId, slot, values) with { Missing = missing };
    }

    /// <summary>
    /// The idle behaviour for an inhabitant in a time slot: weight = base + Σ factor weight × closeness to the
    /// extreme ("high": value, "low": 1 − value); the draw comes from a hash of id and slot.
    /// </summary>
    public ClipChoice ForIdle(string inhabitantId, long slot, IReadOnlyDictionary<string, double> values)
    {
        var index = IdleIndex(inhabitantId, slot, values);
        return index < 0 ? _fallback : Resolve(_idle[index].Clip);
    }

    /// <summary>
    /// The idle behaviour as a figure plays it (<see cref="ForIdle"/>) with its variety: the playback speed
    /// varies within the behaviour's speed_spread, and it plays mirrored in half of the draws where the data
    /// allows it; all from a hash of id and slot, so the same slot looks the same again.
    /// </summary>
    public FigureClip Idle(string inhabitantId, long slot, IReadOnlyDictionary<string, double> values)
    {
        var index = IdleIndex(inhabitantId, slot, values);
        if (index < 0)
        {
            return new FigureClip(_fallback, Walking: false, null);
        }

        var (clip, _, _, spread, mirror) = _idle[index];
        var choice = Resolve(clip);
        return new FigureClip(choice, Walking: false, null)
        {
            Speed = choice.Speed * (1 + (spread * ((2 * Unit(inhabitantId, slot, SaltSpeed)) - 1))),
            Mirror = mirror && Unit(inhabitantId, slot, SaltMirror) < 0.5,
        };
    }

    /// <summary>
    /// The idle slot of an inhabitant at a core tick: each inhabitant has its own slot length (idle_slot_ticks
    /// ± the spread share) and phase, both from its id, so figures standing together change their idle
    /// behaviour at different ticks and drift apart instead of moving in lock-step.
    /// </summary>
    public static long IdleSlot(string inhabitantId, long tick, int slotTicks, double spread)
    {
        ArgumentNullException.ThrowIfNull(inhabitantId);
        ArgumentOutOfRangeException.ThrowIfLessThan(slotTicks, 1);
        var factor = 1 + (Math.Clamp(spread, 0, 0.9) * ((2 * Unit(inhabitantId, 0, SaltSlotLength)) - 1));
        var length = Math.Max(1, (long)Math.Round(slotTicks * factor));
        var phase = (long)(Unit(inhabitantId, 0, SaltSlotPhase) * length);
        return (long)Math.Floor((tick + phase) / (double)length);
    }

    private int IdleIndex(string inhabitantId, long slot, IReadOnlyDictionary<string, double> values)
    {
        ArgumentNullException.ThrowIfNull(inhabitantId);
        ArgumentNullException.ThrowIfNull(values);
        var total = 0.0;
        for (var i = 0; i < _idle.Length; i++)
        {
            var weight = _idle[i].Weight;
            foreach (var (value, high, factor) in _idle[i].Factors)
            {
                var v = Math.Clamp(values.GetValueOrDefault(value, 0.5), 0, 1);
                weight += factor * (high ? v : 1 - v);
            }

            _weights[i] = weight;
            total += weight;
        }

        if (total <= 0)
        {
            return -1;
        }

        var pick = Unit(inhabitantId, slot) * total;
        for (var i = 0; i < _weights.Length; i++)
        {
            pick -= _weights[i];
            if (pick < 0)
            {
                return i;
            }
        }

        return _idle.Length - 1;
    }

    /// <summary>
    /// Fills <paramref name="target"/> with the core values the idle weights read: needs, then moods, then
    /// traits (the first one of an id wins). The buffer is reused, so a tick allocates nothing.
    /// </summary>
    public static void CoreValues(Dictionary<string, double> target, FrozenDictionary<string, double> needs, FrozenDictionary<string, double> moods, FrozenDictionary<string, double> traits)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(needs);
        ArgumentNullException.ThrowIfNull(moods);
        ArgumentNullException.ThrowIfNull(traits);
        target.Clear();
        foreach (var (key, value) in needs)
        {
            target.TryAdd(key, value);
        }

        foreach (var (key, value) in moods)
        {
            target.TryAdd(key, value);
        }

        foreach (var (key, value) in traits)
        {
            target.TryAdd(key, value);
        }
    }

    /// <summary>A number in [0, 1) from inhabitant id and slot (FNV-1a, then SplitMix64): same input, same number.</summary>
    public static double Unit(string inhabitantId, long slot) => Unit(inhabitantId, slot, 0);

    /// <summary>
    /// A number in [0, 1) from inhabitant id, slot and a salt: different salts give independent draws for the
    /// same id and slot (salt 0 is <see cref="Unit(string, long)"/>).
    /// </summary>
    public static double Unit(string inhabitantId, long slot, ulong salt)
    {
        ArgumentNullException.ThrowIfNull(inhabitantId);
        var max = Encoding.UTF8.GetMaxByteCount(inhabitantId.Length);
        Span<byte> bytes = max <= 256 ? stackalloc byte[max] : new byte[max];
        var hash = 14695981039346656037UL;
        foreach (var b in bytes[..Encoding.UTF8.GetBytes(inhabitantId, bytes)])
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        var z = hash ^ (ulong)slot ^ (salt * 0xD6E8FEB86659FD93UL);
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }

    private AnimationGait? GaitFor(double speedCmS)
    {
        AnimationGait? gait = _gaits.Length > 0 ? _gaits[0] : null;
        foreach (var g in _gaits)
        {
            if (g.MinSpeedCmS <= speedCmS)
            {
                gait = g;
            }
        }

        return gait;
    }

    private ClipChoice Resolve(string? clipId)
    {
        if (clipId is null)
        {
            return _fallback;
        }

        if (!_choices.TryGetValue(clipId, out var choice))
        {
            // Unknown, not made yet, a blend or procedural: the fallback clip plays (the caller logs it once).
            choice = _fallback with { ClipId = clipId };
            _choices[clipId] = choice;
        }

        return choice;
    }
}
