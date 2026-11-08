using System.Collections.Frozen;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Time;

namespace GlassWorld.Simulation.View;

/// <summary>Read-only picture of one inhabitant for the UI (no Godot types, ids only, texts via keys).</summary>
/// <param name="Id">Inhabitant id.</param>
/// <param name="Sex">female or male.</param>
/// <param name="Position">Position in metres.</param>
/// <param name="Needs">Needs 0..1 by id.</param>
/// <param name="Moods">Feelings 0..1 by id.</param>
/// <param name="Traits">Character traits 0..1 by id.</param>
/// <param name="WillToLive">Will to live 0..1.</param>
/// <param name="Fading">True below the fading threshold.</param>
/// <param name="Action">Current action id or null.</param>
/// <param name="TargetId">Current target object id or null.</param>
/// <param name="OpenQuestionIds">Open questions (ids only: the question window shows that, not what, B2487).</param>
public sealed record InhabitantSnapshot(
    string Id,
    string Sex,
    Position Position,
    FrozenDictionary<string, double> Needs,
    FrozenDictionary<string, double> Moods,
    FrozenDictionary<string, double> Traits,
    double WillToLive,
    bool Fading,
    string? Action,
    string? TargetId,
    IReadOnlyList<string> OpenQuestionIds)
{
    /// <summary>How the inhabitant looks (B2 R27): the game draws the figure from it, B8 voices read the body values.</summary>
    public Appearance? Appearance { get; init; }
}

/// <summary>Read-only picture of the world at one tick.</summary>
/// <param name="Time">World time.</param>
/// <param name="Inhabitants">Inhabitants in id order.</param>
/// <param name="Objects">Objects in id order.</param>
/// <param name="Events">Events of the most recent tick with events.</param>
public sealed record WorldSnapshot(WorldTime Time, IReadOnlyList<InhabitantSnapshot> Inhabitants, IReadOnlyList<WorldObject> Objects, IReadOnlyList<WorldEvent> Events);

/// <summary>
/// The UI's read port (R15a): copies of the core state that the presentation can keep and compare without
/// ever touching live simulation objects. Commands go the other way through
/// <see cref="DecisionsSystem.Submit"/>.
/// </summary>
public static class WorldView
{
    /// <summary>Takes a snapshot.</summary>
    public static WorldSnapshot Snapshot(World world, InhabitantsSystem inhabitants, WorldObjects objects, DecisionsSystem decisions)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(inhabitants);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(decisions);
        return new WorldSnapshot(
            world.Now,
            [.. inhabitants.All.Select(i => new InhabitantSnapshot(
                i.Id,
                i.Sex,
                i.Position,
                i.Needs.ToFrozenDictionary(StringComparer.Ordinal),
                i.Moods.ToFrozenDictionary(StringComparer.Ordinal),
                i.Traits.ToFrozenDictionary(StringComparer.Ordinal),
                i.WillToLive,
                inhabitants.IsFading(i),
                i.Activity?.Action,
                i.Activity?.TargetId,
                decisions.OpenQuestionIds(i.Id))
            {
                Appearance = i.Appearance,
            })],
            [.. objects.All],
            [.. inhabitants.Events]);
    }
}
