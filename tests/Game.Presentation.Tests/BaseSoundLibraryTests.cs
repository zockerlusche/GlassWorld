using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.Audio;

namespace GlassWorld.Presentation.Tests;

/// <summary>
/// Base sound library (B8-4, B2530): origin and licence per file, ids reachable, loudness in the
/// target range. The file checks need the LFS files; without them (CI job without LFS) the game smoke
/// run checks the same (SOUNDLIB_OK).
/// </summary>
public sealed class BaseSoundLibraryTests
{
    private static readonly string Folder = Path.Combine(TestPaths.Root, "game", "assets", "sound", "base");

    private static readonly Contracts.BaseSoundLibrary Library =
        ContractSerializer.Deserialize<Contracts.BaseSoundLibrary>(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "sound", "library.json")));

    [Fact]
    public void DataIsComplete()
    {
        using var map = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "sound", "sound_map.json")));
        var mapped = map.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
            .SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetProperty("sound").GetString()!));
        Assert.Empty(BaseSoundCheck.DataProblems(Library, mapped));
    }

    [Fact]
    public void EveryFileIsInTheRepositoryAndListed()
    {
        var listed = Library.Sounds.SelectMany(s => s.Files).Select(f => f.File).ToHashSet(StringComparer.Ordinal);
        var present = Directory.GetFiles(Folder, "*.wav").Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(listed.Order(StringComparer.Ordinal), present.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FilesMeetTheTargets()
    {
        var first = File.ReadAllBytes(Path.Combine(Folder, Library.Sounds.First().Files.First().File));
        Assert.SkipWhen(BaseSoundCheck.IsLfsPointer(first), "LFS files not checked out; the game smoke run checks them (SOUNDLIB_OK).");
        Assert.Empty(BaseSoundCheck.FileProblems(Library, name => File.Exists(Path.Combine(Folder, name)) ? File.ReadAllBytes(Path.Combine(Folder, name)) : null));
    }

    [Fact]
    public void LoudnessOfAKnownSignal()
    {
        // A full-scale sine has an RMS of -3.01 dBFS; half amplitude -9.03 dBFS.
        var samples = Enumerable.Range(0, 48000).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 48000))).ToArray();
        var sound = new MonoSound(samples, 48000);
        Assert.InRange(Pcm.LoudnessDbfs(sound, -50), -9.1, -8.9);
        Assert.InRange(Pcm.PeakDbfs(sound), -6.1, -5.9);
        var back = Pcm.ReadWav(Pcm.WriteWav16(sound));
        Assert.InRange(Pcm.LoudnessDbfs(back, -50), -9.1, -8.9);
    }
}
