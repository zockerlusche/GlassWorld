using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Map;

/// <summary>Water in a cell (B2 R4).</summary>
public enum WaterKind : byte
{
    /// <summary>Dry land.</summary>
    None = 0,

    /// <summary>Fresh water (spring, stream, lake, swamp).</summary>
    Fresh = 1,

    /// <summary>Salt water (sea).</summary>
    Salt = 2,
}

/// <summary>The eight flow directions; <see cref="MapCell.Flow"/> indexes into <see cref="All"/> (-1 = still water).</summary>
public static class FlowDirections
{
    /// <summary>Directions as cell steps: east, north-east, north, north-west, west, south-west, south, south-east.</summary>
    public static IReadOnlyList<(int Dx, int Dz)> All { get; } = [(1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1), (1, 1)];
}

/// <summary>
/// One cell of the base grid (B2 R1). All values are integers so generation and hashing are exact on
/// every platform (ADR-0002): heights and depths in height units, moisture in permille, temperature in
/// tenths of a degree, walking cost relative to flat meadow (1000).
/// </summary>
/// <param name="Height">Ground height in height units (sea level from the preset).</param>
/// <param name="Water">Water in the cell.</param>
/// <param name="WaterDepth">Water depth in height units (0 for dry land).</param>
/// <param name="Flow">Flow direction 0..7 (east, then counter-clockwise in 45° steps), -1 for none.</param>
/// <param name="Moisture">Moisture 0..1000.</param>
/// <param name="Temperature">Base temperature in tenths of a degree Celsius.</param>
/// <param name="Biome">Index into <see cref="WorldMap.BiomeIds"/>.</param>
/// <param name="WalkCost">Walking cost, 1000 = flat meadow; <see cref="MapCell.Blocked"/> for obstacles.</param>
public readonly record struct MapCell(int Height, WaterKind Water, int WaterDepth, sbyte Flow, int Moisture, int Temperature, byte Biome, int WalkCost)
{
    /// <summary>Walking cost of a cell that cannot be entered on foot.</summary>
    public const int Blocked = int.MaxValue;
}

/// <summary>
/// The generated base grid of the island (B2 R1, R11): square chunks of cells, each chunk generated
/// from (seed, chunk coordinate) so the map can grow later (B9). Immutable once built; states that
/// change at run time (tides, exploration, weather) live in their own systems.
/// </summary>
public sealed class WorldMap
{
    private readonly MapCell[][] _chunks;

    internal WorldMap(int chunkCells, int chunksX, int chunksZ, IReadOnlyList<string> biomeIds, MapCell[][] chunks)
    {
        ChunkCells = chunkCells;
        ChunksX = chunksX;
        ChunksZ = chunksZ;
        BiomeIds = biomeIds;
        _chunks = chunks;
    }

    /// <summary>Edge length of a chunk in cells.</summary>
    public int ChunkCells { get; }

    /// <summary>Number of chunks along x.</summary>
    public int ChunksX { get; }

    /// <summary>Number of chunks along z.</summary>
    public int ChunksZ { get; }

    /// <summary>Width in cells.</summary>
    public int WidthCells => ChunksX * ChunkCells;

    /// <summary>Depth in cells.</summary>
    public int DepthCells => ChunksZ * ChunkCells;

    /// <summary>Biome ids in table order; <see cref="MapCell.Biome"/> indexes into it.</summary>
    public IReadOnlyList<string> BiomeIds { get; }

    /// <summary>Whether (x, z) lies on the map.</summary>
    public bool Contains(int x, int z) => x >= 0 && z >= 0 && x < WidthCells && z < DepthCells;

    /// <summary>The cell at (x, z).</summary>
    public MapCell Cell(int x, int z)
    {
        if (!Contains(x, z))
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"cell ({x}, {z}) is outside the {WidthCells}x{DepthCells} map");
        }

        var chunk = _chunks[((z / ChunkCells) * ChunksX) + (x / ChunkCells)];
        return chunk[((z % ChunkCells) * ChunkCells) + (x % ChunkCells)];
    }

    /// <summary>Biome id of the cell at (x, z).</summary>
    public string BiomeAt(int x, int z) => BiomeIds[Cell(x, z).Biome];

    /// <summary>Writes the grid in a fixed order (chunks row by row, cells row by row) into the hasher.</summary>
    public void WriteTo(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(ChunkCells).Write(ChunksX).Write(ChunksZ).Write(BiomeIds.Count);
        foreach (var id in BiomeIds)
        {
            hasher.Write(id);
        }

        foreach (var chunk in _chunks)
        {
            foreach (var c in chunk)
            {
                hasher.Write(c.Height).Write((int)c.Water).Write(c.WaterDepth).Write((int)c.Flow)
                    .Write(c.Moisture).Write(c.Temperature).Write((int)c.Biome).Write(c.WalkCost);
            }
        }
    }

    /// <summary>The world hash of the grid alone (B2 acceptance 1).</summary>
    public string Hash()
    {
        using var hasher = new StateHasher();
        hasher.Section("worldmap.grid");
        WriteTo(hasher);
        return hasher.Finish();
    }
}

/// <summary>Mutable grid for the generator; <see cref="Build"/> hands out an immutable copy.</summary>
public sealed class WorldMapBuilder
{
    private readonly MapCell[][] _chunks;
    private readonly IReadOnlyList<string> _biomeIds;

    /// <summary>Creates an empty grid of chunksX × chunksZ chunks.</summary>
    public WorldMapBuilder(int chunkCells, int chunksX, int chunksZ, IReadOnlyList<string> biomeIds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkCells, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunksX, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunksZ, 1);
        ArgumentNullException.ThrowIfNull(biomeIds);
        if (biomeIds.Count is 0 or > byte.MaxValue + 1)
        {
            throw new ArgumentException($"between 1 and 256 biomes expected, got {biomeIds.Count}", nameof(biomeIds));
        }

        ChunkCells = chunkCells;
        ChunksX = chunksX;
        ChunksZ = chunksZ;
        _biomeIds = [.. biomeIds];
        _chunks = new MapCell[chunksX * chunksZ][];
        for (var i = 0; i < _chunks.Length; i++)
        {
            _chunks[i] = new MapCell[chunkCells * chunkCells];
        }
    }

    /// <summary>Edge length of a chunk in cells.</summary>
    public int ChunkCells { get; }

    /// <summary>Number of chunks along x.</summary>
    public int ChunksX { get; }

    /// <summary>Number of chunks along z.</summary>
    public int ChunksZ { get; }

    /// <summary>Sets the cell at (x, z).</summary>
    public void Set(int x, int z, MapCell cell)
    {
        if (x < 0 || z < 0 || x >= ChunksX * ChunkCells || z >= ChunksZ * ChunkCells)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"cell ({x}, {z}) is outside the grid");
        }

        if (cell.Biome >= _biomeIds.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(cell), $"biome index {cell.Biome} has no biome id");
        }

        _chunks[((z / ChunkCells) * ChunksX) + (x / ChunkCells)][((z % ChunkCells) * ChunkCells) + (x % ChunkCells)] = cell;
    }

    /// <summary>An immutable map; later changes to the builder do not affect it.</summary>
    public WorldMap Build() => new(ChunkCells, ChunksX, ChunksZ, _biomeIds, [.. _chunks.Select(c => (MapCell[])c.Clone())]);
}
