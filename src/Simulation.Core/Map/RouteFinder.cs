using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Map;

/// <summary>A route in world positions (metres): cell centres, the last one the exact destination.</summary>
/// <param name="Status">Whether a route was found.</param>
/// <param name="Waypoints">Positions to walk to in order (empty unless found).</param>
public sealed record Route(PathStatus Status, IReadOnlyList<Position> Waypoints);

/// <summary>
/// Walking on a map for the inhabitants (B2 R10): routes around obstacles and deep water, and how fast
/// one walks at a place (shallow water and slopes are slower). Worlds without a map walk straight.
/// </summary>
public interface IRouteFinder
{
    /// <summary>Finds a route from <paramref name="start"/> to <paramref name="destination"/>.</summary>
    Route Find(Position start, Position destination);

    /// <summary>Speed factor at a position (1 = flat meadow).</summary>
    double SpeedFactor(Position at);

    /// <summary>
    /// Whether two positions lie in the same walkable area (cheap, no search): what lies beyond deep water
    /// or cliffs is not a goal for walking.
    /// </summary>
    bool Connected(Position a, Position b);
}
