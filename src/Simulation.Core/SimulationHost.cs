using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
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
    private SimulationHost(ContentSet content, World world, WorldMapSystem? map, WorldObjects objects, DecisionsSystem decisions, InhabitantsSystem inhabitants, DeciderSettings deciderSettings)
    {
        Map = map;
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

    /// <summary>Objects.</summary>
    public WorldObjects Objects { get; }

    /// <summary>Questions to the decider and player commands.</summary>
    public DecisionsSystem Decisions { get; }

    /// <summary>Inhabitants.</summary>
    public InhabitantsSystem Inhabitants { get; }

    /// <summary>Decider settings in use.</summary>
    public DeciderSettings DeciderSettings { get; }

    /// <summary>Creates an empty simulation (no inhabitants, no objects) – the base for new worlds and save games.</summary>
    public static SimulationHost CreateEmpty(string contentDirectory, ulong seed, IDecisionPort port, string displayLocale)
    {
        var content = ContentSet.Load(contentDirectory);
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(contentDirectory, "decider", "settings.json")));
        return Assemble(content, settings, new World(seed, content.Calendar), port, displayLocale);
    }

    /// <summary>Assembles the systems around a given world (new or restored) in the fixed order.</summary>
    public static SimulationHost Assemble(ContentSet content, DeciderSettings settings, World world, IDecisionPort port, string displayLocale) =>
        Assemble(content, settings, world, port, displayLocale, null);

    /// <summary>
    /// Assembles the systems around a given world in the fixed order (worldmap → objects → decisions →
    /// inhabitants); with a preset the island is generated from the world seed (also when loading).
    /// </summary>
    public static SimulationHost Assemble(ContentSet content, DeciderSettings settings, World world, IDecisionPort port, string displayLocale, string? worldPreset)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(world);
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

            map = new WorldMapSystem(island, preset.Grid.CellSizeCm);
            world.Add(map);
        }

        var objects = new WorldObjects(content.Objects);
        var inhabitants = new InhabitantsSystem(content.InhabitantModel, content.Actions, content.Brain, objects, world.Calendar.Settings.TicksPerHour);
        var decisions = new DecisionsSystem(settings, inhabitants, objects, content.Actions, content.Brain, content.Vocabularies[VocabularyKind.Action], port, displayLocale);
        world.Add(objects).Add(decisions).Add(inhabitants);
        return new SimulationHost(content, world, map, objects, decisions, inhabitants, settings);
    }

    /// <summary>
    /// Creates a new world on the flat placeholder start (content/world/placeholder-start.json) until B2
    /// generates the island: start inhabitants by group, objects scattered from the world seed.
    /// </summary>
    public static SimulationHost CreatePlaceholderWorld(string contentDirectory, ulong seed, IDecisionPort port, string displayLocale)
    {
        var host = CreateEmpty(contentDirectory, seed, port, displayLocale);
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
    public static SimulationHost CreateGeneratedWorld(string contentDirectory, ulong seed, IDecisionPort port, string displayLocale, string worldPreset)
    {
        var content = ContentSet.Load(contentDirectory);
        var settings = ContractSerializer.Deserialize<DeciderSettings>(File.ReadAllText(Path.Combine(contentDirectory, "decider", "settings.json")));
        var host = Assemble(content, settings, new World(seed, content.Calendar), port, displayLocale, worldPreset);
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
            host.Objects.Spawn(obj.TypeId, map.CentreOf(obj.X, obj.Z));
        }

        return host;
    }

    /// <summary>Read-only snapshot for the UI.</summary>
    public WorldSnapshot Snapshot() => WorldView.Snapshot(World, Inhabitants, Objects, Decisions);

    private static Position Scatter(Random.Pcg32 rng, Position centre, double radius) =>
        new(centre.X + (((rng.NextDouble() * 2.0) - 1.0) * radius), centre.Z + (((rng.NextDouble() * 2.0) - 1.0) * radius));
}
