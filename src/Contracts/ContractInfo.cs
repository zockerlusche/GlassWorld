namespace GlassWorld.Contracts;

/// <summary>
/// The contract version every message and data file carries (<c>contract_version</c>). It must match
/// the <c>ContractVersion</c> constant in contracts/schemas/common.schema.json; a test checks that.
/// </summary>
public static class ContractInfo
{
    /// <summary>Current contract version (0.2: knowledge, memory, relationships, jobs; B4a/B7).</summary>
    public const string Version = "0.2";
}
