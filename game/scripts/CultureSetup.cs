using System.Globalization;
using System.Runtime.CompilerServices;

namespace GlassWorld.Game;

/// <summary>
/// Godot starts the .NET runtime itself, so <c>InvariantGlobalization</c> from the project file
/// does not apply and the Windows culture leaks in (e.g. "500,5"). Logs, protocols, prompts and
/// state hashes must not depend on the player's system language (ADR-0002), so the invariant
/// culture is set before any game code runs. Player-facing text is localized via keys (ADR-0007).
/// </summary>
internal static class CultureSetup
{
    // The game assembly is the application under Godot's host, so a module initializer is the
    // earliest hook that runs before any node script.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void UseInvariantCulture()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}
