using System.Buffers.Binary;
using System.Numerics;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Exploration;

/// <summary>
/// A set of grid cells as one bit per cell (B2-6 R14): cells are only ever added, never taken out again.
/// The index of a cell is <c>z × width + x</c>.
/// </summary>
public sealed class CellSet
{
    private readonly ulong[] _bits;

    /// <summary>Creates an empty set over a grid.</summary>
    public CellSet(int width, int depth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        Width = width;
        Depth = depth;
        _bits = new ulong[(((long)width * depth) + 63) / 64];
    }

    /// <summary>Width of the grid in cells.</summary>
    public int Width { get; }

    /// <summary>Depth of the grid in cells.</summary>
    public int Depth { get; }

    /// <summary>Number of cells in the set.</summary>
    public int Count { get; private set; }

    /// <summary>Whether the cell (x, z) is in the set; false outside the grid.</summary>
    public bool Contains(int x, int z) => x >= 0 && z >= 0 && x < Width && z < Depth && Contains((z * Width) + x);

    /// <summary>Whether the cell with this index is in the set.</summary>
    public bool Contains(int index) => (_bits[index >> 6] & (1UL << (index & 63))) != 0;

    /// <summary>
    /// Adds the cells <paramref name="fromX"/>..<paramref name="toX"/> (both included, inside the grid) of
    /// row <paramref name="z"/>; the indexes of the cells that are new go to <paramref name="added"/>.
    /// </summary>
    public void AddRow(int z, int fromX, int toX, List<int>? added = null)
    {
        var from = (z * Width) + fromX;
        var to = (z * Width) + toX;
        var first = from >> 6;
        var last = to >> 6;
        for (var word = first; word <= last; word++)
        {
            var mask = ulong.MaxValue;
            if (word == first)
            {
                mask &= ulong.MaxValue << (from & 63);
            }

            if (word == last)
            {
                mask &= ulong.MaxValue >> (63 - (to & 63));
            }

            var fresh = mask & ~_bits[word];
            if (fresh == 0)
            {
                continue;
            }

            _bits[word] |= fresh;
            Count += BitOperations.PopCount(fresh);
            if (added is null)
            {
                continue;
            }

            while (fresh != 0)
            {
                added.Add((word << 6) + BitOperations.TrailingZeroCount(fresh));
                fresh &= fresh - 1;
            }
        }
    }

    /// <summary>Appends the indexes of all cells in the set, ascending.</summary>
    public void CopyTo(List<int> indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        for (var word = 0; word < _bits.Length; word++)
        {
            var rest = _bits[word];
            while (rest != 0)
            {
                indexes.Add((word << 6) + BitOperations.TrailingZeroCount(rest));
                rest &= rest - 1;
            }
        }
    }

    /// <summary>Writes the set into the hasher (grid size, then the bits in index order).</summary>
    public void WriteTo(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(Width).Write(Depth);
        foreach (var word in _bits)
        {
            hasher.Write(word);
        }
    }

    /// <summary>The bits as bytes (64 cells per 8 bytes, little endian), for save games.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[_bits.Length * 8];
        for (var i = 0; i < _bits.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8), _bits[i]);
        }

        return bytes;
    }

    /// <summary>Reads a set written by <see cref="ToBytes"/> for a grid of the given size.</summary>
    public static CellSet FromBytes(int width, int depth, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var set = new CellSet(width, depth);
        if (bytes.Length != set._bits.Length * 8)
        {
            throw new InvalidDataException($"Seen cells: {bytes.Length} bytes do not fit a grid of {width} x {depth} cells.");
        }

        var cells = (long)width * depth;
        for (var i = 0; i < set._bits.Length; i++)
        {
            var word = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i * 8));
            var room = cells - ((long)i * 64);
            if (room < 64 && (word >> (int)room) != 0)
            {
                throw new InvalidDataException("Seen cells: bits beyond the grid.");
            }

            set._bits[i] = word;
            set.Count += BitOperations.PopCount(word);
        }

        return set;
    }
}
