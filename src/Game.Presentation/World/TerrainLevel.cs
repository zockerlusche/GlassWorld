using GlassWorld.Contracts;
using GlassWorld.Simulation.Map;

namespace GlassWorld.Presentation.World;

/// <summary>
/// One detail level of the terrain chunks (B2 R18). Level 0 draws every cell of the base grid; a coarser
/// level merges blocks of <see cref="CellStep"/> × <see cref="CellStep"/> plain dry cells into a few
/// triangles where that stays within <see cref="MaxErrorM"/> and <see cref="MaxColorDelta"/>, and draws
/// all other cells (coast, banks, water, cliffs) as level 0 does (<see cref="TerrainMesher.Ground(TerrainLevel, int, int)"/>).
/// The base grid never changes at run time (<see cref="WorldMap"/> is immutable; tides, weather and
/// exploration live in their own systems), so every level is built once; only a palette change (season,
/// epoch) rebuilds the meshes. The distances say when the view shows the level (Godot visibility ranges,
/// measured from the camera to the chunk's centre): from <see cref="VisibleFromM"/> on (0 = from the
/// camera), up to <see cref="VisibleToM"/> (0 = to the horizon), where the next level begins. Levels switch
/// without fade: Godot's fade dithers the fading chunk and draws it in the transparent pass, where it painted
/// the sea floor over the sea (window run L758). A switch margin gives hysteresis so a chunk does not pop
/// back and forth while the camera eases (see <see cref="RangeBeginM"/>). Neighbouring levels share their
/// borders and differ only within the tolerances, so the switch does not show.
/// </summary>
/// <param name="CellStep">Grid cells per merged block edge (1 = base grid).</param>
/// <param name="Base">The base grid.</param>
/// <param name="Look">Look of the island.</param>
/// <param name="VisibleFromM">Distance from which the level is drawn (0 = from the camera).</param>
/// <param name="VisibleToM">Distance up to which the level is drawn (0 = to the horizon).</param>
/// <param name="MaxErrorM">Largest height difference of a merged block from the base grid (metres).</param>
/// <param name="MaxColorDelta">Largest colour difference per channel (0..1) of a merged block from the base grid.</param>
/// <param name="SeaFloorMarginM">How far below the sea surface every corner of a merged block of sea bed must lie.</param>
/// <param name="SwitchMarginM">Hysteresis of the switch between two levels (metres, see <see cref="RangeBeginM"/>).</param>
public sealed record TerrainLevel(
    int CellStep,
    WorldMap Base,
    TerrainLook Look,
    float VisibleFromM,
    float VisibleToM,
    float MaxErrorM,
    float MaxColorDelta,
    float SeaFloorMarginM,
    float SwitchMarginM)
{
    /// <summary>
    /// Godot's <c>visibility_range_begin</c> (0 = none). With fade disabled Godot uses the margin as
    /// hysteresis per instance: a hidden instance appears only <see cref="SwitchMarginM"/> inside its range,
    /// a shown one disappears only that far outside it. The range is widened by the margin on both inner
    /// ends, so a hidden level appears exactly at the switch distance (a new chunk after a camera cut is
    /// never at neither level) and a shown one stays up to twice the margin beyond it (no popping while
    /// the camera eases; both levels may show in that band, they share their borders).
    /// </summary>
    public float RangeBeginM => VisibleFromM > 0 ? VisibleFromM - SwitchMarginM : 0;

    /// <summary>Godot's <c>visibility_range_end</c> (0 = none), widened by the margin like <see cref="RangeBeginM"/>.</summary>
    public float RangeEndM => VisibleToM > 0 ? VisibleToM + SwitchMarginM : 0;
    /// <summary>
    /// The levels of the detail data for a map, near to far. The first level must draw the base grid
    /// (step 1), so figures and objects stand on the ground the near view shows; steps rise and divide the
    /// chunk size; coarser levels need their tolerances; every level but the last ends at a rising distance.
    /// </summary>
    public static IReadOnlyList<TerrainLevel> From(TerrainDetail detail, WorldMap map, TerrainLook look)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(look);
        var levels = detail.Levels.ToList();
        if (levels.Count == 0 || levels[0].CellStep != 1)
        {
            throw new InvalidDataException("Terrain detail: the first level must have cell_step 1 (the base grid).");
        }

        var result = new List<TerrainLevel>(levels.Count);
        for (var i = 0; i < levels.Count; i++)
        {
            var level = levels[i];
            var last = i == levels.Count - 1;
            if (map.ChunkCells % level.CellStep != 0)
            {
                throw new InvalidDataException($"Terrain detail: cell_step {level.CellStep} does not divide the chunk size {map.ChunkCells}.");
            }

            if (i > 0 && (level.CellStep <= levels[i - 1].CellStep || level.MaxErrorMm is null || level.MaxColorDeltaPermille is null))
            {
                throw new InvalidDataException("Terrain detail: cell steps must rise from level to level, and coarser levels need max_error_mm and max_color_delta_permille.");
            }

            if (last != (level.UntilM is null))
            {
                throw new InvalidDataException("Terrain detail: every level but the last needs until_m, the last none.");
            }

            var previousEnd = i == 0 ? 0f : (float)levels[i - 1].UntilM!.Value;
            var end = last ? 0f : (float)level.UntilM!.Value;
            var margin = (float)detail.SwitchMarginM;
            if (!last && end - previousEnd <= 4 * margin)
            {
                throw new InvalidDataException($"Terrain detail: until_m {end} must lie more than four switch margins beyond the level before ({previousEnd}).");
            }

            if (i > 0 && previousEnd <= margin)
            {
                throw new InvalidDataException("Terrain detail: until_m must lie beyond the switch margin.");
            }

            // The farther level starts where the nearer one ends (plus the switch hysteresis).
            result.Add(new TerrainLevel(
                level.CellStep,
                map,
                look,
                previousEnd,
                end,
                (level.MaxErrorMm ?? 0) / 1000f,
                (level.MaxColorDeltaPermille ?? 0) / 1000f,
                detail.SeaFloorMarginMm / 1000f,
                margin));
        }

        return result;
    }

    /// <summary>The same level with the colours of another look (palette change); the grid stays.</summary>
    public TerrainLevel Recolored(TerrainLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        return this with { Look = look };
    }
}
