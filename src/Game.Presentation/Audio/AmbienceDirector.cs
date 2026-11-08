using GlassWorld.Contracts;

namespace GlassWorld.Presentation.Audio;

/// <summary>What the ambience player does now.</summary>
/// <param name="Start">Loop to fade in, or null.</param>
/// <param name="FadeS">Cross-fade time (the old loop fades out over the same time).</param>
public sealed record AmbienceStep(string? Start, double FadeS);

/// <summary>
/// Biome ambience (B8 R7): a calm loop for the biome under the listener (sound map, ambience table).
/// A change of biome cross-fades; a new biome must hold for a moment before the change, so walking along
/// a border does not flicker. Real seconds drive it. Presentation only.
/// </summary>
public sealed class AmbienceDirector(SoundLibrary library, MixAmbience settings)
{
    private readonly SoundLibrary _library = library ?? throw new ArgumentNullException(nameof(library));
    private readonly MixAmbience _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private string? _candidate;
    private bool _hasCandidate;
    private double _candidateSince;

    /// <summary>The loop playing now (null = none).</summary>
    public string? Current { get; private set; }

    /// <summary>The loop for a biome, or null (no entry or no biome).</summary>
    public string? SoundFor(string? biome) =>
        biome is null ? null : _library.Map.Ambience.FirstOrDefault(a => a.Biome == biome)?.Sound;

    /// <summary>Advances to <paramref name="now"/> (seconds) with the biome under the listener.</summary>
    public AmbienceStep Update(double now, string? biome)
    {
        var wanted = SoundFor(biome);
        if (wanted == Current)
        {
            _hasCandidate = false;
            return new AmbienceStep(null, 0);
        }

        // "No loop" is a candidate too (leaving the map), so it waits for the hold like any change.
        if (!_hasCandidate || wanted != _candidate)
        {
            _candidate = wanted;
            _hasCandidate = true;
            _candidateSince = now;
        }

        // The first loop starts at once; later changes wait for the hold time.
        if (Current is not null && now - _candidateSince < _settings.HoldS)
        {
            return new AmbienceStep(null, 0);
        }

        Current = wanted;
        _hasCandidate = false;
        return new AmbienceStep(wanted, _settings.FadeS);
    }
}
