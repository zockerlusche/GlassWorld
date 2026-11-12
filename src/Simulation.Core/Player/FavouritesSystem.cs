using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Player;

/// <summary>The saved favourites: the ids of the inhabitants the player marked, in id order.</summary>
/// <param name="InhabitantIds">Marked inhabitants.</param>
public sealed record FavouritesState(IReadOnlyList<string> InhabitantIds);

/// <summary>
/// The player's favourites (B6 R19, B2518, Marcel W1603): inhabitants the player marked to keep an eye on and to
/// remember. They belong to the player alone - nothing in the world reads them and they never reach the AI (no
/// request carries them). Marking is a logged player command, so a replay marks the same at the same tick; a
/// favourite stays marked when the inhabitant has died (its memories remain, B8 R16).
/// </summary>
public sealed class FavouritesSystem : IWorldSystem, IOptionalWorldState
{
    /// <summary>Command kind: the player marks an inhabitant as a favourite (InhabitantId).</summary>
    public const string Mark = "mark_favourite";

    /// <summary>Command kind: the player takes the mark away again (InhabitantId).</summary>
    public const string Unmark = "unmark_favourite";

    private readonly InhabitantsSystem _inhabitants;
    private readonly SortedSet<string> _ids = new(StringComparer.Ordinal);

    /// <summary>Creates the favourites of a world.</summary>
    public FavouritesSystem(InhabitantsSystem inhabitants) => _inhabitants = inhabitants ?? throw new ArgumentNullException(nameof(inhabitants));

    /// <inheritdoc/>
    public string Name => "favourites";

    /// <inheritdoc/>
    public bool HasState => _ids.Count > 0;

    /// <summary>The marked inhabitants, in id order.</summary>
    public IReadOnlyList<string> All => [.. _ids];

    /// <summary>Whether the player marked the inhabitant.</summary>
    public bool IsFavourite(string inhabitantId) => _ids.Contains(inhabitantId);

    /// <summary>Checks a mark or unmark command: it must name an inhabitant of the world (marking twice is no error).</summary>
    public CommandResult Check(PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var known = command.InhabitantId is { } id && (_ids.Contains(id) || _inhabitants.All.Any(i => i.Id == id));
        return known ? new CommandResult(true, null) : new CommandResult(false, DecisionsSystem.ReasonNoInhabitant);
    }

    /// <summary>Applies a checked mark or unmark command.</summary>
    public void Apply(World world, PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.InhabitantId is not { } id)
        {
            return;
        }

        if (command.Kind == Mark)
        {
            _ids.Add(id);
        }
        else if (command.Kind == Unmark)
        {
            _ids.Remove(id);
        }
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        // Nothing happens by itself: only the player marks.
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_ids.Count);
        foreach (var id in _ids)
        {
            hasher.Write(id);
        }
    }

    /// <summary>The state to save.</summary>
    public FavouritesState Capture() => new([.. _ids]);

    /// <summary>Restores a saved state (null = a save game from before favourites, or none marked: empty).</summary>
    public void Restore(FavouritesState? state)
    {
        _ids.Clear();
        if (state is null)
        {
            return;
        }

        if (state.InhabitantIds.Any(string.IsNullOrEmpty))
        {
            throw new InvalidDataException("Save game favourites: an empty inhabitant id.");
        }

        _ids.UnionWith(state.InhabitantIds);
    }
}
