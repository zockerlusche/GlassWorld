using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>A1383 (B5-6 R16, B2608): ambient animals near the camera, the ones the sound map says live here and now.</summary>
public sealed class AmbientFaunaTests
{
    private static readonly FaunaAmbient Data = Read<FaunaAmbient>("fauna", "ambient.json");
    private static readonly SoundMap Sounds = Read<SoundMap>("sound", "sound_map.json");

    private static T Read<T>(params string[] path)
        where T : class =>
        ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine([TestPaths.Root, "content", .. path])));

    private static List<SoundForAnimal> Living(string season, string time) =>
        [.. Sounds.Animals.Where(a => a.Seasons.Contains(season) && a.TimesOfDay.Contains(time))];

    private static IReadOnlyList<AmbientCritter> At(string biome, string season, string time, double seconds = 12.5) =>
        AmbientFauna.At(Data, Living(season, time), 100, 100, seconds, 7, (_, _) => biome, (_, _) => 2.0);

    [Fact]
    public void EveryAnimalShownIsOneTheSoundMapKnows()
    {
        var heard = Sounds.Animals.Select(a => a.Animal).ToHashSet(StringComparer.Ordinal);
        Assert.All(Data.Animals, a => Assert.Contains(a.Animal, heard));
    }

    [Fact]
    public void TheMeadowInSpringShowsItsAnimalsTheBeachItsOwnAndNoMapNone()
    {
        var meadow = At("meadow", "spring", "day").Select(c => c.Animal).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("butterfly", meadow);
        Assert.Contains("bee", meadow);
        Assert.DoesNotContain("gull", meadow);
        Assert.DoesNotContain("firefly", meadow); // at night in summer only

        var beach = At("beach_sea", "spring", "day").Select(c => c.Animal).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("gull", beach);
        Assert.DoesNotContain("butterfly", beach);

        Assert.Contains("firefly", At("meadow", "summer", "night").Select(c => c.Animal));
        Assert.Empty(AmbientFauna.At(Data, Living("spring", "day"), 100, 100, 12.5, 7, (_, _) => null, (_, _) => 0));
    }

    [Fact]
    public void TheSameMomentGivesTheSamePictureNearTheCameraAndCalmChanges()
    {
        var once = At("meadow", "spring", "day");
        Assert.Equal(once, At("meadow", "spring", "day"));
        Assert.All(once, c =>
        {
            Assert.InRange(Math.Sqrt(((c.X - 100) * (c.X - 100)) + ((c.Z - 100) * (c.Z - 100))), 0, Data.RadiusM);
            Assert.InRange(c.Scale, 0, 1);
            Assert.True(c.Y >= 2.0 - 1e-9, $"{c.Animal} below the ground");
        });

        // A twentieth of a second later every animal is near where it was: nothing jumps.
        var later = At("meadow", "spring", "day", 12.55);
        var moved = once.Count(c => !later.Any(k => k.Animal == c.Animal && Math.Abs(k.X - c.X) < 2 && Math.Abs(k.Z - c.Z) < 2));
        Assert.True(moved <= once.Count / 10, $"{moved} of {once.Count} jumped");
    }
}
