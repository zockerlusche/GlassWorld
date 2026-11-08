using System.IO.Compression;
using System.Text;

namespace GlassWorld.Simulation.Map;

/// <summary>
/// The generated island as compact bytes for the save game (B2 R11): every cell, the island objects,
/// starts, neighbour islands and the check, deflate-compressed. A save game loads its island from here,
/// so a later change of generator or world data never changes a world that already exists.
/// </summary>
public static class IslandCodec
{
    private const int Format = 1;
    private static readonly byte[] Magic = "GWIS"u8.ToArray();

    /// <summary>Encodes an island and the cell size it was placed with.</summary>
    public static byte[] Encode(GeneratedIsland island, int cellSizeCm)
    {
        ArgumentNullException.ThrowIfNull(island);
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var w = new BinaryWriter(deflate, Encoding.UTF8))
        {
            w.Write(Magic);
            w.Write(Format);
            w.Write(island.PresetId);
            w.Write(island.GeneratorVersion);
            w.Write(island.Seed);
            w.Write(island.Attempts);
            w.Write(island.Passed);
            w.Write(island.CliffCells);
            w.Write(cellSizeCm);

            var map = island.Map;
            w.Write(map.ChunkCells);
            w.Write(map.ChunksX);
            w.Write(map.ChunksZ);
            w.Write(map.BiomeIds.Count);
            foreach (var biome in map.BiomeIds)
            {
                w.Write(biome);
            }

            for (var z = 0; z < map.DepthCells; z++)
            {
                for (var x = 0; x < map.WidthCells; x++)
                {
                    var c = map.Cell(x, z);
                    w.Write(c.Height);
                    w.Write((byte)c.Water);
                    w.Write(c.WaterDepth);
                    w.Write(c.Flow);
                    w.Write(c.Moisture);
                    w.Write(c.Temperature);
                    w.Write(c.Biome);
                    w.Write(c.WalkCost);
                }
            }

            w.Write(island.Objects.Count);
            foreach (var o in island.Objects)
            {
                w.Write(o.TypeId);
                w.Write(o.X);
                w.Write(o.Z);
            }

            w.Write(island.Starts.Count);
            foreach (var s in island.Starts)
            {
                w.Write(s.Pair);
                w.Write(s.X);
                w.Write(s.Z);
            }

            w.Write(island.Neighbours.Count);
            foreach (var n in island.Neighbours)
            {
                w.Write(n.X);
                w.Write(n.Z);
                w.Write(n.GroupSize);
            }

            w.Write(island.Check.Count);
            foreach (var c in island.Check)
            {
                w.Write(c.Id);
                w.Write(c.Reachable);
                w.Write(c.Total);
                w.Write(c.Min);
                w.Write(c.Max);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Decodes an island; returns it with the cell size it was placed with.</summary>
    public static (GeneratedIsland Island, int CellSizeCm) Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var buffer = new MemoryStream(data);
        using var deflate = new DeflateStream(buffer, CompressionMode.Decompress);
        using var r = new BinaryReader(deflate, Encoding.UTF8);
        if (!r.ReadBytes(Magic.Length).SequenceEqual(Magic) || r.ReadInt32() != Format)
        {
            throw new InvalidDataException("Saved island data has an unknown format.");
        }

        var presetId = r.ReadString();
        var generatorVersion = r.ReadInt32();
        var seed = r.ReadUInt64();
        var attempts = r.ReadInt32();
        var passed = r.ReadBoolean();
        var cliffCells = r.ReadInt32();
        var cellSizeCm = r.ReadInt32();

        int chunkCells = r.ReadInt32(), chunksX = r.ReadInt32(), chunksZ = r.ReadInt32();
        var biomes = Enumerable.Range(0, r.ReadInt32()).Select(_ => r.ReadString()).ToList();
        var builder = new WorldMapBuilder(chunkCells, chunksX, chunksZ, biomes);
        for (var z = 0; z < chunkCells * chunksZ; z++)
        {
            for (var x = 0; x < chunkCells * chunksX; x++)
            {
                builder.Set(x, z, new MapCell(r.ReadInt32(), (WaterKind)r.ReadByte(), r.ReadInt32(), r.ReadSByte(), r.ReadInt32(), r.ReadInt32(), r.ReadByte(), r.ReadInt32()));
            }
        }

        var objects = Enumerable.Range(0, r.ReadInt32()).Select(_ => new IslandObject(r.ReadString(), r.ReadInt32(), r.ReadInt32())).ToList();
        var starts = Enumerable.Range(0, r.ReadInt32()).Select(_ => new StartPosition(r.ReadInt32(), r.ReadInt32(), r.ReadInt32())).ToList();
        var neighbours = Enumerable.Range(0, r.ReadInt32()).Select(_ => new NeighbourIsland(r.ReadInt32(), r.ReadInt32(), r.ReadInt32())).ToList();
        var check = Enumerable.Range(0, r.ReadInt32()).Select(_ => new MandatoryResult(r.ReadString(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32())).ToList();
        var island = new GeneratedIsland(presetId, generatorVersion, seed, attempts, passed, builder.Build(), objects, starts, neighbours, check, cliffCells);
        return (island, cellSizeCm);
    }
}
