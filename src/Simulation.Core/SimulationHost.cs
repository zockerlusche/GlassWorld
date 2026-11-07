using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Simulation.Content;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
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
    private SimulationHost(ContentSet content, World world, WorldObjects objects, DecisionsSystem decisions, InhabitantsSystem inhabitants, DeciderSettings deciderSettings)
    {
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
    public static SimulationHost Assemble(ContentSet content, DeciderSettings settings, World world, IDecisionPort port, string displayLocale)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(world);
        var objects = new WorldObjects(content.Objects);
        var inhabitants = new InhabitantsSystem(content.InhabitantModel, content.Actions, content.Brain, objects, world.Calendar.Settings.TicksPerHour);
        var decisions = new DecisionsSystem(settings, inhabitants, objects, content.Actions, content.Brain, content.Vocabularies[VocabularyKind.Action], port, displayLocale);
        world.Add(objects).Add(decisions).Add(inhabitants);
        return new SimulationHost(content, world, objects, decisions, inhabitants, settings);
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

    /// <summary>Read-only snapshot for the UI.</summary>
    public WorldSnapshot Snapshot() => WorldView.Snapshot(World, Inhabitants, Objects, Decisions);

    private static Position Scatter(Random.Pcg32 rng, Position centre, double radius) =>
        new(centre.X + (((rng.NextDouble() * 2.0) - 1.0) * radius), centre.Z + (((rng.NextDouble() * 2.0) - 1.0) * radius));
}
