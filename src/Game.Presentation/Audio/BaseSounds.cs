using GlassWorld.Contracts;

namespace GlassWorld.Presentation.Audio;

/// <summary>
/// The base sound library at runtime (B8-4, B2530): which sound ids have recorded or generated files
/// (they take priority over the retro synth), how many variants each has, and the fallback from a
/// short sound description of the rule guardian to a base sound (keywords from the data) when no
/// generated sound exists (D471 D7).
/// </summary>
public sealed class BaseSounds
{
    private readonly Dictionary<string, LibrarySound> _sounds;

    /// <summary>Wraps the library data.</summary>
    public BaseSounds(BaseSoundLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        Library = library;
        _sounds = library.Sounds.ToDictionary(s => s.Id, StringComparer.Ordinal);
    }

    /// <summary>The data.</summary>
    public BaseSoundLibrary Library { get; }

    /// <summary>Number of file variants per sound id that has files.</summary>
    public IReadOnlyDictionary<string, int> Variants => _sounds.ToDictionary(s => s.Key, s => s.Value.Files.Count, StringComparer.Ordinal);

    /// <summary>File for a sound id and variant (variant wraps around), or null if the id has no file.</summary>
    public string? File(string soundId, int variant)
    {
        if (!_sounds.TryGetValue(soundId, out var sound))
        {
            return null;
        }

        var files = sound.Files.ToList();
        return files[((variant % files.Count) + files.Count) % files.Count].File;
    }

    /// <summary>True if the sound repeats seamlessly (ambience, fire).</summary>
    public bool IsLoop(string soundId) => _sounds.TryGetValue(soundId, out var sound) && sound.Kind == LibrarySoundKind.Loop;

    /// <summary>
    /// Base sound for a short English description (e.g. "wooden door creaks"): sound words count 2,
    /// context words (material, setting) 1; the best score wins, ties by id; a plural or verb "s"
    /// counts as the same word. Below the minimum score of the data the object stays silent rather than
    /// sounding wrong ("a wooden bowl" names only a material; GW1 review #62 K2).
    /// </summary>
    public string? Fallback(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var words = description.ToUpperInvariant()
            .Split([' ', ',', '.', ';', ':', '-', '!', '?', '\'', '"', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Stem)
            .ToHashSet(StringComparer.Ordinal);
        return _sounds.Values
            .Select(s => (s.Id, Score: (2 * s.SoundWords.Count(k => words.Contains(Stem(k.ToUpperInvariant())))) + s.ContextWords.Count(k => words.Contains(Stem(k.ToUpperInvariant())))))
            .Where(s => s.Score >= Library.FallbackMinScore)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => s.Id)
            .FirstOrDefault();
    }

    private static string Stem(string word) => word.Length > 3 && word.EndsWith('S') && !word.EndsWith("SS", StringComparison.Ordinal) ? word[..^1] : word;
}
