using GlassWorld.Contracts;
using GlassWorld.Simulation.Map;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Presentation.World;

/// <summary>A1771: the island the preview showed, with the choice it was made for (the start takes it over if it still fits).</summary>
/// <param name="Choice">The window's choice when the island was generated.</param>
/// <param name="Island">The generated island with what it was made with (the host takes it over only when that fits).</param>
public sealed record PreviewShown(NewWorldChoice Choice, PreviewIsland Island);

/// <summary>
/// A1771 (B2805): the new-world window's preview - generates the island of a choice exactly as the start would (same
/// generator, seed, preset and world specification) and draws its picture. The start then takes this island over
/// (SimulationHostOptions.Island). No Godot; safe to run off the main thread (it only reads the loaded content).
/// </summary>
/// <param name="WorldGen">The world generation content.</param>
/// <param name="Catalog">The object catalog.</param>
/// <param name="PresetId">The island preset a new world starts with (content/look/world.json).</param>
/// <param name="Terrain">The terrain look data (ground colours, preview values).</param>
/// <param name="Palette">The palette the ground colours name.</param>
public sealed record NewWorldPreview(WorldGenContent WorldGen, ObjectCatalog Catalog, string PresetId, LookTerrain Terrain, LookPalette Palette)
{
    /// <summary>The island of <paramref name="seed"/> with <paramref name="spec"/>, and its picture.</summary>
    public (PreviewIsland Island, PreviewImage Image) Make(ulong seed, WorldSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var factor = spec.ResourceFactor ?? 1.0;
        var island = WorldGenerator.Generate(WorldGen, Catalog, PresetId, seed, spec.GridCells, factor, spec.StartPairs);
        var look = TerrainLook.From(Terrain, Palette, island.Map, WorldGen.Presets[PresetId]);
        return (new PreviewIsland(island, spec.GridCells, factor, spec.StartPairs), IslandPreview.Draw(island.Map, look, PreviewLook.From(Terrain)));
    }
}
