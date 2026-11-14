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
    public bool IsLoop(string soundId) => _sounds.TryGetValue(soundId, out var sound) && sound.Kind == SoundKind.Loop;

    /// <summary>
    /// Base sound for a short English description (e.g. "wooden door creaks"): sound words count 2,
    /// context words (material, setting) 1; the best score wins, ties by id; a plural or verb "s"
    /// counts as the same word. Below the minimum score of the data the object stays silent rather than
    /// sounding wrong ("a wooden thing" names only a material; GW1 review #62 K2).
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
            .Select(s => (s.Id, Score: (2 * Matches(s.SoundWords, words)) + Matches(s.ContextWords.Except(s.SoundWords, StringComparer.Ordinal), words)))
            .Where(s => s.Score >= Library.FallbackMinScore)
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => s.Id)
            .FirstOrDefault();
    }

    /// <summary>Distinct word stems of a list that occur in the description (a stem counts once).</summary>
    private static int Matches(IEnumerable<string> list, HashSet<string> words) =>
        list.Select(k => Stem(k.ToUpperInvariant())).Distinct(StringComparer.Ordinal).Count(words.Contains);

    /// <summary>
    /// Small English stemmer for sound descriptions, applied to both sides: -ing, -ed, -es, -s and a
    /// final -e are cut when at least three letters stay, then a doubled final consonant is reduced
    /// (crackling/crackles/crackle, chopping/chop, creaking/creaks; GW1 review #62 R1).
    /// </summary>
    public static string Stem(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        var stem = word.ToUpperInvariant();
        foreach (var suffix in new[] { "ING", "ED", "ES", "S", "E" })
        {
            if (stem.EndsWith(suffix, StringComparison.Ordinal) && stem.Length - suffix.Length >= 3)
            {
                stem = stem[..^suffix.Length];
                break;
            }
        }

        return stem.Length >= 4 && stem[^1] == stem[^2] && !"AEIOU".Contains(stem[^1], StringComparison.Ordinal) ? stem[..^1] : stem;
    }
}
