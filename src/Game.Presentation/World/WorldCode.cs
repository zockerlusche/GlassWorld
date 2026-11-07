using System.Text;
using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>The start parameters a world code carries (no name: the name stays the player's own).</summary>
/// <param name="Seed">World seed.</param>
/// <param name="Size">Size option id.</param>
/// <param name="Climate">Climate option id.</param>
/// <param name="StartFavor">Start favour.</param>
public sealed record WorldStart(ulong Seed, string Size, string Climate, int StartFavor);

/// <summary>
/// Short world code to copy and share (B2534): seed, size, climate and start favour; the same code
/// gives the same start world. Format <c>GW-XXXX-XXXX-…</c> in Crockford base 32 (no I, L, O, U;
/// reading is forgiving: lower case, spaces, missing dashes, I/L as 1 and O as 0). Options are coded
/// by their position in the data, so a code carries a format version; a checksum catches typos.
/// </summary>
public static class WorldCode
{
    /// <summary>Code format version (raise when the coded fields or option lists change meaning).</summary>
    public const int Version = 1;

    private const string Prefix = "GW";
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Encodes the start parameters.</summary>
    /// <exception cref="ArgumentException">An option is not in the data.</exception>
    public static string Encode(WorldStart start, WorldOptions options, FavorSettings favor)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(favor);
        var size = IndexOf(options.Sizes.Select(s => s.Id), start.Size, nameof(start.Size));
        var climate = IndexOf(options.Climates.Select(c => c.Id), start.Climate, nameof(start.Climate));
        var favorIndex = IndexOf(favor.StartOptions.Select(f => f.ToString(System.Globalization.CultureInfo.InvariantCulture)), start.StartFavor.ToString(System.Globalization.CultureInfo.InvariantCulture), nameof(start.StartFavor));

        var bytes = new List<byte> { (byte)Version, (byte)size, (byte)climate, (byte)favorIndex };
        var seed = start.Seed;
        do
        {
            // Varint: small seeds give short codes.
            var b = (byte)(seed & 0x7F);
            seed >>= 7;
            bytes.Add(seed == 0 ? b : (byte)(b | 0x80));
        }
        while (seed != 0);

        bytes.Add(Checksum(bytes));
        var text = ToBase32([.. bytes]);
        var groups = Enumerable.Range(0, (text.Length + 3) / 4).Select(i => text.Substring(i * 4, Math.Min(4, text.Length - (i * 4))));
        return Prefix + "-" + string.Join('-', groups);
    }

    /// <summary>Reads a code; false for typos, unknown versions or options the data does not have.</summary>
    public static bool TryDecode(string? code, WorldOptions options, FavorSettings favor, out WorldStart? start)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(favor);
        start = null;
        var text = Normalize(code);
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var bytes = FromBase32(text[Prefix.Length..]);
        if (bytes is null || bytes.Length < 6 || bytes[0] != Version || Checksum(bytes.AsSpan(0, bytes.Length - 1).ToArray()) != bytes[^1])
        {
            return false;
        }

        ulong seed = 0;
        var shift = 0;
        var i = 4;
        for (; i < bytes.Length - 1; i++)
        {
            if (shift > 63)
            {
                return false;
            }

            seed |= (ulong)(bytes[i] & 0x7F) << shift;
            shift += 7;
            if ((bytes[i] & 0x80) == 0)
            {
                break;
            }
        }

        if (i != bytes.Length - 2 || bytes[1] >= options.Sizes.Count || bytes[2] >= options.Climates.Count || bytes[3] >= favor.StartOptions.Count)
        {
            return false;
        }

        start = new WorldStart(seed, options.Sizes.ElementAt(bytes[1]).Id, options.Climates.ElementAt(bytes[2]).Id, favor.StartOptions.ElementAt(bytes[3]));
        return true;
    }

    private static int IndexOf(IEnumerable<string> ids, string id, string what)
    {
        var index = ids.ToList().IndexOf(id);
        return index >= 0 && index < 256 ? index : throw new ArgumentException($"{what} '{id}' is not an option in the data.", what);
    }

    private static byte Checksum(IEnumerable<byte> bytes)
    {
        // FNV-1a folded to one byte: catches single typos and swapped characters in practice.
        var hash = 2166136261u;
        foreach (var b in bytes)
        {
            hash = (hash ^ b) * 16777619u;
        }

        return (byte)(hash ^ (hash >> 8) ^ (hash >> 16) ^ (hash >> 24));
    }

    private static string? Normalize(string? code)
    {
        if (code is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var c in code.ToUpperInvariant())
        {
            var mapped = c switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                _ => c,
            };
            if (char.IsAsciiLetterOrDigit(mapped))
            {
                builder.Append(mapped);
            }
            else if (!char.IsWhiteSpace(mapped) && mapped != '-')
            {
                return null;
            }
        }

        // "GW" survives the I/L/O mapping because it contains none of them.
        return builder.ToString();
    }

    private static string ToBase32(byte[] bytes)
    {
        var builder = new StringBuilder();
        var buffer = 0;
        var bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            builder.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return builder.ToString();
    }

    private static byte[]? FromBase32(string text)
    {
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var c in text)
        {
            var value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
