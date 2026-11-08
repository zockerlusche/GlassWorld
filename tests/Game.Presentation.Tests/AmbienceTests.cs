using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.Audio;

namespace GlassWorld.Presentation.Tests;

/// <summary>Biome ambience (B8 R7): the loop follows the biome, calm at borders.</summary>
public sealed class AmbienceTests
{
    private static readonly string Sound = Path.Combine(TestPaths.Root, "content", "sound");

    private static readonly SoundLibrary Library = SoundLibrary.FromJson(File.ReadAllText(Path.Combine(Sound, "sound_map.json")), File.ReadAllText(Path.Combine(Sound, "synth_recipes.json")));

    private static readonly MixAmbience Settings = ContractSerializer.Deserialize<SoundMix>(File.ReadAllText(Path.Combine(Sound, "mix.json"))).Ambience;

    [Fact]
    public void TheFirstLoopStartsAtOnceAChangeWaitsForTheHoldThenCrossFades()
    {
        var director = new AmbienceDirector(Library, Settings);
        Assert.Equal(new AmbienceStep("ambience.waves", Settings.FadeS), director.Update(0, "beach_sea"));
        Assert.Null(director.Update(1, "beach_sea").Start);

        var forest = director.SoundFor("forest");
        Assert.Null(director.Update(2, "forest").Start);
        Assert.Null(director.Update(2 + (Settings.HoldS / 2), "forest").Start);
        Assert.Equal(new AmbienceStep(forest, Settings.FadeS), director.Update(2 + Settings.HoldS, "forest"));
        Assert.Equal(forest, director.Current);
    }

    [Fact]
    public void WalkingAlongABorderDoesNotFlicker()
    {
        var director = new AmbienceDirector(Library, Settings);
        director.Update(0, "beach_sea");
        var changes = 0;
        for (var t = 1.0; t < 60; t += 1)
        {
            if (director.Update(t, t % 2 == 0 ? "forest" : "beach_sea").Start is not null)
            {
                changes++;
            }
        }

        Assert.Equal(0, changes);
        Assert.Equal("ambience.waves", director.Current);
    }

    [Fact]
    public void EveryWorldBiomeHasALoopAndNoBiomeMeansSilence()
    {
        var director = new AmbienceDirector(Library, Settings);
        var biomes = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "world", "biomes.json")))!["biomes"]!.AsArray().Select(b => (string)b!["id"]!);
        Assert.All(biomes, b => Assert.NotNull(director.SoundFor(b)));
        director.Update(0, "forest");
        Assert.Equal(new AmbienceStep(null, 0), director.Update(10, null));
        Assert.Equal(new AmbienceStep(null, Settings.FadeS), director.Update(10 + Settings.HoldS, null));
        Assert.Null(director.Current);
    }
}
