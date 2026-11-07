using System.Text;

namespace GlassWorld.Simulation.Random;

/// <summary>
/// Named, independent random streams derived from the world seed. Every purpose gets its own stream
/// (e.g. <c>weather</c> or <c>inhabitant/inh-000001/mood</c>), so adding a new system never shifts the
/// sequence of another one (stable replays and old save games). Stream states are part of the save
/// game and the state hash.
/// </summary>
public sealed class RandomStreams
{
    private readonly SortedDictionary<string, Pcg32> _streams = new(StringComparer.Ordinal);

    /// <summary>Creates the streams for a world seed.</summary>
    public RandomStreams(ulong worldSeed) => WorldSeed = worldSeed;

    /// <summary>The world seed all streams derive from.</summary>
    public ulong WorldSeed { get; }

    /// <summary>Names of the streams created so far, in ordinal order.</summary>
    public IEnumerable<string> Names => _streams.Keys;

    /// <summary>Returns the stream for <paramref name="name"/>, creating it on first use.</summary>
    public Pcg32 Get(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!_streams.TryGetValue(name, out var stream))
        {
            stream = Create(WorldSeed, name);
            _streams.Add(name, stream);
        }

        return stream;
    }

    /// <summary>Snapshot of all stream states (ordinal by name).</summary>
    public IReadOnlyList<KeyValuePair<string, PcgState>> Snapshot() =>
        [.. _streams.Select(s => new KeyValuePair<string, PcgState>(s.Key, s.Value.State))];

    /// <summary>Restores streams from a snapshot (used when loading a save game).</summary>
    public static RandomStreams Restore(ulong worldSeed, IEnumerable<KeyValuePair<string, PcgState>> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var streams = new RandomStreams(worldSeed);
        foreach (var (name, state) in states)
        {
            streams._streams.Add(name, Pcg32.FromState(state));
        }

        return streams;
    }

    /// <summary>Derives the generator of a stream: SplitMix64 over world seed and FNV-1a(name).</summary>
    internal static Pcg32 Create(ulong worldSeed, string name)
    {
        var nameHash = Fnv1a64(name);
        var mix = worldSeed ^ nameHash;
        var initState = SplitMix64(ref mix);
        var initSequence = SplitMix64(ref mix);
        return new Pcg32(initState, initSequence);
    }

    /// <summary>SplitMix64 step (Steele, Lea, Flood 2014).</summary>
    internal static ulong SplitMix64(ref ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15UL;
            var z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>64-bit FNV-1a over the UTF-8 bytes (stable across processes, unlike string.GetHashCode).</summary>
    internal static ulong Fnv1a64(string text)
    {
        var hash = 0xCBF29CE484222325UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash = unchecked((hash ^ b) * 0x100000001B3UL);
        }

        return hash;
    }
}
