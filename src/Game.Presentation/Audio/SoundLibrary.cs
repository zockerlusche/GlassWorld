using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;

namespace GlassWorld.Presentation.Audio;

/// <summary>
/// Resolves what sounds for an event (B8 R2) and renders the retro fallback on demand with a bounded
/// cache (B8 R3). Sound is presentation only; nothing here changes the simulation.
/// </summary>
public sealed class SoundLibrary
{
    private readonly Dictionary<string, SynthRecipe> _recipes;
    private readonly Dictionary<(string Id, int Variant, ulong WorldSeed), short[]> _cache = [];
    private readonly Queue<(string Id, int Variant, ulong WorldSeed)> _cacheOrder = new();
    private readonly int _cacheLimit;

    private SoundLibrary(SoundMap map, SynthRecipes recipes, int cacheLimit)
    {
        Map = map;
        Recipes = recipes;
        _recipes = recipes.Recipes.ToDictionary(r => r.Id, StringComparer.Ordinal);
        _cacheLimit = cacheLimit;
    }

    /// <summary>The sound map.</summary>
    public SoundMap Map { get; }

    /// <summary>The recipe file.</summary>
    public SynthRecipes Recipes { get; }

    /// <summary>Sample rate of all rendered sounds.</summary>
    public int SampleRate => (int)Recipes.SampleRate;

    /// <summary>Parses and validates both files.</summary>
    public static SoundLibrary FromJson(string soundMapJson, string recipesJson, int cacheLimit = 256)
    {
        var library = new SoundLibrary(ContractSerializer.Deserialize<SoundMap>(soundMapJson), ContractSerializer.Deserialize<SynthRecipes>(recipesJson), cacheLimit);
        var problems = library.MissingRecipes();
        return problems.Count == 0 ? library : throw new InvalidDataException("sounds without recipe: " + string.Join(", ", problems));
    }

    /// <summary>Sound id for an action, or null if the action is silent by data.</summary>
    public string? ForAction(string action) => Map.Actions.FirstOrDefault(a => a.Action == action)?.Sound;

    /// <summary>Animals that can be heard in a biome, season and time of day (B2481 data table).</summary>
    public IReadOnlyList<SoundForAnimal> AudibleAnimals(string biome, string season, string timeOfDay) =>
        [.. Map.Animals.Where(a => a.Biomes.Contains(biome) && a.Seasons.Contains(season) && a.TimesOfDay.Contains(timeOfDay))];

    /// <summary>Every sound id the map uses.</summary>
    public IEnumerable<string> UsedSoundIds() =>
        Map.Actions.Select(a => a.Sound)
            .Concat(Map.Surfaces.Select(s => s.Sound))
            .Concat(Map.Weather.Select(w => w.Sound))
            .Concat(Map.Ambience.Select(a => a.Sound))
            .Concat(Map.Animals.Select(a => a.Sound))
            .Distinct(StringComparer.Ordinal);

    /// <summary>Sound ids used by the map that have no retro recipe (must be empty: every sound has a fallback).</summary>
    public IReadOnlyList<string> MissingRecipes() => [.. UsedSoundIds().Where(id => !_recipes.ContainsKey(id)).Order(StringComparer.Ordinal)];

    /// <summary>The recipe of a sound.</summary>
    public SynthRecipe Recipe(string soundId) => _recipes[soundId];

    /// <summary>Rendered PCM of one variant, cached; the seed depends on world seed, sound id and variant.</summary>
    public short[] Render(string soundId, int variant, ulong worldSeed)
    {
        if (_cache.TryGetValue((soundId, variant, worldSeed), out var cached))
        {
            return cached;
        }

        var seed = SplitMix64.SeedOf($"{worldSeed}/{soundId}/{variant}");
        var pcm = RetroSynth.Render(_recipes[soundId], SampleRate, Recipes.Peak, seed);
        _cache[(soundId, variant, worldSeed)] = pcm;
        _cacheOrder.Enqueue((soundId, variant, worldSeed));
        while (_cacheOrder.Count > _cacheLimit)
        {
            _cache.Remove(_cacheOrder.Dequeue());
        }

        return pcm;
    }

    /// <summary>Number of rendered sounds kept.</summary>
    public int CachedCount => _cache.Count;
}
