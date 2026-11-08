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
    private SimulationHost(ContentSet content, World world, WorldMapSystem? map, WorldTypeCatalog types, WorldObjects objects, DecisionsSystem decisions, InhabitantsSystem inhabitants, Player.PlayerSystem player, InventionSystem inventions, ChronicleSystem chronicle, DeciderSettings deciderSettings, Writing.WritingSystem writing)
    {
        Writing = writing;
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

    /// <summary>Objects.</summary>
    public WorldObjects Objects { get; }

    /// <summary>Questions to the decider and player commands.</summary>
    public DecisionsSystem Decisions { get; }

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
    public static SimulationHost Assemble(ContentSet content, DeciderSettings settings, World world, IDecisionPort port, string displayLocale, string? worldPreset, IJobPort? jobPort = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(world);
        var types = new WorldTypeCatalog(content.Objects);
        var actions = content.Actions.WithTruth(types);
        var objects = new WorldObjects(types);
        WorldMapSystem? map = null;
        if (worldPreset is not null)
        {
            if (!content.WorldGen.Presets.TryGetValue(worldPreset, out var preset))
            {
                throw new InvalidDataException($"Unknown world preset '{worldPreset}'.");
            }

            var island = WorldGenerator.Generate(content.WorldGen, content.Objects, worldPreset, world.Seed);
            if (!island.Passed)
            {
                throw new InvalidDataException($"World preset '{worldPreset}' with seed {world.Seed} gives no island that passes the check after {island.Attempts} attempts.");
            }

            var ticksPerHour = world.Calendar.Settings.TicksPerHour;
            var regrow = content.WorldGen.Spawns.Spawns
                .Where(r => r.RegrowHours is not null)
                .GroupBy(r => r.ObjectType, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (long)g.First().RegrowHours!.Value * ticksPerHour, StringComparer.Ordinal);
            map = new WorldMapSystem(island, preset.Grid.CellSizeCm, objects, regrow);
            world.Add(map);
        }

        var inhabitants = new InhabitantsSystem(content.InhabitantModel, actions, content.Brain, objects, world.Calendar.Settings.TicksPerHour);
        var decisions = new DecisionsSystem(settings, inhabitants, objects, actions, content.Brain, content.Vocabularies[VocabularyKind.Action], port, displayLocale);
        var player = new Simulation.Player.PlayerSystem(content.GiftRules, content.Favor, content.Brain.Weights, content.Brain, inhabitants, objects, world.Calendar.Settings.TicksPerHour);
        decisions.RegisterCommand(Simulation.Player.PlayerSystem.PlaceGift, player.Check, player.Apply);
        decisions.RegisterCommand(Simulation.Player.PlayerSystem.StartFavorCommand, player.CheckStartFavor, player.ApplyStartFavor);
        var inventions = new InventionSystem(content.Inventions, inhabitants, objects, types, content.Brain.Weights.PerceptionRadiusM, jobPort, displayLocale);
        var chronicle = new ChronicleSystem(content.Chronicle);
        inhabitants.Reported += chronicle.Observe;
        // Weather comes with GW1's weather core (Host.Weather, IWeather); until then texts wear only by themselves.
        var writing = new Writing.WritingSystem(content.Writing, objects, weather: null, world.Calendar.Settings.TicksPerHour, world.Calendar.TicksPerDay);
        world.Add(objects).Add(decisions).Add(inhabitants).Add(player).Add(inventions).Add(chronicle).Add(writing);
        return new SimulationHost(content, world, map, types, objects, decisions, inhabitants, player, inventions, chronicle, settings, writing);
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
    public WorldSnapshot Snapshot() => WorldView.Snapshot(World, Inhabitants, Objects, Decisions);

    private static Position Scatter(Random.Pcg32 rng, Position centre, double radius) =>
        new(centre.X + (((rng.NextDouble() * 2.0) - 1.0) * radius), centre.Z + (((rng.NextDouble() * 2.0) - 1.0) * radius));
}
