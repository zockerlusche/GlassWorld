using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlassWorld.Presentation.Audio;

namespace GlassWorld.Presentation.Tests;

/// <summary>B8 R2/R3/R4: every action, weather and animal sounds; the retro fallback is deterministic.</summary>
public sealed class AudioTests
{
    /// <summary>Atmosphere and farm animals of decision B2481.</summary>
    private static readonly string[] AnimalList =
        ["gull", "crab", "butterfly", "bee", "songbird", "squirrel", "dragonfly", "frog", "firefly", "owl", "fish", "wild_chicken", "goat"];

    /// <summary>
    /// Animals the spec keeps silent near the camera. None at present: the firefly was silent after Marcel
    /// rejected its tone (W1645) and has a chosen sound again since listening test 3 (W1664).
    /// </summary>
    private static readonly string[] SilentBySpec = [];

    private static readonly string Root = TestPaths.Root;
    private static readonly string MapJson = File.ReadAllText(Path.Combine(Root, "content", "sound", "sound_map.json"));
    private static readonly string RecipesJson = File.ReadAllText(Path.Combine(Root, "content", "sound", "synth_recipes.json"));
    private static readonly Lazy<SoundLibrary> Library = new(() => SoundLibrary.FromJson(MapJson, RecipesJson));

    [Fact]
    public void EverySoundHasTheSameLoudnessTarget()
    {
        // Marcel W1645: one integrated target for all sounds (data), peak only as the limit.
        double Read(params string[] path)
        {
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine([Root, "content", .. path[..^1]])))!;
            foreach (var key in path[^1].Split('.'))
            {
                node = node![key]!;
            }

