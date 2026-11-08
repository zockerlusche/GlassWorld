using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.Audio;

/// <summary>The sound of an invented thing: its own generated file, or a base library sound.</summary>
/// <param name="FilePath">Generated WAV file (D471 D7), or null.</param>
/// <param name="LibrarySoundId">Base library sound found by the description, or null.</param>
public sealed record InventionSound(string? FilePath, string? LibrarySoundId);

/// <summary>
/// Sound at the object (D471 D7, A1446): when an inhabitant uses an invented thing, it sounds like
/// itself. The order is the spec's: the sound the model made for this invention (file by name in the
/// service's sound folder), else a base library sound found by the rule guard's sound description, else
/// nothing special (the action's own sound plays). Presentation only.
/// </summary>
/// <param name="catalog">Runtime types of the world.</param>
/// <param name="objects">Objects of the world (target id to type).</param>
/// <param name="baseSounds">The base library, or null without it.</param>
/// <param name="soundFolder">Folder of made sounds, or null.</param>
/// <param name="fileExists">File check (tests pass a stand-in).</param>
public sealed class InventionSounds(IRuntimeCatalog catalog, WorldObjects objects, BaseSounds? baseSounds, string? soundFolder, Func<string, bool>? fileExists = null)
{
    private readonly Func<string, bool> _exists = fileExists ?? File.Exists;

    /// <summary>
    /// Folder of made sounds: <c>GW_AI_SOUND_OUTPUT_DIR</c> if set, else the service default
    /// <c>&lt;repository&gt;/.local/ai-service/sounds</c> (ai-service/config.toml, sound_output_dir).
    /// </summary>
    public static string DefaultFolder(string contentDirectory, string? environmentOverride)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentDirectory);
        return string.IsNullOrWhiteSpace(environmentOverride)
            ? Path.GetFullPath(Path.Combine(contentDirectory, "..", ".local", "ai-service", "sounds"))
            : Path.GetFullPath(environmentOverride);
    }

    /// <summary>The sound for an action on a target object, or null when the target is no invention or has no sound.</summary>
    public InventionSound? For(string? targetId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(objects);
        if (targetId is null || !objects.TryGet(targetId, out var target) || catalog.Invention(target.TypeId) is not { } invention)
        {
            return null;
        }

        if (invention.SoundFile is { } file && soundFolder is not null)
        {
            var path = Path.Combine(soundFolder, Path.GetFileName(file));
            if (_exists(path))
            {
                return new InventionSound(path, null);
            }
        }

        return baseSounds?.Fallback(invention.SoundDescription) is { } library ? new InventionSound(null, library) : null;
    }
}
