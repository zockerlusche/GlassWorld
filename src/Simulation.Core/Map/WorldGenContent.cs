using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Simulation.Map;

/// <summary>
/// The data the world generator and the weather read (B2 R1–R15, R33): presets, biome table, spawns
/// with the mandatory start-island check, and climates. Each file is checked against its schema on
/// load (ContractSerializer); this class checks what a schema cannot: references between files and
/// to the vocabularies, the object catalogue and the calendar. A broken reference fails at load with
/// all problems listed, never later in the generator.
/// </summary>
public sealed class WorldGenContent
{
    private WorldGenContent(
        IReadOnlyDictionary<string, WorldPreset> presets,
        BiomeTable biomes,
        SpawnTable spawns,
        IReadOnlyDictionary<string, ClimateSettings> climates)
    {
        Presets = presets;
        Biomes = biomes;
        Spawns = spawns;
        Climates = climates;
    }

    /// <summary>Presets by id (content/world/presets).</summary>
    public IReadOnlyDictionary<string, WorldPreset> Presets { get; }

    /// <summary>Biome table (content/world/biomes.json).</summary>
    public BiomeTable Biomes { get; }

    /// <summary>Spawns and mandatory resources (content/world/spawns.json).</summary>
    public SpawnTable Spawns { get; }

    /// <summary>Climates by id (content/climate).</summary>
    public IReadOnlyDictionary<string, ClimateSettings> Climates { get; }

    /// <summary>Biome ids in table order (the index a <see cref="MapCell"/> stores).</summary>
    public IReadOnlyList<string> BiomeIds => [.. Biomes.Biomes.Select(b => b.Id)];

    /// <summary>Loads and cross-checks the world data of a content folder.</summary>
    public static WorldGenContent Load(
        string contentDirectory,
        IReadOnlyDictionary<VocabularyKind, Vocabulary> vocabularies,
        ObjectCatalog objects,
        WorldCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(vocabularies);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(calendar);
        T Read<T>(string path)
            where T : class => ContractSerializer.Deserialize<T>(File.ReadAllText(path));
        string[] Files(string folder) =>
            [.. Directory.GetFiles(Path.Combine(contentDirectory, folder), "*.json").Order(StringComparer.Ordinal)];

        var presets = Files(Path.Combine("world", "presets")).Select(Read<WorldPreset>).ToList();
        var biomes = Read<BiomeTable>(Path.Combine(contentDirectory, "world", "biomes.json"));
        var spawns = Read<SpawnTable>(Path.Combine(contentDirectory, "world", "spawns.json"));
        var climates = Files("climate").Select(Read<ClimateSettings>).ToList();

        var problems = new List<string>();
        var biomeVocabulary = Ids(vocabularies, VocabularyKind.Biome);
        var weatherVocabulary = Ids(vocabularies, VocabularyKind.Weather);
        CheckBiomes(biomes, biomeVocabulary, problems);
        CheckSpawns(spawns, biomes, objects, problems);
        foreach (var climate in climates)
        {
            CheckClimate(climate, weatherVocabulary, calendar, problems);
        }

        var biomeIds = biomes.Biomes.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var preset in presets)
        {
            CheckPreset(preset, problems);
            if (!climates.Any(c => c.Id == preset.Climate.ClimateId))
            {
                problems.Add($"preset '{preset.Id}': unknown climate '{preset.Climate.ClimateId}'");
            }

            var p = $"preset '{preset.Id}'";
            var islandTypes = spawns.Spawns.Select(s => s.ObjectType).Distinct(StringComparer.Ordinal).Where(objects.Contains).ToList();
            foreach (var resource in preset.Start.Favor.Resources)
            {
                var unknown = resource.PropertiesAll.Concat(resource.PropertiesNone).Where(pr => !objects.PropertyVocabulary.Contains(pr)).ToList();
                if (unknown.Count > 0)
                {
                    problems.Add($"{p}: start favour '{resource.Id}' uses unknown properties {string.Join(", ", unknown)}");
                }
                else if (!islandTypes.Any(type => resource.PropertiesAll.All(pr => objects.HasProperty(type, pr)) && !resource.PropertiesNone.Any(pr => objects.HasProperty(type, pr))))
                {
                    problems.Add($"{p}: start favour '{resource.Id}' matches no spawn type of the island");
                }
            }

            if (!objects.Contains(preset.Caves.ObjectType))
            {
                problems.Add($"{p}: cave object type '{preset.Caves.ObjectType}' not in the catalogue");
            }

            foreach (var (what, biome) in new[] { ("cave cliff", preset.Caves.CliffBiome), ("start", preset.Start.Biome) })
            {
                if (!biomeIds.Contains(biome))
                {
                    problems.Add($"{p}: {what} biome '{biome}' not in the biome list");
                }
            }
        }