            return (double)node!;
        }

        var target = Read("sound", "library.json", "target.one_shot_dbfs");
        Assert.Equal(target, Read("sound", "library.json", "target.loop_dbfs"));
        Assert.Equal(target, Read("voice", "murmur.json", "loudness_dbfs"));
        Assert.Equal(target, Read("music", "music.json", "loudness_dbfs"));
        Assert.Equal(target, Read("sound", "animals.json", "birdsong.loudness_dbfs"));
    }

    [Fact]
    public void EveryActionOfTheVocabularyHasASound()
    {
        // Every action has an entry; null means silent on purpose (waiting and sleeping, Marcel W1643).
        var mapped = Library.Value.Map.Actions.Select(a => a.Action).ToHashSet(StringComparer.Ordinal);
        var missing = VocabularyIds("actions.json").Where(id => !mapped.Contains(id)).ToList();
        Assert.True(missing.Count == 0, "actions without an entry: " + string.Join(", ", missing));
        Assert.Null(Library.Value.ForAction("idle"));
        Assert.Null(Library.Value.ForAction("sleep"));
        Assert.NotNull(Library.Value.ForAction("drink"));
    }

    [Fact]
    public void EveryWeatherHasASound()
    {
        var mapped = Library.Value.Map.Weather.Select(w => w.Weather).ToHashSet(StringComparer.Ordinal);
        var missing = VocabularyIds("weather.json").Where(id => !mapped.Contains(id)).ToList();
        Assert.True(missing.Count == 0, "weather without sound: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryAnimalOfTheAnimalListHasASound()
    {
        var mapped = Library.Value.Map.Animals.Select(a => a.Animal).ToHashSet(StringComparer.Ordinal);
        var missing = AnimalList.Where(a => !mapped.Contains(a)).ToList();
        Assert.True(missing.Count == 0, "animals without sound: " + string.Join(", ", missing));
    }

    [Fact]
    public void EverySoundHasARetroRecipe() => Assert.Empty(Library.Value.MissingRecipes());

    [Fact]
    public void MissingRecipeIsRejected()
    {
        var map = JsonNode.Parse(MapJson)!;
        map["actions"]![0]!["sound"] = "action.does_not_exist";
        var ex = Assert.Throws<InvalidDataException>(() => SoundLibrary.FromJson(map.ToJsonString(), RecipesJson));
        Assert.Contains("action.does_not_exist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SeasonsAndTimesComeFromTheData()
    {
        var seasons = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "content", "world", "time.json")))!["seasons"]!.AsArray().Select(s => (string)s!["id"]!).ToHashSet(StringComparer.Ordinal);
        var phases = VocabularyIds("time_of_day.json").ToHashSet(StringComparer.Ordinal);
        Assert.All(Library.Value.Map.Animals, a =>
        {
            Assert.All(a.Seasons, s => Assert.Contains(s, seasons));
            Assert.All(a.TimesOfDay, t => Assert.Contains(t, phases));
        });
    }

    [Fact]
    public void NoSummerInsectsInWinterAndOwlsAtNight()
    {
        // A1350 criterion 2: in no biome and at no time of day of the winter (data test).
        foreach (var biome in WorldBiomes())
        {
            foreach (var phase in VocabularyIds("time_of_day.json"))
            {
                var winter = Library.Value.AudibleAnimals(biome, "winter", phase).Select(a => a.Animal).ToList();
                Assert.DoesNotContain("bee", winter);
                Assert.DoesNotContain("butterfly", winter);
                Assert.DoesNotContain("dragonfly", winter);
            }
        }

        Assert.Contains("owl", Library.Value.AudibleAnimals("forest", "winter", "night").Select(a => a.Animal));
    }

    [Fact]
    public void SoundBiomesAreTheWorldsBiomesAndEachHasAnAmbience()
    {
        // The sound map once used ids the world does not have (beach, stream, rocks): nothing ever matched.
        var world = WorldBiomes().ToHashSet(StringComparer.Ordinal);
        var used = Library.Value.Map.Animals.SelectMany(a => a.Biomes).Concat(Library.Value.Map.Ambience.Select(a => a.Biome)).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(used.Except(world));
        Assert.Empty(world.Except(Library.Value.Map.Ambience.Select(a => a.Biome)));
    }

    [Fact]
    public void EveryAnimalCanBeHeardSomewhere()
    {
        // A1350 criterion 1: every animal of B2481 has a time and place in the world and a sound that is not silent.
        var seasons = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "content", "world", "time.json")))!["seasons"]!.AsArray().Select(s => (string)s!["id"]!).ToList();
        foreach (var animal in AnimalList.Where(a => !SilentBySpec.Contains(a)))
        {
            var heard = WorldBiomes().Any(b => seasons.Any(s => VocabularyIds("time_of_day.json").Any(t => Library.Value.AudibleAnimals(b, s, t).Any(a => a.Animal == animal))));
            Assert.True(heard, animal + " is never audible");
            var sound = Library.Value.Map.Animals.First(a => a.Animal == animal).Sound;
            var pcm = Library.Value.Render(sound, 0, 1);
            Assert.True(pcm.Max(x => Math.Abs((int)x)) > 1000, sound + " is silent");
        }
    }

    private static IEnumerable<string> WorldBiomes() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "content", "world", "biomes.json")))!["biomes"]!.AsArray().Select(b => (string)b!["id"]!);

    [Fact]
    public void SameSeedGivesTheSameSamples()
    {
        var a = Hash(Library.Value.Recipes.Recipes.Select(r => RetroSynth.Render(r, Library.Value.SampleRate, Library.Value.Recipes.Peak, 42)));
        var b = Hash(Library.Value.Recipes.Recipes.Select(r => RetroSynth.Render(r, Library.Value.SampleRate, Library.Value.Recipes.Peak, 42)));
        Assert.Equal(a, b);
    }

    [Fact]
    public void VariantsDifferButStayRecognisable()
    {
        var recipe = Library.Value.Recipe("animal.songbird.song");
        var one = RetroSynth.Render(recipe, Library.Value.SampleRate, Library.Value.Recipes.Peak, 1);
        var two = RetroSynth.Render(recipe, Library.Value.SampleRate, Library.Value.Recipes.Peak, 2);
        Assert.NotEqual(Hash([one]), Hash([two]));
        Assert.Equal(one.Length, two.Length);
    }

    [Fact]
    public void EveryRecipeRendersAudibleAndWithinThePeak()
    {
        var limit = (int)Math.Ceiling(Library.Value.Recipes.Peak * short.MaxValue) + 1;
        Assert.All(Library.Value.Recipes.Recipes, r =>
        {
            var pcm = RetroSynth.Render(r, Library.Value.SampleRate, Library.Value.Recipes.Peak, 7);
            Assert.Equal((int)Math.Round(r.DurationS * Library.Value.SampleRate), pcm.Length);
            var peak = pcm.Max(s => Math.Abs((int)s));
            Assert.InRange(peak, short.MaxValue / 10, limit);
        });
    }

    [Fact]
    public void CacheIsBounded()
    {
        var library = SoundLibrary.FromJson(MapJson, RecipesJson, cacheLimit: 3);
        for (var v = 0; v < 10; v++)
        {
            library.Render("step.grass", v, 1);
        }

        Assert.Equal(3, library.CachedCount);
        Assert.Same(library.Render("step.grass", 9, 1), library.Render("step.grass", 9, 1));
    }

    [Fact]
    public void CacheSeparatesWorlds()
    {
        var library = SoundLibrary.FromJson(MapJson, RecipesJson);
        var worldOne = library.Render("animal.songbird.song", 0, 1);
        var worldTwo = library.Render("animal.songbird.song", 0, 2);
        Assert.NotEqual(Hash([worldOne]), Hash([worldTwo]));
        Assert.Same(worldOne, library.Render("animal.songbird.song", 0, 1));
    }

    [Fact]
    public void SeedOfIsStable() => Assert.Equal(0xcbf29ce484222325UL ^ 0, SplitMix64.SeedOf(string.Empty));

    private static string Hash(IEnumerable<short[]> buffers)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var buffer in buffers)
        {
            sha.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.AsSpan()));
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static IEnumerable<string> VocabularyIds(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "content", "vocabulary", file)));
        return [.. doc.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("id").GetString()!)];
    }
}
