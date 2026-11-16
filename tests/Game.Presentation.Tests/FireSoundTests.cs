using GlassWorld.Presentation.Audio;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1543 (D1237): a fire crackles as loud as it burns and as near as it is; embers quietly.</summary>
public sealed class FireSoundTests
{
    [Fact]
    public void AFireCracklesAsLoudAsItBurnsAndAsNearAsItIs()
    {
        Assert.Equal(1.0, FireSound.Gain(1, 1000, false, 0.35), 9);
        Assert.Equal(0.5, FireSound.Gain(1, 500, false, 0.35), 9);
        Assert.Equal(0.25, FireSound.Gain(0.5, 500, false, 0.35), 9);
        Assert.Equal(0.35, FireSound.Gain(1, 150, true, 0.35), 9);
        Assert.Equal(0.0, FireSound.Gain(0, 1000, false, 0.35), 9);
    }

    [Fact]
    public void TheBurningSoundIsALoopOfTheLibrary()
    {
        var root = Path.Combine(TestPaths.Root, "content", "sound");
        var map = GlassWorld.Contracts.Validation.ContractSerializer.Deserialize<GlassWorld.Contracts.SoundMap>(File.ReadAllText(Path.Combine(root, "sound_map.json")));
        var library = GlassWorld.Contracts.Validation.ContractSerializer.Deserialize<GlassWorld.Contracts.BaseSoundLibrary>(File.ReadAllText(Path.Combine(root, "library.json")));
        var burning = map.Burning;
        Assert.NotNull(burning);
        Assert.Contains(library.Sounds, s => s.Id == burning.Sound && s.Kind.ToString().Equals("loop", StringComparison.OrdinalIgnoreCase));
    }
}
