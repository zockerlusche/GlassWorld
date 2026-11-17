namespace GlassWorld.Simulation.Map;

/// <summary>
/// A1769 (B2805, review GW2 #584): an island the new-world window's preview generated, with everything the generator read
/// besides the content - so a new world takes it over only when it is exactly the island its own choice would give.
/// </summary>
/// <param name="Island">The generated island (carries its seed and preset).</param>
/// <param name="GridCells">The grid size it was made with; null = the preset's grid.</param>
/// <param name="ResourceFactor">The resource factor it was made with (1 = the data as written).</param>
/// <param name="StartPairs">The start pairs it was made with; null = the preset's pairs.</param>
/// <param name="TerrainFactor">A1833: the terrain factor it was made with (1 = the preset as written).</param>
/// <param name="ClusterFactor">A1833: the cluster factor it was made with (1 = the data as written).</param>
public sealed record PreviewIsland(GeneratedIsland Island, int? GridCells, double ResourceFactor, int? StartPairs, double TerrainFactor = 1.0, double ClusterFactor = 1.0)
{
    /// <summary>Whether this is the island that seed, preset, grid, resource, terrain and cluster factors and pairs give.</summary>
    public bool Fits(ulong seed, string presetId, int? gridCells, double resourceFactor, int? startPairs, double terrainFactor = 1.0, double clusterFactor = 1.0) =>
        Island.Seed == seed
        && string.Equals(Island.PresetId, presetId, StringComparison.Ordinal)
        && GridCells == gridCells
        && Math.Abs(ResourceFactor - resourceFactor) < 1e-9
        && StartPairs == startPairs
        && Math.Abs(TerrainFactor - terrainFactor) < 1e-9
        && Math.Abs(ClusterFactor - clusterFactor) < 1e-9;
}