        Duplicates(presets.Select(p => p.Id), "preset", problems);
        Duplicates(climates.Select(c => c.Id), "climate", problems);
        if (presets.Count == 0)
        {
            problems.Add("no world preset in content/world/presets");
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException("World data: " + string.Join("; ", problems));
        }

        return new WorldGenContent(
            presets.ToDictionary(p => p.Id, StringComparer.Ordinal),
            biomes,
            spawns,
            climates.ToDictionary(c => c.Id, StringComparer.Ordinal));
    }

    private static HashSet<string> Ids(IReadOnlyDictionary<VocabularyKind, Vocabulary> vocabularies, VocabularyKind kind) =>
        vocabularies.TryGetValue(kind, out var vocabulary)
            ? vocabulary.Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal)
            : throw new InvalidDataException($"vocabulary kind '{kind}' missing");

    private static void Duplicates(IEnumerable<string> ids, string what, List<string> problems)
    {
        foreach (var id in ids.GroupBy(i => i, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
        {
            problems.Add($"{what} '{id}' defined twice");
        }
    }

    private static void Range(string where, int min, int max, List<string> problems)
    {
        if (min > max)
        {
            problems.Add($"{where}: min {min} > max {max}");
        }
    }

    private static void CheckBiomes(BiomeTable table, HashSet<string> vocabulary, List<string> problems)
    {
        var defined = table.Biomes.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        Duplicates(table.Biomes.Select(b => b.Id), "biome", problems);
        problems.AddRange(defined.Where(id => !vocabulary.Contains(id)).Select(id => $"biome '{id}' not in vocabulary 'biome'"));
        if (!defined.Contains(table.FallbackBiome))
        {
            problems.Add($"fallback biome '{table.FallbackBiome}' not in the biome list");
        }

        foreach (var rule in table.Rules)
        {
            if (!defined.Contains(rule.Biome))
            {
                problems.Add($"biome rule names unknown biome '{rule.Biome}'");
            }

            Range($"biome rule '{rule.Biome}' height", rule.MinHeight, rule.MaxHeight, problems);
            Range($"biome rule '{rule.Biome}' moisture", rule.MinMoisturePermille, rule.MaxMoisturePermille, problems);
            Range($"biome rule '{rule.Biome}' temperature", rule.MinTemperatureTenthC, rule.MaxTemperatureTenthC, problems);
        }
    }

    private static void CheckSpawns(SpawnTable spawns, BiomeTable biomes, ObjectCatalog objects, List<string> problems)
    {
        var defined = biomes.Biomes.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);

        // Regrowth belongs to the type: all rules of one type must agree (or leave it out).
        foreach (var type in spawns.Spawns.GroupBy(r => r.ObjectType, StringComparer.Ordinal))
        {
            if (type.Select(r => r.RegrowHours).Distinct().Count() > 1)
            {
                problems.Add($"spawn rules of '{type.Key}' disagree on regrow_hours");
            }
        }

        foreach (var rule in spawns.Spawns)
        {
            if (!defined.Contains(rule.Biome))
            {
                problems.Add($"spawn rule names unknown biome '{rule.Biome}'");
            }

            if (!objects.Contains(rule.ObjectType))
            {
                problems.Add($"spawn rule names unknown object type '{rule.ObjectType}'");
            }

            Range($"spawn {rule.Biome}/{rule.ObjectType} cluster", rule.Cluster.Min, rule.Cluster.Max, problems);
        }

        Duplicates(spawns.Mandatory.Select(m => m.Id), "mandatory resource", problems);
        foreach (var m in spawns.Mandatory)
        {
            Range($"mandatory '{m.Id}'", m.Min, m.Max, problems);
            var properties = m.PropertiesAll.Concat(m.PropertiesNone).ToList();
            problems.AddRange(properties.Where(p => !objects.PropertyVocabulary.Contains(p)).Select(p => $"mandatory '{m.Id}' names unknown property '{p}'"));
            if (m.Source == MandatoryResourceSource.Fresh_water && properties.Count > 0)
            {
                problems.Add($"mandatory '{m.Id}' is fresh water and must not name properties");
            }

            if (m.Source == MandatoryResourceSource.Object && m.PropertiesAll.Count == 0)
            {
                problems.Add($"mandatory '{m.Id}' needs at least one property");
            }

            // A category no catalogue type can satisfy would fail every island; say so at load.
            if (m.Source == MandatoryResourceSource.Object
                && properties.All(objects.PropertyVocabulary.Contains)
                && !objects.TypeIds.Any(t => m.PropertiesAll.All(p => objects.HasProperty(t, p)) && !m.PropertiesNone.Any(p => objects.HasProperty(t, p))))
            {
                problems.Add($"mandatory '{m.Id}': no object type in the catalogue has these properties");
            }
        }
    }

    private static void CheckClimate(ClimateSettings climate, HashSet<string> weatherVocabulary, WorldCalendar calendar, List<string> problems)
    {
        var weather = climate.Weather;
        var states = weather.States.Select(s => s.Weather).ToHashSet(StringComparer.Ordinal);
        Duplicates(weather.States.Select(s => s.Weather), $"climate '{climate.Id}' weather state", problems);
        problems.AddRange(states.Where(s => !weatherVocabulary.Contains(s)).Select(s => $"climate '{climate.Id}': weather '{s}' not in vocabulary 'weather'"));
        var temperature = weather.Temperature;
        problems.AddRange(calendar.Settings.Seasons.Select(s => s.Id).Where(s => !temperature.Seasons.Any(t => t.Season == s)).Select(s => $"climate '{climate.Id}': no temperature offset for season '{s}'"));
        problems.AddRange(states.Where(s => !temperature.Weathers.Any(t => t.Weather == s)).Select(s => $"climate '{climate.Id}': no temperature offset for weather '{s}'"));
        problems.AddRange(Enum.GetNames<Time.DayPhase>().Select(p => p.ToLowerInvariant()).Where(p => !temperature.TimesOfDay.Any(t => t.TimeOfDay == p)).Select(p => $"climate '{climate.Id}': no temperature offset for time of day '{p}'"));
        foreach (var (what, id) in new[] { ("snow", weather.SnowWeather), ("drought", weather.DroughtWeather) })
        {
            if (!states.Contains(id))
            {
                problems.Add($"climate '{climate.Id}': {what} weather '{id}' has no state values");
            }
        }

        var seasons = calendar.Settings.Seasons.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var covered = weather.Seasons.Select(s => s.Season).ToList();
        Duplicates(covered, $"climate '{climate.Id}' season", problems);
        problems.AddRange(seasons.Where(s => !covered.Contains(s)).Select(s => $"climate '{climate.Id}': no weather table for season '{s}'"));
        problems.AddRange(covered.Where(s => !seasons.Contains(s)).Select(s => $"climate '{climate.Id}': unknown season '{s}'"));
        foreach (var season in weather.Seasons)
        {
            Duplicates(season.Transitions.Select(t => t.FromWeather), $"climate '{climate.Id}' {season.Season} transition from", problems);
            foreach (var t in season.Transitions)
            {
                foreach (var id in t.To.Select(x => x.Weather).Prepend(t.FromWeather).Where(id => !states.Contains(id)))
                {
                    problems.Add($"climate '{climate.Id}' {season.Season}: weather '{id}' has no state values");
                }
            }
        }
    }

    private static void CheckPreset(WorldPreset preset, List<string> problems)
    {
        var p = $"preset '{preset.Id}'";
        var grid = preset.Grid;
        if (grid.WidthCells % grid.ChunkCells != 0 || grid.DepthCells % grid.ChunkCells != 0)
        {
            problems.Add($"{p}: width and depth must be multiples of chunk_cells");
        }

        if (preset.Terrain.SeaLevel >= preset.Terrain.MaxHeight)
        {
            problems.Add($"{p}: sea level must lie below max height");
        }

        Range($"{p} rocks", preset.Terrain.Rocks.Min, preset.Terrain.Rocks.Max, problems);
        Range($"{p} springs", preset.Water.Springs.Min, preset.Water.Springs.Max, problems);
        Range($"{p} caves", preset.Caves.Min, preset.Caves.Max, problems);
        Range($"{p} neighbour islands", preset.Neighbours.Islands.Min, preset.Neighbours.Islands.Max, problems);
        Range($"{p} neighbour group size", preset.Neighbours.GroupSize.Min, preset.Neighbours.GroupSize.Max, problems);
        Range($"{p} pair distance", preset.Start.MinPairDistanceCells, preset.Start.MaxPairDistanceCells, problems);
        if (preset.Start.MinPairDistanceCells <= preset.Exploration.ViewRangeCells)
        {
            // R9: the start pairs must not see each other.
            problems.Add($"{p}: min_pair_distance_cells must exceed view_range_cells");
        }

        var curve = preset.Tides.Curve.ToList();
        if (curve[0].PhasePermille != 0 || curve[^1].PhasePermille != 1000
            || curve.Zip(curve.Skip(1)).Any(pair => pair.Second.PhasePermille <= pair.First.PhasePermille))
        {
            problems.Add($"{p}: tide curve phases must rise strictly from 0 to 1000");
        }
    }
}
