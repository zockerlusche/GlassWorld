using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GlassWorld.Simulation.Objects;

/// <summary>
/// The canonical signature of a processing try and the world rule id from it (B2537, contract
/// <c>ProcessSignature</c>): every property list deduplicated and sorted ordinally, the inputs sorted by
/// their lists joined with ',', no tool = null, keys sorted, no spaces, UTF-8; the id is 'world_' plus the
/// first 16 hex digits of its sha256. The same as the service's <c>world_rule_id</c> (test vectors in
/// contracts/testvectors/process-signature.json), so core and service name one try the same.
/// </summary>
public static class ProcessSignatures
{
    /// <summary>The process of a signature for a combine trial (B2687 K2): the inhabitant chose no process.</summary>
    public const string CombineTrial = "combine";

    /// <summary>The inputs in canonical order: each list deduplicated and sorted, the lists sorted by their joined text.</summary>
    public static List<List<string>> CanonicalInputs(IEnumerable<IEnumerable<string>> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return [.. inputs
            .Select(i => i.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList())
            .OrderBy(i => string.Join(',', i), StringComparer.Ordinal)];
    }

    /// <summary>Canonical JSON of a signature.</summary>
    public static string Canonical(string process, IEnumerable<IEnumerable<string>> inputs, IEnumerable<string>? tool, string? heatLevel)
    {
        ArgumentNullException.ThrowIfNull(process);
        var toolList = tool?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            if (heatLevel is null)
            {
                writer.WriteNull("heat_level");
            }
            else
            {
                writer.WriteString("heat_level", heatLevel);
            }

            writer.WriteStartArray("inputs");
            foreach (var input in CanonicalInputs(inputs))
            {
                writer.WriteStartArray();
                foreach (var property in input)
                {
                    writer.WriteStringValue(property);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteString("process", process);
            if (toolList is not { Count: > 0 })
            {
                writer.WriteNull("tool");
            }
            else
            {
                writer.WriteStartArray("tool");
                foreach (var property in toolList)
                {
                    writer.WriteStringValue(property);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The world rule id of a signature: 'world_' + the first 16 hex digits of sha256(canonical JSON).</summary>
    public static string RuleId(string process, IEnumerable<IEnumerable<string>> inputs, IEnumerable<string>? tool, string? heatLevel)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(process, inputs, tool, heatLevel)));
        return "world_" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}
