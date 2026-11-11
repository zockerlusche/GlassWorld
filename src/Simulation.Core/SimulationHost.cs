using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Inventions;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.View;

namespace GlassWorld.Simulation;

/// <summary>Switches of a host that are not world data (B2-5): they come from the program that runs the world.</summary>
/// <param name="DebugCommands">
/// Whether debug commands are accepted (default off): only then the core takes commands that go around a
/// rule of the world, e.g. <see cref="Look.LookEpochSystem.DebugSetLookEpoch"/>. Not part of the world state.
/// </param>
public sealed record SimulationHostOptions(bool DebugCommands = false)
{
    /// <summary>
    /// Whether the requests the core sends (decisions, conversations, night jobs, inventions, build plans,
    /// sounds) are validated against their schema before they leave (ADR-0009). On unless the host says
    /// otherwise, so every test and every check run validates without having to ask for it; the running
    /// game turns it off, because validating a request costs more than the rest of a step. What comes back
    /// from the AI is validated in any case. Not part of the world state: the same requests leave either way.
    /// </summary>
    public bool ValidateRequests { get; init; } = true;
}

/// <summary>
/// A complete simulation: content, world and its systems in their fixed order (objects → decisions →
/// inhabitants). Used by the headless runner, the game and save games, so everybody builds the world the
/// same way.
/// </summary>
public sealed class SimulationHost
{
    private SimulationHost(ContentSet content, World world, WorldMapSystem? map, WorldTypeCatalog types, WorldObjects objects, DecisionsSystem decisions, InhabitantsSystem inhabitants, Player.PlayerSystem player, InventionSystem inventions, ChronicleSystem chronicle, DeciderSettings deciderSettings, Writing.WritingSystem writing, SkillRegister skills, Player.MailboxSystem mailbox)
    {
        Writing = writing;
        Skills = skills;
        Mailbox = mailbox;
        Map = map;
        Player = player;
        Types = types;
        Inventions = inventions;
        Chronicle = chronicle;
        Content = content;
        World = world;
        Objects = objects;
        Decisions = decisions;
        Inhabitants = inhabitants;
        DeciderSettings = deciderSettings;
    }

    /// <summary>Loaded content.</summary>
    public ContentSet Content { get; }

    /// <summary>The world (tick, time, random streams, hash).</summary>
    public World World { get; }

    /// <summary>The generated island, or null on the placeholder world (B0/B1).</summary>
    public WorldMapSystem? Map { get; }

    /// <summary>The type truth of this world: content types plus runtime types (inventions).</summary>
    public WorldTypeCatalog Types { get; }

    /// <summary>Wishes to invent, invention jobs and the inventions of this world (D471 D5).</summary>
    public InventionSystem Inventions { get; }

    /// <summary>The chronicle (core state, D471 D8).</summary>
    public ChronicleSystem Chronicle { get; }

    /// <summary>
    /// Each inhabitant's diary of started actions (TL #20472): saved with the game, outside the world hash
    /// (the simulation never reads it).
    /// </summary>
    public InhabitantDiary Diary { get; private init; } = null!;

    /// <summary>Writing on objects and how it wears away (B8 R13-R15).</summary>
    public Writing.WritingSystem Writing { get; }

    /// <summary>The weather (B2 R13), or null while the world has no weather system.</summary>
    public Weather.IWeather? Weather { get; private init; }

    /// <summary>Skills the world knows (B4b R11, B5 R18).</summary>
    public SkillRegister Skills { get; }

    /// <summary>Objects.</summary>
    public WorldObjects Objects { get; }

    /// <summary>Questions to the decider and player commands.</summary>
    public DecisionsSystem Decisions { get; }

    /// <summary>The player's mailbox (B6 R10/R11).</summary>
    public Player.MailboxSystem Mailbox { get; }

    /// <summary>The player's part: favour, calls for help, gifts (B6).</summary>
    public Player.PlayerSystem Player { get; }

    /// <summary>Inhabitants.</summary>
    public InhabitantsSystem Inhabitants { get; }

    /// <summary>Decider settings in use.</summary>
    public DeciderSettings DeciderSettings { get; }

