using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GlassWorld.Simulation.State;

/// <summary>
/// Canonical binary writer for the simulation state: fixed little-endian encoding, length-prefixed
/// UTF-8 strings, doubles by their IEEE bits, and a section tag before every part. Callers must write
/// collections in a defined order (sorted by id). The SHA-256 of the written bytes is the state hash
/// (ADR-0002): equal state ⇔ equal hash, on every machine.
/// </summary>
public sealed class StateHasher : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly byte[] _buffer = new byte[8];

    /// <summary>Starts a named section (makes the hash sensitive to structure, not only values).</summary>
    public StateHasher Section(string name) => Write("§" + name);

    /// <summary>Writes a 64-bit integer.</summary>
    public StateHasher Write(long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(_buffer, value);
        _hash.AppendData(_buffer, 0, 8);
        return this;
    }

    /// <summary>Writes an unsigned 64-bit integer.</summary>
    public StateHasher Write(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer, value);
        _hash.AppendData(_buffer, 0, 8);
        return this;
    }

    /// <summary>Writes a 32-bit integer.</summary>
    public StateHasher Write(int value) => Write((long)value);

    /// <summary>Writes a boolean.</summary>
    public StateHasher Write(bool value) => Write(value ? 1L : 0L);

    /// <summary>Writes a double by its exact IEEE 754 bits (no formatting, no culture).</summary>
    public StateHasher Write(double value) => Write(BitConverter.DoubleToInt64Bits(value));

    /// <summary>Writes a string as length-prefixed UTF-8.</summary>
    public StateHasher Write(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        Write(bytes.Length);
        _hash.AppendData(bytes);
        return this;
    }

    /// <summary>Returns the hash as lowercase hex.</summary>
    public string Finish() => Convert.ToHexStringLower(_hash.GetHashAndReset());

    /// <inheritdoc/>
    public void Dispose() => _hash.Dispose();
}
