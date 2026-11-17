using System.Text;
using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>The start parameters a world code carries (no name: the name stays the player's own).</summary>
/// <param name="Seed">World seed.</param>
/// <param name="Size">Size option id.</param>
/// <param name="Climate">Climate option id.</param>
/// <param name="StartFavor">Start favour.</param>
/// <param name="Resources">A1854: resources option id; null = the data's default.</param>
/// <param name="StartPairs">A1854: start pairs; null = the data's default.</param>
/// <param name="Terrain">A1854: terrain option id; null = the data's default.</param>
/// <param name="Distribution">A1854: distribution option id; null = the data's default.</param>
public sealed record WorldStart(ulong Seed, string Size, string Climate, int StartFavor, string? Resources = null, int? StartPairs = null, string? Terrain = null, string? Distribution = null);

/// <summary>
/// Short world code to copy and share (B2534): seed, size, climate and start favour; the same code
/// gives the same start world. Format <c>GW-XXXX-XXXX-…</c> in Crockford base 32 (no I, L, O, U;
/// reading is forgiving: lower case, spaces, missing dashes, I/L as 1 and O as 0). Options are coded
/// by their position in the data; one byte hashes the option lists, so a code made with other option
/// data is refused instead of silently meaning another world (GW1 review #58 C2). A checksum catches
/// typos.
/// </summary>
public static class WorldCode
{
    /// <summary>
    /// Code format version (raise when the coded fields or option lists change meaning). Version 2 (A1854) adds
    /// resources, start pairs, terrain and distribution, so a shared code gives the same island (review GW2 #595);
    /// codes of version 1 still read, with the data's defaults for those.
    /// </summary>
    public const int Version = 2;

    private const int Version1 = 1;

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

        var resources = OptionalIndex((options.Resources ?? []).Select(r => r.Id), start.Resources, nameof(start.Resources));
        var pairs = OptionalIndex((options.StartPairs ?? []).Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)), start.StartPairs?.ToString(System.Globalization.CultureInfo.InvariantCulture), nameof(start.StartPairs));
        var terrain = OptionalIndex((options.Terrains ?? []).Select(t => t.Id), start.Terrain, nameof(start.Terrain));
        var distribution = OptionalIndex((options.Distributions ?? []).Select(d => d.Id), start.Distribution, nameof(start.Distribution));
        var bytes = new List<byte> { (byte)Version, OptionsHash(options, favor), (byte)size, (byte)climate, (byte)favorIndex, (byte)resources, (byte)pairs, (byte)terrain, (byte)distribution };
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
        var header = bytes is { Length: > 0 } && bytes[0] == Version ? 9 : 5;
        if (bytes is null || bytes.Length < header + 2 || (bytes[0] != Version && bytes[0] != Version1) || Checksum(bytes.AsSpan(0, bytes.Length - 1).ToArray()) != bytes[^1]
            || bytes[1] != (bytes[0] == Version ? OptionsHash(options, favor) : OptionsHashVersion1(options, favor)))
        {
            return false;
        }

        ulong seed = 0;
        var shift = 0;
        var i = header;
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

        if (i != bytes.Length - 2 || bytes[2] >= options.Sizes.Count || bytes[3] >= options.Climates.Count || bytes[4] >= favor.StartOptions.Count)
        {
            return false;
        }

        start = new WorldStart(seed, options.Sizes.ElementAt(bytes[2]).Id, options.Climates.ElementAt(bytes[3]).Id, favor.StartOptions.ElementAt(bytes[4]));
        if (header == 9)
        {
            string? Pick(IEnumerable<string>? ids, byte index) => index == 255 ? null : ids?.ElementAtOrDefault(index);
            var resources = Pick(options.Resources?.Select(r => r.Id), bytes[5]);
            var pairs = Pick(options.StartPairs?.Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)), bytes[6]);
            var terrain = Pick(options.Terrains?.Select(t => t.Id), bytes[7]);
            var distribution = Pick(options.Distributions?.Select(d => d.Id), bytes[8]);
            if ((bytes[5] != 255 && resources is null) || (bytes[6] != 255 && pairs is null) || (bytes[7] != 255 && terrain is null) || (bytes[8] != 255 && distribution is null))
            {
                start = null;
                return false;
            }

            start = start with { Resources = resources, StartPairs = pairs is null ? null : int.Parse(pairs, System.Globalization.CultureInfo.InvariantCulture), Terrain = terrain, Distribution = distribution };
        }

        return true;
    }

    /// <summary>One byte over the ordered option lists (sizes, climates, start favours; since version 2 also resources, pairs, terrains, distributions).</summary>
    public static byte OptionsHash(WorldOptions options, FavorSettings favor)
    {
        ArgumentNullException.ThrowIfNull(options);
        var text = Version1Text(options, favor) + "#" + string.Join('|', (options.Resources ?? []).Select(r => r.Id)) + "#"
            + string.Join('|', (options.StartPairs ?? []).Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "#"
            + string.Join('|', (options.Terrains ?? []).Select(t => t.Id)) + "#" + string.Join('|', (options.Distributions ?? []).Select(d => d.Id))

            // The defaults too (review GW2 #605): 'not chosen' (255) means them, so other defaults must not read the code.
            + "#" + options.DefaultResources + "|" + options.DefaultStartPairs?.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + options.DefaultTerrain + "|" + options.DefaultDistribution;
        return Checksum(Encoding.UTF8.GetBytes(text));
    }

    private static byte OptionsHashVersion1(WorldOptions options, FavorSettings favor) => Checksum(Encoding.UTF8.GetBytes(Version1Text(options, favor)));

    private static string Version1Text(WorldOptions options, FavorSettings favor)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(favor);
        return string.Join('|', options.Sizes.Select(s => s.Id)) + "#" + string.Join('|', options.Climates.Select(c => c.Id)) + "#"
            + string.Join('|', favor.StartOptions.Select(f => f.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static int IndexOf(IEnumerable<string> ids, string id, string what)
    {
        var index = ids.ToList().IndexOf(id);
        return index >= 0 && index < 256 ? index : throw new ArgumentException($"{what} '{id}' is not an option in the data.", what);
    }

    /// <summary>A1854: an optional option not chosen (null = the data's default when the world is made) is coded as 255.</summary>
    private static int OptionalIndex(IEnumerable<string> ids, string? id, string what)
    {
        if (id is null)
        {
            return 255;
        }

        var index = IndexOf(ids, id, what);
        return index < 255 ? index : throw new ArgumentException($"{what} '{id}' is beyond the code's range.", what);
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

        // A1854: the leftover bits of the last character are padding and must be zero - else a typo there would go unseen.
        return (buffer & ((1 << bits) - 1)) == 0 ? [.. bytes] : null;
    }
}