    /// <summary>Creates an empty simulation (no inhabitants, no objects) – the base for new worlds and save games.</summary>
    public static SimulationHost CreateEmpty(string contentDirectory, ulong seed, IDecisionPort port, string displayLocale, IJobPort? jobPort = null, SimulationHostOptions? options = null)
    {
        var content = ContentSet.Load(contentDirectory);
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(contentDirectory, "decider", "settings.json")));
        return Assemble(content, settings, new World(seed, content.Calendar), port, displayLocale, null, jobPort, null, options);
    }

    /// <summary>Assembles the systems around a given world (new or restored) in the fixed order.</summary>
    public static SimulationHost Assemble(ContentSet content, DeciderSettings settings, World world, IDecisionPort port, string displayLocale) =>
        Assemble(content, settings, world, port, displayLocale, null, null);

    /// <summary>
    /// Assembles the systems around a given world in the fixed order (worldmap → objects → decisions →
    /// inhabitants → inventions → night jobs → chronicle); with a preset the island is generated from the
    /// world seed (also when loading).
    /// </summary>
    /// <remarks>
    /// Rules judge targets by the world's type truth (content types plus runtime types). Without a job port
    /// the invention system and the night jobs are dormant; invention, night jobs and chronicle stay out of
    /// the world hash while empty, so worlds without them keep their hashes.
    /// </remarks>
    /// <param name="content">Content.</param>
    /// <param name="settings">Decider settings.</param>
    /// <param name="world">New or restored world.</param>
    /// <param name="port">Decider port.</param>
    /// <param name="displayLocale">Player language.</param>
    /// <param name="worldPreset">Preset of the island, or null for the placeholder world.</param>
    /// <param name="jobPort">Job port, or null (inventions dormant).</param>
    /// <param name="savedIsland">The island as a save game kept it (B2 R11); null = generate from seed and preset.</param>
    /// <param name="options">Switches of the host (debug commands); null = all off.</param>
    public static SimulationHost Assemble(ContentSet content, DeciderSettings settings, World world, IDecisionPort port, string displayLocale, string? worldPreset, IJobPort? jobPort = null, (GeneratedIsland Island, int CellSizeCm)? savedIsland = null, SimulationHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(world);
        options ??= new SimulationHostOptions();
        var types = new WorldTypeCatalog(content.Objects);
        var actions = content.Actions.WithTruth(types);
        var objects = new WorldObjects(types);
        WorldMapSystem? map = null;
        Weather.WeatherSystem? weather = null;
        string? climate = null;
        var seaLevel = 0;
        var dropPer100Height = 0;
        var startDropTenthC = 0;
        WorldPresetExploration? sight = null;
        if (worldPreset is not null)
        {
            if (!content.WorldGen.Presets.TryGetValue(worldPreset, out var preset))
            {
                throw new InvalidDataException($"Unknown world preset '{worldPreset}'.");
            }

            // A saved island is used as it is, so later changes of generator or data keep existing worlds.
            var island = savedIsland?.Island ?? WorldGenerator.Generate(content.WorldGen, content.Objects, worldPreset, world.Seed);
            if (!island.Passed)
            {
                throw new InvalidDataException($"World preset '{worldPreset}' with seed {world.Seed} gives no island that passes the check after {island.Attempts} attempts.");
            }

            var ticksPerHour = world.Calendar.Settings.TicksPerHour;
            // Regrowth by type from the spawn rules and the preset's drink spots (they agree, checked at load).
            var regrow = content.WorldGen.Spawns.Spawns.Select(r => (Type: r.ObjectType, Hours: r.RegrowHours))
                .Concat(preset.Water.DrinkSpots.Select(s => (Type: s.ObjectType, Hours: s.RegrowHours)))
                .Where(r => r.Hours is not null)
                .GroupBy(r => r.Type, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (long)g.First().Hours!.Value * ticksPerHour, StringComparer.Ordinal);
            // Tides (B2-5 R15): the level follows from the tick; with amplitude 0 no layer exists and walking
            // reads the base grid as before.
            var tides = new Tides(preset.Tides);
            map = new WorldMapSystem(island, savedIsland?.CellSizeCm ?? preset.Grid.CellSizeCm, objects, regrow)
            {
                NodeBudget = preset.Pathfinding.NodeBudget,
                Tides = tides,
                TideWalk = tides.Active
                    ? new TideWalkLayer(
                        island.Map,
                        tides,
                        preset.Water.ShallowMaxDepth,
                        WorldGenerator.WaterCost(preset.Pathfinding.CostShallowWater, preset.Pathfinding),
                        WorldGenerator.WaterCost(preset.Pathfinding.CostFloodedWater ?? 0, preset.Pathfinding))
                    : null,
                Clock = () => world.Tick,
            };
            world.Add(map);

            climate = preset.Climate.ClimateId;
            seaLevel = preset.Terrain.SeaLevel;
            sight = preset.Exploration;

            // Weather of the preset's climate (B2 R13); worlds without a preset (placeholder) have none.
            // The weather is island-wide for now; its height drop stands for the mean height of the start cells.
            var startHeight = island.Starts.Count == 0 ? 0 : island.Starts.Sum(s => Math.Max(0, island.Map.Cell(s.X, s.Z).Height - preset.Terrain.SeaLevel)) / island.Starts.Count;
            dropPer100Height = preset.Climate.TemperatureDropPer100Height;
            startDropTenthC = -(dropPer100Height * startHeight / 100);
            weather = new Weather.WeatherSystem(
                content.WorldGen.Climates[preset.Climate.ClimateId],
                startDropTenthC,
                world.Calendar.Settings.TicksPerHour,
                world.Calendar.Settings.HoursPerDay,
                world.Seed);
            world.Add(weather);
        }

        // The air where an inhabitant stands (B2582, comfort zone): the island-wide weather's temperature, with
        // the height drop of the inhabitant's own cell in place of the one of the start cells. Integers only.
        // Worlds without a climate (placeholder) have no air temperature.
        // The weather's part of it is the same for everybody within a tick, so it is worked out once per
        // tick and main weather, not once per inhabitant.
        (long Tick, string Weather, int TemperatureTenthC)? islandAir = null;
        Air? AirAt(World w, Position at)
        {
            if (weather is null || map is null)
            {
                return null;
            }

            var cellDropTenthC = map.CellOf(at) is { } cell
                ? -(dropPer100Height * Math.Max(0, map.Map.Cell(cell.X, cell.Z).Height - seaLevel) / 100)
                : startDropTenthC;
            if (islandAir is not { } known || known.Tick != w.Tick || !string.Equals(known.Weather, weather.Current, StringComparison.Ordinal))
            {
                islandAir = known = (w.Tick, weather.Current, weather.TemperatureAt(w.Now, weather.Current));
            }

            return new Air(known.TemperatureTenthC - startDropTenthC + cellDropTenthC, weather.PrecipitationPermille > 0);
        }

        var inhabitants = new InhabitantsSystem(content.InhabitantModel, actions, content.Brain, content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            AirAt = AirAt,
            Looks = content.Appearance,
            Names = content.InhabitantNames,
            Routes = map,

            // B2594: the weather lowers how far the inhabitants see (exploring and perceiving alike).
            Sight = new Sight(weather),
            Knowledge = content.Knowledge,
            EventKinds = content.EventKinds,

            // Start clothing fits the season, the climate, the biome at the place and the tech level (F4).
            ClothingAt = (w, at) => new ClothingContext(
                w.Now.SeasonId,
                climate,
                map?.CellOf(at) is { } cell ? map.Map.BiomeAt(cell.X, cell.Z) : null,
                content.Inventions.Settings.TechLevel),
        };
        CheckNightTriggers(settings, content.Knowledge);
        var decisions = new DecisionsSystem(settings, inhabitants, objects, actions, content.Brain, content.Vocabularies[VocabularyKind.Action], port, content.Inventions.Guard, displayLocale)
        {
            WeatherSource = weather,
            Knowledge = content.Knowledge,

            // A1487: questions wait for the night only where the night can answer them (a job port).
            NightAnswers = jobPort is not null && content.Knowledge.NightQuestions is not null,
            ValidateRequests = options.ValidateRequests,
        };
        inhabitants.QuestionOpen = decisions.IsAskingAbout;

        // Cloud cover and exploring (B2-6 R14): islands only, the placeholder world has no cells to see.
        // The sight range is the preset's (its exploration section, the same values the start check uses).
        var exploration = map is null || sight is null ? null : new Simulation.Exploration.ExplorationSystem(sight, map, inhabitants, seaLevel);
        if (exploration is not null)
        {
            inhabitants.FrontierOf = exploration.Frontier;
        }

        var player = new Simulation.Player.PlayerSystem(content.GiftRules, content.Favor, content.Brain.Weights, content.Brain, inhabitants, objects, world.Calendar.Settings.TicksPerHour);
        decisions.RegisterCommand(Simulation.Player.PlayerSystem.PlaceGift, player.Check, player.Apply);
        decisions.RegisterCommand(Simulation.Player.PlayerSystem.StartFavorCommand, player.CheckStartFavor, player.ApplyStartFavor);

        // The player's weather (B2-5 R13): a logged command that costs favour; start and end of storm and
        // drought go to the world's events.
        if (weather is not null)
        {
            weather.Report = inhabitants.Report;
        }

        var weatherIntervention = new Simulation.Player.WeatherIntervention(weather, player, inhabitants.Report);
        decisions.RegisterCommand(Simulation.Player.WeatherIntervention.SetWeather, weatherIntervention.Check, weatherIntervention.Apply);

        // The look epoch (B2-5 R16): the epochs count tech levels as numbers, the vocabulary gives their order.
        var lookEpoch = new Look.LookEpochSystem(content.LookEpochs, content.Inventions.ReachedTechLevel, options.DebugCommands, inhabitants.Report);
        decisions.RegisterCommand(Look.LookEpochSystem.SetLookEpoch, lookEpoch.Check, lookEpoch.Apply);
        decisions.RegisterCommand(Look.LookEpochSystem.DebugSetLookEpoch, lookEpoch.CheckDebug, lookEpoch.ApplyDebug);

        // One job port for all job systems: each receives only the outcomes of its own jobs (the invention
        // system takes the rest), and all write the world's one job log.
        var jobs = jobPort is null ? null : new JobRouter(jobPort);
        var inventions = new InventionSystem(content.Inventions, inhabitants, objects, types, content.Brain.Weights.PerceptionRadiusM, jobs?.Rest, displayLocale) { ValidateRequests = options.ValidateRequests };
        decisions.InventedName = typeId => inventions.Invention(typeId)?.Name;
        inhabitants.InventionNameKnownTo = inventions.NameKnownTo;

        // Memory (B4a R5): what the inhabitants remember comes from the core's events, by data; the compute
        // night condenses it and guesses at open questions (dormant without a job port).
        var memory = new MemoryRecorder(content.Knowledge.Memory, inhabitants, objects, actions);
        inhabitants.Reported += memory.Observe;
        var nightJobs = new Night.NightJobsSystem(content.Knowledge, content.Inventions.Build.NightPhases, types.PropertyVocabulary, content.Inventions.Guard, inhabitants, decisions, jobs, inventions.Log, displayLocale) { ValidateRequests = options.ValidateRequests };
        var chronicle = new ChronicleSystem(content.Chronicle);
        inhabitants.Reported += chronicle.Observe;

        // Each inhabitant's diary (TL #20472): presentation-only history, saved but not a world system (not hashed).
        var diary = new InhabitantDiary(content.InhabitantModel.Diary, inhabitants, objects);
        inhabitants.Reported += diary.Observe;
        var skills = new SkillRegister(content.InhabitantModel.Skills, content.InhabitantModel.SkillRules, content.SkillPractice);

        // Skill levels per inhabitant (B5 part): finished actions practise their skills.
        var skillLevels = new SkillLevelsSystem(content.SkillLevels, skills, world.Seed, world.Calendar.TicksPerDay);
        inhabitants.Reported += skillLevels.Observe;

        // New skills of an approved invention enter the register right away (refused ones are dropped, the invention stays).
        inhabitants.Reported += e =>
        {
            if (e.Kind == InventionSystem.Invented && e.TargetId is { } typeId)
            {
                foreach (var ability in inventions.AbilitiesOf(typeId))
                {
                    if (skills.Add(ability, typeId) is { } reason)
                    {
                        inhabitants.Report(new WorldEvent(SkillRegister.SkillRefused, e.Tick, e.InhabitantId, null, typeId) { Detail = reason });
                    }
                }
            }
        };
        // Texts wear by the weather (GW1 #95 wires the weather system before writing).
        var writing = new Writing.WritingSystem(content.Writing, objects, weather, world.Calendar.Settings.TicksPerHour, world.Calendar.TicksPerDay)
        {
            Inhabitants = inhabitants,
            PerceptionRadiusM = content.Brain.Weights.PerceptionRadiusM,
        };
        var mailbox = new Simulation.Player.MailboxSystem(content.MessageKinds, inhabitants, player, world.Calendar.Settings.TicksPerHour);
        decisions.RegisterCommand(Simulation.Player.MailboxSystem.Acknowledge, mailbox.Check, mailbox.Apply);

        // Talking with an inhabitant (B6 R13): the AI port is set by the game, the core decides.
        var expressions = content.Vocabularies[VocabularyKind.Expression].Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var conversation = new Simulation.Player.ConversationSystem(content.Conversation, content.Knowledge, inhabitants, player, decisions, content.Inventions.Guard, expressions, displayLocale, world.Calendar.Settings.TicksPerHour) { Diary = diary, ValidateRequests = options.ValidateRequests };
        decisions.RegisterCommand(Simulation.Player.ConversationSystem.SendText, conversation.Check, conversation.Apply);

        // The mailbox after the chronicle (agreed order): help_needed is reported by the player system earlier in the tick.
        // The look epoch last: it stays out of the hash while the world is in its first epoch.
        // Exploration right after the inhabitants: who has moved in a tick looks around in the same tick.
        world.Add(objects).Add(decisions).Add(inhabitants);
        if (exploration is not null)
        {
            world.Add(exploration);
        }

        // The night jobs right after the inventions: both stay out of the hash while they hold nothing.
        world.Add(player).Add(inventions).Add(nightJobs).Add(chronicle).Add(writing).Add(skills).Add(skillLevels).Add(mailbox).Add(conversation).Add(lookEpoch);
        var host = new SimulationHost(content, world, map, types, objects, decisions, inhabitants, player, inventions, chronicle, settings, writing, skills, mailbox)
        {
            WeatherSystem = weather,
            Weather = weather,
            LookEpoch = lookEpoch,
            Exploration = exploration,
            Options = options,
            SkillLevels = skillLevels,
            NightJobs = nightJobs,
            Conversation = conversation,
            Diary = diary,
        };

        // The player's language (#20043): a logged command, so replays ask in the same language at the same tick.
        decisions.RegisterCommand(DecisionsSystem.SetDisplayLocale, CheckDisplayLocale, (_, command) => host.UseDisplayLocale(command.Text!));
        return host;
    }

    /// <summary>Language of the texts the AI writes for this world from now on (display_locale of all requests).</summary>
    public string DisplayLocale => Decisions.DisplayLocale;

    /// <summary>
    /// After a restore: the language the saved log ends with (the last <see cref="DecisionsSystem.SetDisplayLocale"/>,
    /// else <see cref="DecisionsSystem.DefaultDisplayLocale"/>), so the world goes on as it was saved. A
    /// different player language then follows as a logged command.
    /// </summary>
    /// <remarks>Relies on the full command log: trimming or compacting the decision log must keep the
    /// <see cref="DecisionsSystem.SetDisplayLocale"/> commands (SaveGameTests pin this through a save file).</remarks>
    internal void UseLoggedDisplayLocale()
    {
        var locale = DecisionsSystem.DefaultDisplayLocale;
        foreach (var entry in Decisions.Log.Entries)
        {
            if (entry.Kind == DecisionLogKind.Command && entry.Json.Contains(DecisionsSystem.SetDisplayLocale, StringComparison.Ordinal)
                && System.Text.Json.JsonSerializer.Deserialize<PlayerCommand>(entry.Json) is { Kind: DecisionsSystem.SetDisplayLocale, Text: { } text })
            {
                locale = text;
            }
        }

        UseDisplayLocale(locale);
    }

    /// <summary>
    /// A trigger that asks at night (decider settings, A1487) needs a night that can answer it: its questions
    /// are about an object type and the knowledge data list it under night_questions. Otherwise its questions
    /// would stay open for good.
    /// </summary>
    private static void CheckNightTriggers(DeciderSettings settings, KnowledgeSettings knowledge)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (var trigger in settings.Triggers.Where(t => t.Ask == TriggerAsk.Night))
        {
            var kind = trigger.Kind.ToString().ToLowerInvariant();
            if (!DecisionsSystem.NightCanAnswer(kind)
                || knowledge?.NightQuestions?.Questions.Any(q => string.Equals(q.Trigger.ToString(), kind, StringComparison.OrdinalIgnoreCase)) != true)
            {
                throw new InvalidDataException($"Decider settings: trigger '{kind}' asks at night, but the night cannot answer it (only questions about an object type that knowledge night_questions lists).");
            }
        }
    }

    private static CommandResult CheckDisplayLocale(PlayerCommand command) =>
        Enum.GetValues<Locale>().Any(l => string.Equals(l.ToString().ToLowerInvariant(), command.Text, StringComparison.Ordinal))
            ? new CommandResult(true, null)
            : new CommandResult(false, DecisionsSystem.ReasonUnknownCommand);

    private void UseDisplayLocale(string locale)
    {
        Decisions.DisplayLocale = locale;
        Inventions.DisplayLocale = locale;
        NightJobs.DisplayLocale = locale;
        Conversation.DisplayLocale = locale;
    }

    /// <summary>
    /// A new world in another language than <see cref="DecisionsSystem.DefaultDisplayLocale"/> logs its
    /// language at the start (like the start favour), so the replay, which starts in the default, follows.
    /// </summary>
    private static void LogStartLocale(SimulationHost host, string displayLocale)
    {
        if (string.Equals(displayLocale, DecisionsSystem.DefaultDisplayLocale, StringComparison.Ordinal))
        {
            return;
        }

        var result = host.Decisions.ApplySetup(host.World, new PlayerCommand(DecisionsSystem.SetDisplayLocale, null) { Text = displayLocale });
        if (!result.Accepted)
        {
            throw new ArgumentException($"Unsupported display locale '{displayLocale}'.", nameof(displayLocale));
        }
    }

    /// <summary>
    /// Creates a new world on the flat placeholder start (content/world/placeholder-start.json) until B2
    /// generates the island: start inhabitants by group, objects scattered from the world seed.
    /// </summary>
    public static SimulationHost CreatePlaceholderWorld(string contentDirectory, ulong seed, IDecisionPort port, string displayLocale, IJobPort? jobPort = null, SimulationHostOptions? options = null)
    {
        var host = CreateEmpty(contentDirectory, seed, port, displayLocale, jobPort, options);
        var start = ContractSerializer.Deserialize<PlaceholderStart>(File.ReadAllText(Path.Combine(contentDirectory, "world", "placeholder-start.json")));
        var centres = start.Groups.ToDictionary(g => g.Group, g => new Position(g.X, g.Z));
        var rng = host.World.Random.Get("placeholder/start");
        var positions = new List<Position>();
        foreach (var starter in host.Content.InhabitantModel.Start.Inhabitants)
        {
            if (!centres.TryGetValue(starter.Group, out var centre))
            {
                throw new InvalidDataException($"Placeholder start has no centre for group {starter.Group}.");
            }

            positions.Add(Scatter(rng, centre, start.MemberSpreadM));
        }

        host.Inhabitants.SpawnStartInhabitants(host.World, positions);
        foreach (var group in start.Groups.OrderBy(g => g.Group))
        {
            foreach (var placement in start.Objects)
            {
                for (var i = 0; i < placement.CountPerGroup; i++)
                {
                    host.Objects.Spawn(placement.Type, Scatter(rng, centres[group.Group], placement.RadiusM));
                }
            }
        }

        LogStartLocale(host, displayLocale);
        return host;
    }

    /// <summary>
    /// Creates a new world on the island generated from the seed and preset (B2-2): start inhabitants
    /// at the start positions of their pair (group n = pair n − 1, members in start order), objects
    /// at the centres of their cells.
    /// </summary>
    /// <remarks>
    /// With <paramref name="inhabitants"/> the world starts with that many instead of those the data lists
    /// (<see cref="InhabitantsSystem.StartersFor"/>: the data's start inhabitants, repeated as further
    /// groups). A group the island has no start positions for wakes up around the start place of the group it
    /// repeats, on the nearest free dry cells of the same walkable area (<see cref="FurtherStartCells"/>).
    /// For measurements with many inhabitants (B2 acceptance: the step with 100 inhabitants); the game and
    /// the save games start with the data's inhabitants.
    /// </remarks>
    public static SimulationHost CreateGeneratedWorld(string contentDirectory, ulong seed, IDecisionPort port, string displayLocale, string worldPreset, IJobPort? jobPort = null, SimulationHostOptions? options = null, int? inhabitants = null)
    {
        var content = ContentSet.Load(contentDirectory);
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(contentDirectory, "decider", "settings.json")));
        var host = Assemble(content, settings, new World(seed, content.Calendar), port, displayLocale, worldPreset, jobPort, null, options);
        var map = host.Map!;
        var free = map.Island.Starts.GroupBy(s => s.Pair).ToDictionary(g => g.Key + 1, g => new Queue<StartPosition>(g));
        var listedGroups = content.InhabitantModel.Start.Inhabitants.Select(s => s.Group).DefaultIfEmpty(0).Max();
        var further = new Dictionary<int, IEnumerator<(int X, int Z)>>();
        var positions = new List<Position>();
        foreach (var starter in InhabitantsSystem.StartersFor(content.InhabitantModel.Start, inhabitants))
        {
            if (free.TryGetValue(starter.Group, out var queue) && queue.Count > 0)
            {
                var start = queue.Dequeue();
                positions.Add(map.CentreOf(start.X, start.Z));
                continue;
            }

            // A repeated group (see the remarks): around the start place of the listed group it repeats.
            var home = listedGroups == 0 ? starter.Group : ((starter.Group - 1) % listedGroups) + 1;
            if (starter.Group <= listedGroups || map.Island.Starts.FirstOrDefault(s => s.Pair + 1 == home) is not { } anchor)
            {
                throw new InvalidDataException($"The island has no free start position for group {starter.Group}.");
            }

            if (!further.TryGetValue(home, out var cells))
            {
                further[home] = cells = FurtherStartCells(map, anchor).GetEnumerator();
            }

            if (!cells.MoveNext())
            {
                throw new InvalidDataException($"The island has no free place left around the start position of group {home} (for group {starter.Group}).");
            }

            positions.Add(map.CentreOf(cells.Current.X, cells.Current.Z));
        }

        host.Inhabitants.SpawnStartInhabitants(host.World, positions, inhabitants);
        host.Exploration?.Look();
        foreach (var obj in map.Island.Objects)
        {
            map.Track(host.Objects.Spawn(obj.TypeId, map.CentreOf(obj.X, obj.Z)), obj.X, obj.Z);
        }

        LogStartLocale(host, displayLocale);
        return host;
    }

    /// <summary>How far apart further start inhabitants wake up, in cells (every second cell: nobody stands in another's way).</summary>
    private const int FurtherStartSpacingCells = 2;

    /// <summary>
    /// Free places around a start position for groups the island has no start positions for: dry cells of the
    /// start's walkable area without a start position or an object on them, every second cell, nearest ring
    /// first (rings of the square around the start; within a ring by z, then x). Deterministic, no random draw.
    /// </summary>
    private static IEnumerable<(int X, int Z)> FurtherStartCells(WorldMapSystem map, StartPosition anchor)
    {
        var area = map.AreaOf(anchor.X, anchor.Z);
        var taken = map.Island.Starts.Select(s => (s.X, s.Z)).Concat(map.Island.Objects.Select(o => (o.X, o.Z))).ToHashSet();
        var reach = Math.Max(map.Map.WidthCells, map.Map.DepthCells);
        for (var ring = FurtherStartSpacingCells; area != 0 && ring <= reach; ring += FurtherStartSpacingCells)
        {
            for (var dz = -ring; dz <= ring; dz += FurtherStartSpacingCells)
            {
                for (var dx = -ring; dx <= ring; dx += FurtherStartSpacingCells)
                {
                    var (x, z) = (anchor.X + dx, anchor.Z + dz);
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) == ring && map.AreaOf(x, z) == area
                        && map.Map.Cell(x, z).Water == WaterKind.None && !taken.Contains((x, z)))
                    {
                        yield return (x, z);
                    }
                }
            }
        }
    }

    /// <summary>
    /// How many requests of this world the AI service has refused as invalid so far (ADR-0009): the entries
    /// <see cref="ReplayNotes.RequestRejected"/> of the decision log and of the job log, and the refused
    /// conversation requests. It should stay 0: every request form has a contract test; a number here
    /// means the core and the service disagree about the contract. Read from the logs, so it survives a load.
    /// </summary>
    public int RejectedRequests =>
        Decisions.Log.CountOf(ReplayNotes.RequestRejected) + Inventions.Log.CountOf(ReplayNotes.RequestRejected)
        + Conversation.Log.Count(e => e.Kind == Simulation.Player.ConversationLogKind.Error && ReplayNotes.IsRejection(e.Json));

    /// <summary>
    /// The requests the service refused, oldest first: the request, the tick the refusal took effect and
    /// what the service said was wrong (read from the log entries, which keep the service's whole message).
    /// </summary>
    public IReadOnlyList<(string RequestId, long Tick, string? Message)> Rejections =>
        [.. Decisions.Log.Entries.Concat(Inventions.Log.Entries)
            .Where(e => string.Equals(e.Note, ReplayNotes.RequestRejected, StringComparison.Ordinal))
            .Select(e => (e.RequestId, e.Tick, ReplayNotes.MessageOf(e.Json)))
            .Concat(Conversation.Log
                .Where(e => e.Kind == Simulation.Player.ConversationLogKind.Error && ReplayNotes.IsRejection(e.Json))
                .Select(e => (e.RequestId, e.Tick, ReplayNotes.MessageOf(e.Json))))
            .OrderBy(r => r.Tick)];

    /// <summary>Read-only snapshot for the UI.</summary>
    public WorldSnapshot Snapshot() => WorldView.Snapshot(World, Inhabitants, Objects, Decisions) with { Weather = WeatherSystem?.Read(World), LookEpoch = LookEpoch.Current, Tide = Map?.TideAt(World.Tick), ExploredVersion = Exploration?.Version, ComputeNight = Inventions.IsComputeNight };

    /// <summary>
    /// Cloud cover and exploring (B2-6 R14): the cells every inhabitant has seen and the player's view, the
    /// union of them that is never covered again. Null on worlds without an island.
    /// </summary>
    public Exploration.ExplorationSystem? Exploration { get; private init; }

    /// <summary>The look epoch of the world (B2-5 R16).</summary>
    public Look.LookEpochSystem LookEpoch { get; private init; } = null!;

    /// <summary>The switches this host runs with (debug commands; all off by default).</summary>
    public SimulationHostOptions Options { get; private init; } = new();

    /// <summary>The weather system itself (save games, debug jumps, player intervention); null without weather.</summary>
    public Weather.WeatherSystem? WeatherSystem { get; private init; }

    /// <summary>Talking with inhabitants (B6 R13, A1342).</summary>
    public Player.ConversationSystem Conversation { get; private init; } = null!;

    /// <summary>Skill levels per inhabitant (start level, talent, practice; B5 part).</summary>
    public SkillLevelsSystem SkillLevels { get; private init; } = null!;

    /// <summary>The inhabitants' work of the compute night: memory jobs, then question jobs (B4a R5, B7).</summary>
    public Night.NightJobsSystem NightJobs { get; private init; } = null!;

    private static Position Scatter(Random.Pcg32 rng, Position centre, double radius) =>
        new(centre.X + (((rng.NextDouble() * 2.0) - 1.0) * radius), centre.Z + (((rng.NextDouble() * 2.0) - 1.0) * radius));
}
