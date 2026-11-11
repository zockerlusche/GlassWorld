using System.Text;
using System.Text.Json;

namespace GlassWorld.Contracts.Validation;

/// <summary>
/// The only way the game and the core read or write contract messages: JSON is validated against
/// the schema of the target type before deserializing, and after serializing. The schema is found by
/// name: <c>DecisionRequest</c> → <c>decision-request</c>.
/// <para>
/// What comes in is always validated (AI output is data). What the core sends is validated in tests and
/// check runs and not in the running game (ADR-0009): the caller says so with
/// <see cref="Serialize{T}(T, bool)"/>.
/// </para>
/// </summary>
public static class ContractSerializer
{
    /// <summary>Validates <paramref name="json"/> and deserializes it.</summary>
    /// <exception cref="ContractViolationException">The JSON does not satisfy the schema.</exception>
    public static T Deserialize<T>(string json) where T : class => (T)Deserialize(json, typeof(T));

    /// <summary>Validates <paramref name="json"/> and deserializes it into <paramref name="type"/>.</summary>
    /// <exception cref="ContractViolationException">The JSON does not satisfy the schema.</exception>
    public static object Deserialize(string json, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var schema = SchemaNameOf(type);
        var errors = ContractValidator.Validate(schema, json);
        if (errors.Count > 0)
        {
            throw new ContractViolationException(schema, errors);
        }

        return JsonSerializer.Deserialize(json, type)
            ?? throw new ContractViolationException(schema, ["Null document"]);
    }

    /// <summary>Serializes <paramref name="value"/> and validates the result.</summary>
    /// <exception cref="ContractViolationException">The value does not satisfy the schema.</exception>
    public static string Serialize<T>(T value) where T : class
    {
        var schema = SchemaNameOf(typeof(T));
        var json = JsonSerializer.Serialize(value);
        var errors = ContractValidator.Validate(schema, json);
        return errors.Count > 0 ? throw new ContractViolationException(schema, errors) : json;
    }

    /// <summary>
    /// Serializes <paramref name="value"/>; validates the result only when <paramref name="validate"/> is
    /// true. For the requests the core builds from its own typed data (ADR-0009): validating one costs
    /// more than the rest of a simulation step, so the running game leaves it to the tests, the check runs
    /// and the service, which validates everything it receives.
    /// </summary>
    /// <exception cref="ContractViolationException">The value does not satisfy the schema (only when validating).</exception>
    public static string Serialize<T>(T value, bool validate) where T : class =>
        validate ? Serialize(value) : JsonSerializer.Serialize(value);

    /// <summary>Schema name for a generated root type, e.g. <c>HealthStatus</c> → <c>health-status</c>.</summary>
    public static string SchemaNameOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var name = new StringBuilder();
        foreach (var c in type.Name)
        {
            if (char.IsUpper(c) && name.Length > 0)
            {
                name.Append('-');
            }

            name.Append(char.ToLowerInvariant(c));
        }

        return name.ToString();
    }
}
