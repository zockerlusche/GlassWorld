using System.IO.Compression;
using System.Text;

namespace GlassWorld.Simulation.Map;

/// <summary>
/// The generated island as compact bytes for the save game (B2 R11): every cell, the island objects,
/// starts, neighbour islands and the check, deflate-compressed. A save game loads its island from here,
/// so a later change of generator or world data never changes a world that already exists.
/// Format 2 adds the hydrology and underground layers after the cells (each behind a presence flag);
/// format 1 blobs (saved before) still decode, as islands without these layers.
/// </summary>
public static class IslandCodec
{
    /// <summary>Format written by this code.</summary>
    public const int Format = 2;
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

            WriteLayers(w, map);
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

    /// <summary>The format of an encoded island (0 if the bytes are no island).</summary>
    public static int FormatOf(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var buffer = new MemoryStream(data);
            using var deflate = new DeflateStream(buffer, CompressionMode.Decompress);
            using var r = new BinaryReader(deflate, Encoding.UTF8);
            return r.ReadBytes(Magic.Length).SequenceEqual(Magic) ? r.ReadInt32() : 0;
        }
        catch (InvalidDataException)
        {
            return 0;
        }
        catch (EndOfStreamException)
        {
            return 0;
        }
    }

    /// <summary>Decodes an island; returns it with the cell size it was placed with.</summary>
    public static (GeneratedIsland Island, int CellSizeCm) Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var buffer = new MemoryStream(data);
        using var deflate = new DeflateStream(buffer, CompressionMode.Decompress);
        using var r = new BinaryReader(deflate, Encoding.UTF8);
        var magic = r.ReadBytes(Magic.Length).SequenceEqual(Magic);
        var format = magic ? r.ReadInt32() : 0;
        if (format is not (1 or Format))
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

        if (format >= 2)
        {
            ReadLayers(r, builder);
        }

        var objects = Enumerable.Range(0, r.ReadInt32()).Select(_ => new IslandObject(r.ReadString(), r.ReadInt32(), r.ReadInt32())).ToList();
        var starts = Enumerable.Range(0, r.ReadInt32()).Select(_ => new StartPosition(r.ReadInt32(), r.ReadInt32(), r.ReadInt32())).ToList();
        var neighbours = Enumerable.Range(0, r.ReadInt32()).Select(_ => new NeighbourIsland(r.ReadInt32(), r.ReadInt32(), r.ReadInt32())).ToList();
        var check = Enumerable.Range(0, r.ReadInt32()).Select(_ => new MandatoryResult(r.ReadString(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32())).ToList();
        var island = new GeneratedIsland(presetId, generatorVersion, seed, attempts, passed, builder.Build(), objects, starts, neighbours, check, cliffCells);
        return (island, cellSizeCm);
    }

    private static void WriteLayers(BinaryWriter w, WorldMap map)
    {
        w.Write(map.Hydrology is not null);
        if (map.Hydrology is { } h)
        {
            for (var z = 0; z < h.Depth; z++)
            {
                for (var x = 0; x < h.Width; x++)
                {
                    w.Write(h.Accumulation(x, z));
                    w.Write(h.WidthCm(x, z));
                    w.Write((byte)h.Feature(x, z));
                }
            }
        }

        w.Write(map.Underground is not null);
        if (map.Underground is { } u)
        {
            w.Write(u.SoilIds.Count);
            foreach (var id in u.SoilIds)
            {
                w.Write(id);
            }

            for (var z = 0; z < u.Depth; z++)
            {
                for (var x = 0; x < u.Width; x++)
                {
                    var c = u.Cell(x, z);
                    w.Write(c.GroundwaterDepth);
                    w.Write(c.Soil);
                    w.Write(c.StonesPermille);
                    w.Write(c.OrePermille);
                }
            }
        }
    }

    private static void ReadLayers(BinaryReader r, WorldMapBuilder builder)
    {
        int width = builder.ChunksX * builder.ChunkCells, depth = builder.ChunksZ * builder.ChunkCells, n = width * depth;
        if (r.ReadBoolean())
        {
            var accumulation = new int[n];
            var widthCm = new int[n];
            var features = new WaterFeature[n];
            for (var i = 0; i < n; i++)
            {
                accumulation[i] = r.ReadInt32();
                widthCm[i] = r.ReadInt32();
                features[i] = (WaterFeature)r.ReadByte();
            }

            builder.Hydrology = new HydrologyLayer(width, depth, accumulation, widthCm, features);
        }

        if (r.ReadBoolean())
        {
            var soils = Enumerable.Range(0, r.ReadInt32()).Select(_ => r.ReadString()).ToList();
            var cells = new GroundCell[n];
            for (var i = 0; i < n; i++)
            {
                cells[i] = new GroundCell(r.ReadInt32(), r.ReadByte(), r.ReadInt32(), r.ReadInt32());
            }

            builder.Underground = new UndergroundLayer(width, depth, soils, cells);
        }
    }
}
