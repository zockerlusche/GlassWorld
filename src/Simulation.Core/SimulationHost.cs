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
    public static SimulationHost CreateEmpty(string contentDirectory, ulong seed, IDecisionPort port, string displayLocale, IJobPort? jobPort = null)
    {
        var content = ContentSet.Load(contentDirectory);
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(contentDirectory, "decider", "settings.json")));
        return Assemble(content, settings, new World(seed, content.Calendar), port, displayLocale, null, jobPort);
    }

    /// <summary>Assembles the systems around a given world (new or restored) in the fixed order.</summary>
    public static SimulationHost Assemble(ContentSet content, DeciderSettings settings, World world, IDecisionPort port, string displayLocale) =>
        Assemble(content, settings, world, port, displayLocale, null, null);

    /// <summary>
    /// Assembles the systems around a given world in the fixed order (worldmap → objects → decisions →
    /// inhabitants → inventions → chronicle); with a preset the island is generated from the world seed
    /// (also when loading).
    /// </summary>
    /// <remarks>
    /// Rules judge targets by the world's type truth (content types plus runtime types). Without a job port
    /// the invention system is dormant; invention and chronicle stay out of the world hash while empty, so
    /// worlds without inventions keep their hashes.
    /// </remarks>
    /// <param name="content">Content.</param>
    /// <param name="settings">Decider settings.</param>
    /// <param name="world">New or restored world.</param>
    /// <param name="port">Decider port.</param>
    /// <param name="displayLocale">Player language.</param>
    /// <param name="worldPreset">Preset of the island, or null for the placeholder world.</param>
    /// <param name="jobPort">Job port, or null (inventions dormant).</param>
    /// <param name="savedIsland">The island as a save game kept it (B2 R11); null = generate from seed and preset.</param>
    public static SimulationHost Assemble(ContentSet content, DeciderSettings settings, World world, IDecisionPort port, string displayLocale, string? worldPreset, IJobPort? jobPort = null, (GeneratedIsland Island, int CellSizeCm)? savedIsland = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(world);
        var types = new WorldTypeCatalog(content.Objects);
        var actions = content.Actions.WithTruth(types);
        var objects = new WorldObjects(types);
        WorldMapSystem? map = null;
        Weather.WeatherSystem? weather = null;
        string? climate = null;
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
            map = new WorldMapSystem(island, savedIsland?.CellSizeCm ?? preset.Grid.CellSizeCm, objects, regrow) { NodeBudget = preset.Pathfinding.NodeBudget };
            world.Add(map);

            climate = preset.Climate.ClimateId;

            // Weather of the preset's climate (B2 R13); worlds without a preset (placeholder) have none.
            // The weather is island-wide for now; its height drop stands for the mean height of the start cells.
            var startHeight = island.Starts.Count == 0 ? 0 : island.Starts.Sum(s => Math.Max(0, island.Map.Cell(s.X, s.Z).Height - preset.Terrain.SeaLevel)) / island.Starts.Count;
            weather = new Weather.WeatherSystem(
                content.WorldGen.Climates[preset.Climate.ClimateId],
                -(preset.Climate.TemperatureDropPer100Height * startHeight / 100),
                world.Calendar.Settings.TicksPerHour,
                world.Calendar.Settings.HoursPerDay,
                world.Seed);
            world.Add(weather);
        }

        var inhabitants = new InhabitantsSystem(content.InhabitantModel, actions, content.Brain, content.Gaits, objects, world.Calendar.Settings.TicksPerHour)
        {
            Looks = content.Appearance,
            Routes = map,
            Knowledge = content.Knowledge,
            EventKinds = content.EventKinds,

            // Start clothing fits the season, the climate, the biome at the place and the tech level (F4).
            ClothingAt = (w, at) => new ClothingContext(
                w.Now.SeasonId,
                climate,
                map?.CellOf(at) is { } cell ? map.Map.BiomeAt(cell.X, cell.Z) : null,
                content.Inventions.Settings.TechLevel),
        };
        var decisions = new DecisionsSystem(settings, inhabitants, objects, actions, content.Brain, content.Vocabularies[VocabularyKind.Action], port, displayLocale)
        {
            WeatherSource = weather,
            Knowledge = content.Knowledge,
        };
        inhabitants.QuestionOpen = decisions.HasOpenQuestion;
        var player = new Simulation.Player.PlayerSystem(content.GiftRules, content.Favor, content.Brain.Weights, content.Brain, inhabitants, objects, world.Calendar.Settings.TicksPerHour);
        decisions.RegisterCommand(Simulation.Player.PlayerSystem.PlaceGift, player.Check, player.Apply);
        decisions.RegisterCommand(Simulation.Player.PlayerSystem.StartFavorCommand, player.CheckStartFavor, player.ApplyStartFavor);
        var inventions = new InventionSystem(content.Inventions, inhabitants, objects, types, content.Brain.Weights.PerceptionRadiusM, jobPort, displayLocale);
        var chronicle = new ChronicleSystem(content.Chronicle);
        inhabitants.Reported += chronicle.Observe;
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
        var conversation = new Simulation.Player.ConversationSystem(content.Conversation, content.Knowledge, inhabitants, player, decisions, content.Inventions.Guard, expressions, displayLocale, world.Calendar.Settings.TicksPerHour);
        decisions.RegisterCommand(Simulation.Player.ConversationSystem.SendText, conversation.Check, conversation.Apply);

        // The mailbox after the chronicle (agreed order): help_needed is reported by the player system earlier in the tick.
        world.Add(objects).Add(decisions).Add(inhabitants).Add(player).Add(inventions).Add(chronicle).Add(writing).Add(skills).Add(skillLevels).Add(mailbox).Add(conversation);
        return new SimulationHost(content, world, map, types, objects, decisions, inhabitants, player, inventions, chronicle, settings, writing, skills, mailbox)
        {
            WeatherSystem = weather,
            Weather = weather,
            SkillLevels = skillLevels,
            Conversation = conversation,
        };
    }

    /// <summary>
    /// Creates a new world on the flat placeholder start (content/world/placeholder-start.json) until B2
    /// generates the island: start inhabitants by group, objects scattered from the world seed.
    /// </summary>
    public static SimulationHost CreatePlaceholderWorld(string contentDirectory, ulong seed, IDecisionPort port, string displayLocale, IJobPort? jobPort = null)
    {
        var host = CreateEmpty(contentDirectory, seed, port, displayLocale, jobPort);
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

        return host;
    }

    /// <summary>
    /// Creates a new world on the island generated from the seed and preset (B2-2): start inhabitants
    /// at the start positions of their pair (group n = pair n − 1, members in start order), objects
    /// at the centres of their cells.
    /// </summary>
    public static SimulationHost CreateGeneratedWorld(string contentDirectory, ulong seed, IDecisionPort port, string displayLocale, string worldPreset, IJobPort? jobPort = null)
    {
        var content = ContentSet.Load(contentDirectory);
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(contentDirectory, "decider", "settings.json")));
        var host = Assemble(content, settings, new World(seed, content.Calendar), port, displayLocale, worldPreset, jobPort);
        var map = host.Map!;
        var free = map.Island.Starts.GroupBy(s => s.Pair).ToDictionary(g => g.Key + 1, g => new Queue<StartPosition>(g));
        var positions = new List<Position>();
        foreach (var starter in content.InhabitantModel.Start.Inhabitants)
        {
            if (!free.TryGetValue(starter.Group, out var queue) || queue.Count == 0)
            {
                throw new InvalidDataException($"The island has no free start position for group {starter.Group}.");
            }

            var start = queue.Dequeue();
            positions.Add(map.CentreOf(start.X, start.Z));
        }

        host.Inhabitants.SpawnStartInhabitants(host.World, positions);
        foreach (var obj in map.Island.Objects)
        {
            map.Track(host.Objects.Spawn(obj.TypeId, map.CentreOf(obj.X, obj.Z)), obj.X, obj.Z);
        }

        return host;
    }

    /// <summary>Read-only snapshot for the UI.</summary>
    public WorldSnapshot Snapshot() => WorldView.Snapshot(World, Inhabitants, Objects, Decisions) with { Weather = WeatherSystem?.Read(World) };

    /// <summary>The weather system itself (save games, debug jumps, player intervention); null without weather.</summary>
    public Weather.WeatherSystem? WeatherSystem { get; private init; }

    /// <summary>Talking with inhabitants (B6 R13, A1342).</summary>
    public Player.ConversationSystem Conversation { get; private init; } = null!;

    /// <summary>Skill levels per inhabitant (start level, talent, practice; B5 part).</summary>
    public SkillLevelsSystem SkillLevels { get; private init; } = null!;

    private static Position Scatter(Random.Pcg32 rng, Position centre, double radius) =>
        new(centre.X + (((rng.NextDouble() * 2.0) - 1.0) * radius), centre.Z + (((rng.NextDouble() * 2.0) - 1.0) * radius));
}
