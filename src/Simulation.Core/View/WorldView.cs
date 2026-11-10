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

    /// <summary>
    /// Gait id while the inhabitant walks (content/behaviour/gaits.json: stroll, walk, hurry, run …), null while
    /// it stands or performs its action; the presentation plays the animation gait of the same id (F5264).
    /// </summary>
    public string? Gait { get; init; }

    /// <summary>
    /// What an action aimed at another inhabitant is about (object type id of the belief it would pass on),
    /// null otherwise; the inhabitant list says "shows Toma the spring water" with it (W1687).
    /// </summary>
    public string? Topic { get; init; }

    /// <summary>Id of the inhabitant this one turns its whole body to (B2528, W1691), or null; see InhabitantsSystem.FacingOf.</summary>
    public string? Facing { get; init; }

    /// <summary>
    /// The open questions that still puzzle the inhabitant (TL #19921): after it has looked at a thing, a
    /// question about it leaves only the effect open and no longer counts for the question mark.
    /// </summary>
    public IReadOnlyList<string> PuzzlingQuestionIds { get; init; } = [];

    /// <summary>
    /// The inhabitant's own name (content/inhabitants/names.json, PR #171), shown by the list, card and logs
    /// (W1698); null only if it has none. Read-only copy for the presentation, not part of the state hash.
    /// </summary>
    public string? Name { get; init; }
}

/// <summary>Read-only picture of the world at one tick.</summary>
/// <param name="Time">World time.</param>
/// <param name="Inhabitants">Inhabitants in id order.</param>
/// <param name="Objects">Objects in id order.</param>
/// <param name="Events">Events of the most recent tick with events.</param>
public sealed record WorldSnapshot(WorldTime Time, IReadOnlyList<InhabitantSnapshot> Inhabitants, IReadOnlyList<WorldObject> Objects, IReadOnlyList<WorldEvent> Events)
{
    /// <summary>The weather now (B2 R13), or null without a weather system.</summary>
    public Weather.WeatherReading? Weather { get; init; }

    /// <summary>
    /// The tide now (B2-5 R15): water level against the mean sea level, for the water of the presentation;
    /// null on worlds without an island. With amplitude 0 the level is always 0.
    /// </summary>
    public Map.TideReading? Tide { get; init; }

    /// <summary>
    /// The look epoch of the world (id of content/look/epochs.json, B2-5 R16); the presentation blends palette
    /// and details to it. Null only in snapshots taken without a host.
    /// </summary>
    public string? LookEpoch { get; init; }

    /// <summary>
    /// Version of the player's view (B2-6 R14): the number of cells any inhabitant has seen so far. It only
    /// grows; the presentation compares it with the version it has drawn and reads the cells that came
    /// after from <see cref="Exploration.ExplorationSystem.RevealedSince"/>. Null on worlds without an island.
    /// </summary>
    public int? ExploredVersion { get; init; }
}

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
                Gait = inhabitants.GaitOf(i),
                Topic = inhabitants.TopicOf(i),
                Facing = inhabitants.FacingOf(i),
                PuzzlingQuestionIds = decisions.PuzzlingQuestionIds(i),
                Name = i.Name,
            })],
            [.. objects.All],
            [.. inhabitants.Events]);
    }
}
