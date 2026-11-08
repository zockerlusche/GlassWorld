using GlassWorld.Contracts;

namespace GlassWorld.Presentation.World;

/// <summary>How the main game draws a world (TL decision #19505).</summary>
public enum WorldLookMode
{
    /// <summary>Flat placeholder start: capsules and spheres (the safe fallback).</summary>
    Placeholder,

    /// <summary>Generated island with the real figures and objects.</summary>
    Island,
}

/// <summary>
/// The world-look switch without Godot: <c>content/look/world.json</c> names the world of a new game,
/// the environment variable <see cref="EnvironmentVariable"/> overrides it for tests and start scripts.
/// A world that exists already is drawn the way it was made: one with an island map as island, one
/// without as placeholder (a placeholder world has no ground to stand on).
/// </summary>
public static class WorldLookChoice
{
    /// <summary>Environment variable that overrides the data value: <c>island</c> or <c>placeholder</c>.</summary>
    public const string EnvironmentVariable = "GLASSWORLD_WORLD";

    /// <summary>
    /// The world for a new game: the environment value if it is a known one, otherwise the data value.
    /// <paramref name="problem"/> names an unknown environment value (the caller logs it), else null.
    /// </summary>
    public static WorldLookMode ForNewWorld(LookWorld data, string? environment, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(data);
        problem = null;
        var fromData = data.World == LookWorldWorld.Island ? WorldLookMode.Island : WorldLookMode.Placeholder;
        if (string.IsNullOrWhiteSpace(environment))
        {
            return fromData;
        }

        switch (environment.Trim().ToUpperInvariant())
        {
            case "ISLAND":
                return WorldLookMode.Island;
            case "PLACEHOLDER":
                return WorldLookMode.Placeholder;
            default:
                problem = $"{EnvironmentVariable}='{environment}' is neither 'island' nor 'placeholder'; using '{data.World.ToString().ToLowerInvariant()}' from content/look/world.json";
                return fromData;
        }
    }

    /// <summary>How an existing world is drawn: as island exactly when it has an island map.</summary>
    public static WorldLookMode ForWorld(bool hasIslandMap) => hasIslandMap ? WorldLookMode.Island : WorldLookMode.Placeholder;
}
