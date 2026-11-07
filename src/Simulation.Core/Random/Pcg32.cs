namespace GlassWorld.Simulation.Random;

/// <summary>
/// PCG32 (XSH RR, 64-bit state, 32-bit output) by Melissa O'Neill, https://www.pcg-random.org.
/// The core uses its own generator instead of <c>System.Random</c>, whose sequence is not guaranteed
/// across .NET versions; the sequence is pinned by reference test vectors (ADR-0002).
/// </summary>
public sealed class Pcg32
{
    private const ulong Multiplier = 6364136223846793005UL;

    private ulong _state;
    private readonly ulong _increment;

    /// <summary>Creates a generator like the reference <c>pcg32_srandom_r(initState, initSequence)</c>.</summary>
    public Pcg32(ulong initState, ulong initSequence)
    {
        _state = 0;
        _increment = (initSequence << 1) | 1UL;
        NextUInt();
        _state += initState;
        NextUInt();
    }

    private Pcg32(PcgState state)
    {
        _state = state.State;
        _increment = state.Increment;
    }

    /// <summary>Restores a generator from a saved state.</summary>
    public static Pcg32 FromState(PcgState state) => new(state);

    /// <summary>The complete generator state (for saving and hashing).</summary>
    public PcgState State => new(_state, _increment);

    /// <summary>Next uniformly distributed 32-bit value.</summary>
    public uint NextUInt()
    {
        var old = _state;
        _state = unchecked((old * Multiplier) + _increment);
        var xorShifted = (uint)(((old >> 18) ^ old) >> 27);
        var rotation = (int)(old >> 59);
        return (xorShifted >> rotation) | (xorShifted << ((-rotation) & 31));
    }

    /// <summary>Uniform integer in [0, <paramref name="bound"/>) without modulo bias (reference bounded rand).</summary>
    public uint NextUInt(uint bound)
    {
        ArgumentOutOfRangeException.ThrowIfZero(bound);
        var threshold = unchecked((uint)-bound) % bound;
        while (true)
        {
            var value = NextUInt();
            if (value >= threshold)
            {
                return value % bound;
            }
        }
    }

    /// <summary>Uniform integer in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(minInclusive, maxExclusive);
        return minInclusive + (int)NextUInt((uint)(maxExclusive - minInclusive));
    }

    /// <summary>Uniform double in [0, 1) with 53 random bits.</summary>
    public double NextDouble()
    {
        var bits = ((ulong)NextUInt() << 21) ^ (NextUInt() >> 11);
        return (bits & ((1UL << 53) - 1)) * (1.0 / (1UL << 53));
    }
}

/// <summary>Saved state of a <see cref="Pcg32"/>.</summary>
/// <param name="State">Internal 64-bit state.</param>
/// <param name="Increment">Stream increment (always odd).</param>
public readonly record struct PcgState(ulong State, ulong Increment);
