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

    /// <summary>
    /// The personal space of the inhabitants in centimetres (spacing.min_separation_m of
    /// content/behaviour/gaits.json, W1695): the two start cells of a pair lie at least this far apart, so
    /// nobody starts inside the other. 0 = different cells only.
    /// </summary>
    public int StartSeparationCm { get; private init; }

    /// <summary>Biome ids in table order (the index a <see cref="MapCell"/> stores).</summary>
    public IReadOnlyList<string> BiomeIds => [.. Biomes.Biomes.Select(b => b.Id)];

    /// <summary>Loads and cross-checks the world data of a content folder.</summary>
    /// <param name="contentDirectory">The content folder.</param>
    /// <param name="vocabularies">The vocabularies the data refers to.</param>
    /// <param name="objects">The object catalogue.</param>
    /// <param name="calendar">The calendar (weather events).</param>
    /// <param name="minSeparationM">
    /// The personal space of the inhabitants in metres (spacing.min_separation_m of the gaits data; 0 =
    /// none). Required, so no caller builds the world data without it.
    /// </param>
    public static WorldGenContent Load(
        string contentDirectory,
        IReadOnlyDictionary<VocabularyKind, Vocabulary> vocabularies,
        ObjectCatalog objects,
        WorldCalendar calendar,
        double minSeparationM)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minSeparationM);
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
        var soilVocabulary = Ids(vocabularies, VocabularyKind.Soil);
        CheckBiomes(biomes, biomeVocabulary, problems);
        CheckSpawns(spawns, biomes, objects, problems);
        foreach (var climate in climates)
        {
            CheckClimate(climate, weatherVocabulary, Ids(vocabularies, VocabularyKind.Event), biomeVocabulary, calendar, problems);
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
            // The island's types: what the spawn rules place and what the hydrology puts at its water.
            var islandTypes = spawns.Spawns.Select(s => s.ObjectType).Concat(preset.Water.DrinkSpots.Select(s => s.ObjectType))
                .Distinct(StringComparer.Ordinal).Where(objects.Contains).ToList();
            foreach (var spot in preset.Water.DrinkSpots)
            {
                if (!objects.Contains(spot.ObjectType))
                {
                    problems.Add($"{p}: drink spot object type '{spot.ObjectType}' not in the catalogue");
                }

                // A hollow needs its depression test; the test needs its radius (TL #19990).
                if ((spot.At == WorldPresetDrinkSpotAt.Hollow && spot.MinHollowDepth is null) || (spot.MinHollowDepth is null) != (spot.HollowRadiusCells is null))
                {
                    problems.Add($"{p}: drink spot '{spot.At}' needs min_hollow_depth and hollow_radius_cells together (hollow: required)");
                }

                // Regrowth belongs to the type: drink spots and spawn rules of one type must agree.
                var others = spawns.Spawns.Where(r => r.ObjectType == spot.ObjectType).Select(r => r.RegrowHours)
                    .Concat(preset.Water.DrinkSpots.Where(o => o.ObjectType == spot.ObjectType).Select(o => o.RegrowHours));
                if (others.Distinct().Count() > 1 && preset.Water.DrinkSpots.First(o => o.ObjectType == spot.ObjectType) == spot)
                {
                    problems.Add($"{p}: drink spots and spawn rules of '{spot.ObjectType}' disagree on regrow_hours");
                }
            }

            foreach (var resource in preset.Start.Supply.Resources)
            {
                var unknown = resource.PropertiesAll.Concat(resource.PropertiesNone).Where(pr => !objects.PropertyVocabulary.Contains(pr)).ToList();
                if (unknown.Count > 0)
                {
                    problems.Add($"{p}: start supply '{resource.Id}' uses unknown properties {string.Join(", ", unknown)}");
                }
                else if (!islandTypes.Any(type => resource.PropertiesAll.All(pr => objects.HasProperty(type, pr)) && !resource.PropertiesNone.Any(pr => objects.HasProperty(type, pr))))
                {
                    problems.Add($"{p}: start supply '{resource.Id}' matches no spawn type of the island");
                }
            }

            var soils = preset.Underground.SoilRules.Select(r => r.Soil).Append(preset.Underground.FallbackSoil).Append(preset.Underground.Stones.StoneSoil);
            problems.AddRange(soils.Distinct(StringComparer.Ordinal).Where(id => !soilVocabulary.Contains(id)).Select(id => $"{p}: soil '{id}' not in vocabulary 'soil'"));

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
            climates.ToDictionary(c => c.Id, StringComparer.Ordinal))
        {
            // Rounded up to whole centimetres, so the cells lie at least the data distance apart.
            StartSeparationCm = (int)Math.Ceiling((minSeparationM * 100) - 1e-9),
        };
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
        problems.AddRange(table.RequiredBiomes.Where(id => !defined.Contains(id)).Select(id => $"required biome '{id}' is no biome of the table"));
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

    /// <summary>
    /// The optional parts of a climate's temperature (B5 R1/R28): hourly curves of known seasons, one per
    /// season, hours ascending within a day; biome offsets of known biomes and seasons, one per pair; a season
    /// transition needs the temperature table in calendar order (the previous season is the one before).
    /// </summary>
    private static void CheckTemperatureExtras(string climateId, ClimateTemperature temperature, IReadOnlySet<string> biomeVocabulary, WorldCalendar calendar, List<string> problems)
    {
        var c = $"climate '{climateId}'";
        var seasons = calendar.Settings.Seasons.Select(s => s.Id).ToList();
        var hoursPerDay = calendar.Settings.HoursPerDay;
        var curves = temperature.Hourly ?? [];
        Duplicates(curves.Select(h => h.Season), $"{c} hourly curve of season", problems);
        foreach (var curve in curves)
        {
            if (!seasons.Contains(curve.Season))
            {
                problems.Add($"{c}: hourly curve for unknown season '{curve.Season}'");
            }

            var hours = curve.Points.Select(pt => pt.Hour).ToList();
            if (hours.Zip(hours.Skip(1)).Any(pair => pair.Second <= pair.First))
            {
                problems.Add($"{c}: hourly curve of '{curve.Season}' needs strictly ascending hours");
            }

            if (hours.Any(h => h >= hoursPerDay))
            {
                problems.Add($"{c}: hourly curve of '{curve.Season}' has an hour at or after {hoursPerDay}");
            }
        }

        var biomes = temperature.Biomes ?? [];
        Duplicates(biomes.Select(b => $"{b.Biome}/{b.Season ?? "*"}"), $"{c} biome temperature offset", problems);
        problems.AddRange(biomes.Where(b => !biomeVocabulary.Contains(b.Biome)).Select(b => $"{c}: biome temperature offset for '{b.Biome}' not in vocabulary 'biome'"));
        problems.AddRange(biomes.Where(b => b.Season is { } s && !seasons.Contains(s)).Select(b => $"{c}: biome temperature offset for unknown season '{b.Season}'"));

        if ((temperature.SeasonTransitionHours ?? 0) > 0 && !temperature.Seasons.Select(s => s.Season).SequenceEqual(seasons, StringComparer.Ordinal))
        {
            problems.Add($"{c}: season_transition_hours needs the temperature seasons in calendar order ({string.Join(", ", seasons)})");
        }
    }

    private static void CheckClimate(ClimateSettings climate, HashSet<string> weatherVocabulary, HashSet<string> eventVocabulary, IReadOnlySet<string> biomeVocabulary, WorldCalendar calendar, List<string> problems)
    {
        var weather = climate.Weather;
        var states = weather.States.Select(s => s.Weather).ToHashSet(StringComparer.Ordinal);

        // Signals (B2-5 R13): a condition is a weather of this climate, its events are in the event vocabulary.
        var signals = weather.Signals ?? [];
        Duplicates(signals.Select(s => s.Condition), $"climate '{climate.Id}' signal", problems);
        problems.AddRange(signals.Where(s => !states.Contains(s.Condition)).Select(s => $"climate '{climate.Id}': signal condition '{s.Condition}' has no state values"));
        problems.AddRange(signals.SelectMany(s => new[] { s.StartEvent, s.EndEvent }).Where(e => !eventVocabulary.Contains(e)).Select(e => $"climate '{climate.Id}': signal event '{e}' not in vocabulary 'event'"));
        Duplicates(weather.States.Select(s => s.Weather), $"climate '{climate.Id}' weather state", problems);
        problems.AddRange(states.Where(s => !weatherVocabulary.Contains(s)).Select(s => $"climate '{climate.Id}': weather '{s}' not in vocabulary 'weather'"));
        var temperature = weather.Temperature;
        problems.AddRange(calendar.Settings.Seasons.Select(s => s.Id).Where(s => !temperature.Seasons.Any(t => t.Season == s)).Select(s => $"climate '{climate.Id}': no temperature offset for season '{s}'"));
        problems.AddRange(states.Where(s => !temperature.Weathers.Any(t => t.Weather == s)).Select(s => $"climate '{climate.Id}': no temperature offset for weather '{s}'"));
        problems.AddRange(Enum.GetNames<Time.DayPhase>().Select(p => p.ToLowerInvariant()).Where(p => !temperature.TimesOfDay.Any(t => t.TimeOfDay == p)).Select(p => $"climate '{climate.Id}': no temperature offset for time of day '{p}'"));
        CheckTemperatureExtras(climate.Id, temperature, biomeVocabulary, calendar, problems);
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
        var channel = preset.Water.Channel.ToList();
        if (channel.Zip(channel.Skip(1)).Any(pair => pair.Second.Accumulation <= pair.First.Accumulation))
        {
            problems.Add($"{p}: channel points must rise strictly in accumulation");
        }

        if (preset.Water.Springs.Discharge < preset.Water.StreamMinAccumulation && preset.Water.Springs.MaxCount > 0)
        {
            // A spring whose water is too little for a stream would be a puddle without an outflow.
            problems.Add($"{p}: spring discharge must reach stream_min_accumulation, so a stream runs from every spring");
        }

        foreach (var rule in preset.Underground.SoilRules)
        {
            Range($"{p} soil rule '{rule.Soil}' height", rule.MinHeight, rule.MaxHeight, problems);
            Range($"{p} soil rule '{rule.Soil}' moisture", rule.MinMoisturePermille, rule.MaxMoisturePermille, problems);
        }

        if (preset.Underground.Stones.StoneSoil != preset.Underground.FallbackSoil && preset.Underground.SoilRules.All(r => r.Soil != preset.Underground.Stones.StoneSoil))
        {
            problems.Add($"{p}: stony soil '{preset.Underground.Stones.StoneSoil}' is no soil of the rules");
        }

        Range($"{p} caves", preset.Caves.Min, preset.Caves.Max, problems);
        Range($"{p} neighbour islands", preset.Neighbours.Islands.Min, preset.Neighbours.Islands.Max, problems);
        Range($"{p} neighbour group size", preset.Neighbours.GroupSize.Min, preset.Neighbours.GroupSize.Max, problems);
        Range($"{p} pair distance", preset.Start.MinPairDistanceCells, preset.Start.MaxPairDistanceCells, problems);
        if (preset.Start.MinPairDistanceCells <= preset.Exploration.ViewRangeCells + preset.Exploration.MaxHeightBonusCells)
        {
            // R9: the start pairs must not see each other, not even from a height (B2-6 R14: the same
            // values are the sight range of the cloud cover).
            problems.Add($"{p}: min_pair_distance_cells must exceed view_range_cells plus max_height_bonus_cells");
        }

        var curve = preset.Tides.Curve.ToList();
        if (curve[0].PhasePermille != 0 || curve[^1].PhasePermille != 1000
            || curve.Zip(curve.Skip(1)).Any(pair => pair.Second.PhasePermille <= pair.First.PhasePermille))
        {
            problems.Add($"{p}: tide curve phases must rise strictly from 0 to 1000");
        }

        if (curve[0].LevelPermille != curve[^1].LevelPermille)
        {
            // Phase 1000 is phase 0 of the next cycle: otherwise the water jumps once every cycle (review GW2 #182).
            problems.Add($"{p}: tide curve must end at the level it starts with (phase 0: {curve[0].LevelPermille}, phase 1000: {curve[^1].LevelPermille})");
        }
    }
}
