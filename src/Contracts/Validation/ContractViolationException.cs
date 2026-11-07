namespace GlassWorld.Contracts.Validation;

/// <summary>A message does not satisfy its contract schema.</summary>
public sealed class ContractViolationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ContractViolationException()
        : this("unknown", [])
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ContractViolationException(string message)
        : base(message)
    {
        Schema = "unknown";
        Errors = [];
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public ContractViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Schema = "unknown";
        Errors = [];
    }

    /// <summary>Creates the exception for a schema and its validation errors.</summary>
    public ContractViolationException(string schema, IReadOnlyList<string> errors)
        : base($"Contract '{schema}' violated: {string.Join("; ", errors ?? [])}")
    {
        Schema = schema;
        Errors = errors ?? [];
    }

    /// <summary>Schema name, e.g. <c>decision-response</c>.</summary>
    public string Schema { get; }

    /// <summary>Validation errors (kind and JSON path).</summary>
    public IReadOnlyList<string> Errors { get; }
}
