using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Map;

/// <summary>
/// The generated start island as a world system (B2 R1): the base grid never changes after generation,
/// so the system does nothing per tick and writes the island data into the state hash. It is rebuilt
/// from seed and preset when a save game is loaded (the generator is deterministic).
/// </summary>
public sealed class WorldMapSystem : IWorldSystem
{
    /// <summary>Creates the system around a generated island.</summary>
    public WorldMapSystem(GeneratedIsland island, int cellSizeCm)
    {
        ArgumentNullException.ThrowIfNull(island);
        ArgumentOutOfRangeException.ThrowIfLessThan(cellSizeCm, 1);
        Island = island;
        CellSizeCm = cellSizeCm;
        IslandHash = island.Hash();
    }

    /// <summary>Hash of the generated data, computed once (the grid never changes).</summary>
    public string IslandHash { get; }

    /// <inheritdoc/>
    public string Name => "worldmap";

    /// <summary>The generated island (map, objects, starts, check).</summary>
    public GeneratedIsland Island { get; }

    /// <summary>The base grid.</summary>
    public WorldMap Map => Island.Map;

    /// <summary>Edge length of a cell in centimetres (from the preset).</summary>
    public int CellSizeCm { get; }

    /// <summary>World position (metres, origin at the map corner) of the centre of cell (x, z).</summary>
    public Position CentreOf(int x, int z) => new((x + 0.5) * CellSizeCm / 100.0, (z + 0.5) * CellSizeCm / 100.0);

    /// <inheritdoc/>
    public void Advance(World world)
    {
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(Island.PresetId).Write(IslandHash);
    }
}
