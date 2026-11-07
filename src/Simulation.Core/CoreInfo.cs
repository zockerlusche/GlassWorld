namespace GlassWorld.Simulation;

/// <summary>
/// Placeholder for the simulation core (Phase 0). Replaced by real types in B1.
/// </summary>
public static class CoreInfo
{
    /// <summary>
    /// Gets the name of the core assembly, used by smoke tests to prove the core loads.
    /// </summary>
    public static string AssemblyName => typeof(CoreInfo).Assembly.GetName().Name ?? string.Empty;
}
