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
        // BS.1770: a half-amplitude sine is -9.03 dB in power; K-weighting at 440 Hz adds about 0 dB and the
        // -0.691 offset (which balances the +0.69 dB at 1 kHz) makes it about -9.72 LUFS.
        var samples = Enumerable.Range(0, 48000).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 48000))).ToArray();
        var sound = new MonoSound(samples, 48000);
        Assert.InRange(Pcm.Loudness(sound, -50), -9.8, -9.6);
        Assert.InRange(Pcm.PeakDbfs(sound), -6.1, -5.9);
        var back = Pcm.ReadWav(Pcm.WriteWav16(sound));
        Assert.InRange(Pcm.Loudness(back, -50), -9.8, -9.6);
    }
}

/// <summary>Runtime view of the library: variants and the description fallback (D471 D7).</summary>
public sealed class BaseSoundsTests
{
    private static readonly BaseSounds Sounds = new(Contracts.Validation.ContractSerializer.Deserialize<Contracts.BaseSoundLibrary>(
        File.ReadAllText(Path.Combine(TestPaths.Root, "content", "sound", "library.json"))));

    [Theory]
    [InlineData("wooden door creaks", "object.creak")]
    [InlineData("crackles like a small fire", "object.fire")]
    [InlineData("water trickles over stones", "ambience.stream")]
    [InlineData("stone clacks when struck", "object.knock_stone")]
    [InlineData("Chop! The axe splits wood.", "object.chop_wood")]
    [InlineData("crackling fire", "object.fire")]
    [InlineData("door creaking", "object.creak")]
    [InlineData("chopping wood", "object.chop_wood")]
    [InlineData("footsteps on sand", "step.sand")]
    [InlineData("splashing in shallow water", "step.water")]
    [InlineData("a bird singing", "animal.songbird.song")]
    [InlineData("leaves rustling in the wind", "ambience.leaves")]
    [InlineData("hums quietly", null)]
    [InlineData("a wooden bowl", null)]
    [InlineData("glass clinks", null)]
    [InlineData("water", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void DescriptionFallsBackToTheBestBaseSound(string? description, string? expected) => Assert.Equal(expected, Sounds.Fallback(description));

    [Theory]
    [InlineData("crackling", "crackle")]
    [InlineData("crackles", "crackle")]
    [InlineData("chopping", "chop")]
    [InlineData("creaking", "creaks")]
    [InlineData("footsteps", "footstep")]
    [InlineData("splashes", "splash")]
    [InlineData("tapping", "tap")]
    public void StemsMeet(string a, string b) => Assert.Equal(BaseSounds.Stem(b), BaseSounds.Stem(a));

    [Fact]
    public void VariantsWrapAroundAndUnknownIdsHaveNoFile()
    {
        var count = Sounds.Variants["step.grass"];
        Assert.Equal(Sounds.File("step.grass", 0), Sounds.File("step.grass", count));
        Assert.Equal(Sounds.File("step.grass", count - 1), Sounds.File("step.grass", -1));
        Assert.Null(Sounds.File("weather.fog", 0));
        Assert.True(Sounds.IsLoop("ambience.waves"));
        Assert.False(Sounds.IsLoop("step.grass"));
    }
}

/// <summary>Retro synth and library files play at the same level (GW1 review #62 K1).</summary>
public sealed class SynthLoudnessTests
{
    private static string Read(string file) => File.ReadAllText(Path.Combine(TestPaths.Root, "content", "sound", file));

    private static readonly Contracts.LibraryTarget Target =
        Contracts.Validation.ContractSerializer.Deserialize<Contracts.BaseSoundLibrary>(Read("library.json")).Target;

    [Fact]
    public void SynthSoundsMeetTheLibraryTarget()
    {
        var plain = SoundLibrary.FromJson(Read("sound_map.json"), Read("synth_recipes.json"));
        var normalised = SoundLibrary.FromJson(Read("sound_map.json"), Read("synth_recipes.json"), loudness: Target);
        var offBefore = 0;
        foreach (var id in normalised.UsedSoundIds())
        {
            var wanted = normalised.Recipe(id).Bus == Contracts.SynthRecipeBus.Ambience ? Target.LoopDbfs : Target.OneShotDbfs;
            if (Math.Abs(plain.LoudnessOf(plain.Render(id, 0, 1)) - wanted) > Target.ToleranceDb)
            {
                offBefore++;
            }

            var pcm = normalised.Render(id, 0, 1);
            var loudness = normalised.LoudnessOf(pcm);
            var peak = 20 * Math.Log10(Math.Max(1, pcm.Max(s => Math.Abs((int)s))) / 32768.0);
            Assert.True(
                loudness <= wanted + Target.ToleranceDb && (loudness >= wanted - Target.ToleranceDb || Math.Abs(peak - Target.PeakMaxDbfs) <= 0.2),
                $"{id}: {loudness:0.0} dBFS (peak {peak:0.0}), target {wanted:0.0}");
        }

        Assert.True(offBefore > 0, "without normalising, some synth sounds are off target (the reason for K1)");
    }
}

/// <summary>Sound generation settings are consistent (GW2 review #77).</summary>
public sealed class SoundGenerationDataTests
{
    [Fact]
    public void DefaultStepsAreNotBelowTheMinimum()
    {
        var data = Contracts.Validation.ContractSerializer.Deserialize<Contracts.SoundGeneration>(
            File.ReadAllText(Path.Combine(TestPaths.Root, "content", "sound", "generation.json")));
        Assert.True(data.DefaultSteps >= data.MinSteps, $"default_steps {data.DefaultSteps} below min_steps {data.MinSteps} (D440: silence)");
        Assert.True(1 + data.SeedRetries <= 10, "attempts of a sound result are at most 10");
    }
}
