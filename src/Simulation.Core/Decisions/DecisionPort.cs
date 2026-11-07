using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GlassWorld.Simulation.Decisions;

/// <summary>A message from the decider: a <c>DecisionResponse</c> or a <c>ServiceError</c> as raw JSON.</summary>
/// <param name="RequestId">Request the message answers.</param>
/// <param name="Json">Raw JSON; the core validates it before use.</param>
/// <param name="IsError">True for a <c>ServiceError</c>.</param>
public sealed record PortMessage(string RequestId, string Json, bool IsError);

/// <summary>
/// The core's only connection to the decider (ADR-0002/0006). Requests go out as validated JSON; answers
/// come back whenever they are ready (B2487: no fixed time). The core collects them at a tick boundary, so
/// the moment an answer takes effect is a tick, recorded in the <see cref="DecisionLog"/> for replays.
/// </summary>
public interface IDecisionPort
{
    /// <summary>Sends a request (JSON already validated against <c>decision-request</c>).</summary>
    void Send(string requestId, string requestJson);

    /// <summary>Messages that are ready at <paramref name="tick"/>, in arrival order.</summary>
    IReadOnlyList<PortMessage> Receive(long tick);
}

/// <summary>Deterministic ids for requests (no Guid.NewGuid in the core).</summary>
public static class DecisionIds
{
    /// <summary>
    /// A lowercase RFC 9562 UUIDv8 from SHA-256 over seed, tick, inhabitant and a counter. Same history
    /// → same ids, in every run and in replays.
    /// </summary>
    public static string RequestId(ulong worldSeed, long tick, string inhabitantId, long counter)
    {
        ArgumentNullException.ThrowIfNull(inhabitantId);
        var name = Encoding.UTF8.GetBytes(inhabitantId);
        var input = new byte[24 + name.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(0), worldSeed);
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(8), tick);
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(16), counter);
        name.CopyTo(input, 24);
        var hash = SHA256.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexStringLower(hash.AsSpan(0, 16));
        return string.Create(CultureInfo.InvariantCulture, $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}");
    }
}
