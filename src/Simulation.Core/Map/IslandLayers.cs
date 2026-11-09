using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Map;

/// <summary>What the drainage made of a cell (hydrology layer).</summary>
[Flags]
public enum WaterFeature : byte
{
    /// <summary>Nothing: dry land or sea.</summary>
    None = 0,

    /// <summary>A running stream (centre or side cell of its channel).</summary>
    Stream = 1,

    /// <summary>A pond or lake: a fed hollow filled to its spill level.</summary>
    Lake = 2,

    /// <summary>A spring: groundwater meets the surface here; its stream starts in this cell.</summary>
    Spring = 4,
}

/// <summary>
/// The drainage of the island per cell (B2 R4, TL #19722): flow accumulation (rain of the whole
/// catchment plus springs above), the stream width where a stream runs, and which cells are springs,
/// streams or lakes. Integers only; generated with the island, saved in its blob (format 2), hashed
/// with the map. Islands saved before (format 1) have none.
/// </summary>
public sealed class HydrologyLayer
{
    private readonly int[] _accumulation;
    private readonly int[] _widthCm;
    private readonly WaterFeature[] _features;

    /// <summary>Creates the layer from row-major arrays (copied).</summary>
    public HydrologyLayer(int width, int depth, int[] accumulation, int[] widthCm, WaterFeature[] features)
    {
        ArgumentNullException.ThrowIfNull(accumulation);
        ArgumentNullException.ThrowIfNull(widthCm);
        ArgumentNullException.ThrowIfNull(features);
        if (accumulation.Length != width * depth || widthCm.Length != width * depth || features.Length != width * depth)
        {
            throw new ArgumentException("hydrology arrays must have one value per cell");
        }

        Width = width;
        Depth = depth;
        _accumulation = (int[])accumulation.Clone();
        _widthCm = (int[])widthCm.Clone();
        _features = (WaterFeature[])features.Clone();
    }

    /// <summary>Width in cells.</summary>
    public int Width { get; }

    /// <summary>Depth in cells.</summary>
    public int Depth { get; }

    /// <summary>Flow accumulation of the cell (accumulation units).</summary>
    public int Accumulation(int x, int z) => _accumulation[Index(x, z)];

    /// <summary>Stream width in centimetres where a stream runs, else 0.</summary>
    public int WidthCm(int x, int z) => _widthCm[Index(x, z)];

    /// <summary>What the drainage made of the cell.</summary>
    public WaterFeature Feature(int x, int z) => _features[Index(x, z)];

    /// <summary>Spring cells, row by row.</summary>
    public IEnumerable<(int X, int Z)> Springs()
    {
        for (var i = 0; i < _features.Length; i++)
        {
            if ((_features[i] & WaterFeature.Spring) != 0)
            {
                yield return (i % Width, i / Width);
            }
        }
    }

    /// <summary>Writes the layer in a fixed order.</summary>
    public void WriteTo(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Section("worldmap.hydrology").Write(Width).Write(Depth);
        for (var i = 0; i < _features.Length; i++)
        {
            hasher.Write(_accumulation[i]).Write(_widthCm[i]).Write((int)_features[i]);
        }
    }

    private int Index(int x, int z) =>
        x >= 0 && z >= 0 && x < Width && z < Depth ? (z * Width) + x : throw new ArgumentOutOfRangeException(nameof(x), $"cell ({x}, {z}) is outside the hydrology layer");
}

/// <summary>One cell of the underground (TL #19717): groundwater depth, soil, stones and ore.</summary>
/// <param name="GroundwaterDepth">Height units from the ground down to the groundwater table (0 = at the surface).</param>
/// <param name="Soil">Index into <see cref="UndergroundLayer.SoilIds"/>.</param>
/// <param name="StonesPermille">Stones in the ground, 0..1000.</param>
/// <param name="OrePermille">Ore in the ground, 0..1000.</param>
public readonly record struct GroundCell(int GroundwaterDepth, byte Soil, int StonesPermille, int OrePermille);

/// <summary>
/// The underground truth of the island from the seed (TL #19717, W1672 generator part): per cell
/// groundwater depth, soil, stones and ore. Generated with the island and saved in its blob (format 2);
/// not yet used by actions (digging, wells: B5). Islands saved before have none.
/// </summary>
public sealed class UndergroundLayer
{
    private readonly GroundCell[] _cells;

    /// <summary>Creates the layer from row-major cells (copied).</summary>
    public UndergroundLayer(int width, int depth, IReadOnlyList<string> soilIds, GroundCell[] cells)
    {
        ArgumentNullException.ThrowIfNull(soilIds);
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Length != width * depth)
        {
            throw new ArgumentException("underground needs one cell per map cell", nameof(cells));
        }

        if (soilIds.Count is 0 or > byte.MaxValue + 1 || cells.Any(c => c.Soil >= soilIds.Count))
        {
            throw new ArgumentException("every soil index needs a soil id", nameof(soilIds));
        }

        Width = width;
        Depth = depth;
        SoilIds = [.. soilIds];
        _cells = (GroundCell[])cells.Clone();
    }

    /// <summary>Width in cells.</summary>
    public int Width { get; }

    /// <summary>Depth in cells.</summary>
    public int Depth { get; }

    /// <summary>Soil ids; <see cref="GroundCell.Soil"/> indexes into it.</summary>
    public IReadOnlyList<string> SoilIds { get; }

    /// <summary>The underground cell at (x, z).</summary>
    public GroundCell Cell(int x, int z) =>
        x >= 0 && z >= 0 && x < Width && z < Depth ? _cells[(z * Width) + x] : throw new ArgumentOutOfRangeException(nameof(x), $"cell ({x}, {z}) is outside the underground layer");

    /// <summary>Soil id at (x, z).</summary>
    public string SoilAt(int x, int z) => SoilIds[Cell(x, z).Soil];

    /// <summary>Writes the layer in a fixed order.</summary>
    public void WriteTo(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Section("worldmap.underground").Write(Width).Write(Depth).Write(SoilIds.Count);
        foreach (var id in SoilIds)
        {
            hasher.Write(id);
        }

        foreach (var c in _cells)
        {
            hasher.Write(c.GroundwaterDepth).Write((int)c.Soil).Write(c.StonesPermille).Write(c.OrePermille);
        }
    }
}
