namespace GlassWorld.Game.Prototype.Look;

/// <summary>Small deterministic generator for the look prototype (xorshift, presentation only).</summary>
public sealed class PrototypeRandom(ulong seed)
{
    private ulong _state = seed == 0 ? 0x9E3779B97F4A7C15 : seed;

    /// <summary>Next value in 0..max-1.</summary>
    public int Next(int max)
    {
        _state ^= _state << 13;
        _state ^= _state >> 7;
        _state ^= _state << 17;
        return (int)(_state % (ulong)max);
    }

    /// <summary>Next value in 0..1.</summary>
    public double NextUnit() => Next(1_000_000) / 1_000_000.0;
}
