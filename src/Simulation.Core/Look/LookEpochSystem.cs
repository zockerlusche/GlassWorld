using GlassWorld.Contracts;
using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Look;

/// <summary>
/// The look epoch of the world (B2-5 R16, name "look_epoch"): which epoch of content/look/epochs.json the
/// presentation shows. Core state, so it is saved, replayed and the same for everybody; the epoch itself
/// changes only palette and details in the presentation (B2501).
/// <list type="bullet">
/// <item>A world starts in the first epoch of the data. While it is there the system stays out of the world
/// hash, so worlds that never change the epoch keep their hashes.</item>
/// <item>Command <see cref="SetLookEpoch"/>: the epoch must be in the data and its <c>tech_level</c> must
/// not exceed the reached tech level. The epochs count the tech levels as whole numbers: the reached level
/// is the position of the world's tech level id in the vocabulary <c>tech_level</c> (first = 0).</item>
/// <item>Command <see cref="DebugSetLookEpoch"/>: any epoch of the data, but only while the host runs with
/// debug commands (<see cref="SimulationHostOptions.DebugCommands"/>); otherwise it is refused at submit.
/// A logged one is applied as logged, so a replay follows without the debug switch.</item>
/// </list>
/// The code names no epoch and no tech level.
/// </summary>
public sealed class LookEpochSystem : IWorldSystem, IOptionalWorldState
{
    /// <summary>
    /// Command kind: set the look epoch (TypeId = epoch id), checked against the reached tech level. Any
    /// reached epoch is accepted, also an earlier one than the current, and it costs nothing. Who may send
    /// it and whether the look may go back is decided with the trigger from the tech level (B4b R12).
    /// </summary>
    public const string SetLookEpoch = "set_look_epoch";

    /// <summary>Command kind: set the look epoch without the tech level check (TypeId = epoch id); debug mode only.</summary>
    public const string DebugSetLookEpoch = "debug_set_look_epoch";

    /// <summary>Event: the look epoch changed (Detail = the new epoch, no inhabitant).</summary>
    public const string EpochChanged = "look_epoch_changed";

    /// <summary>Reason: the epoch is not in content/look/epochs.json.</summary>
    public const string ReasonUnknownEpoch = "msg.command.unknown_epoch";

    /// <summary>Reason: the epoch needs a higher tech level than the world has reached.</summary>
    public const string ReasonNotReached = "msg.command.epoch_not_reached";

    /// <summary>Reason: debug commands are off.</summary>
    public const string ReasonDebugOff = "msg.command.debug_off";

    private readonly Dictionary<string, LookEpoch> _epochs;
    private readonly bool _debugCommands;
    private readonly Action<WorldEvent> _report;

    /// <summary>Creates the system in the first epoch of the data.</summary>
    /// <param name="epochs">Epochs (content/look/epochs.json, checked at load).</param>
    /// <param name="reachedTechLevel">Reached tech level as a number: position of the tech level id in its vocabulary.</param>
    /// <param name="debugCommands">Whether the debug command is allowed.</param>
    /// <param name="report">Where the change event goes.</param>
    public LookEpochSystem(LookEpochs epochs, int reachedTechLevel, bool debugCommands, Action<WorldEvent> report)
    {
        ArgumentNullException.ThrowIfNull(epochs);
        if (epochs.Epochs.Count == 0)
        {
            throw new InvalidDataException("Look epochs: at least one epoch is needed.");
        }

        _epochs = epochs.Epochs.ToDictionary(e => e.Id, StringComparer.Ordinal);
        _debugCommands = debugCommands;
        _report = report ?? throw new ArgumentNullException(nameof(report));
        ReachedTechLevel = reachedTechLevel;
        First = epochs.Epochs.First().Id;
        Current = First;
    }

    /// <inheritdoc/>
    public string Name => "look_epoch";

    /// <summary>The epoch a new world starts in (the first of the data).</summary>
    public string First { get; }

    /// <summary>The current epoch id.</summary>
    public string Current { get; private set; }

    /// <summary>The reached tech level as the epochs count it (position in the vocabulary <c>tech_level</c>).</summary>
    public int ReachedTechLevel { get; }

    /// <inheritdoc/>
    public bool HasState => !string.Equals(Current, First, StringComparison.Ordinal);

    /// <summary>Raised when the epoch changes (old, new, tick).</summary>
    public event Action<string, string, long>? Changed;

    /// <summary>Whether an epoch of the data is within the reached tech level.</summary>
    public bool IsReached(string epoch) => epoch is not null && _epochs.TryGetValue(epoch, out var data) && data.TechLevel <= ReachedTechLevel;

    /// <summary>Checks <see cref="SetLookEpoch"/>: a known epoch within the reached tech level.</summary>
    public CommandResult Check(PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.TypeId is not { } epoch || !_epochs.ContainsKey(epoch))
        {
            return new CommandResult(false, ReasonUnknownEpoch);
        }

        return IsReached(epoch) ? new CommandResult(true, null) : new CommandResult(false, ReasonNotReached);
    }

    /// <summary>Applies a checked <see cref="SetLookEpoch"/> at the tick it takes effect.</summary>
    public void Apply(World world, PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(command);
        if (Check(command).Accepted)
        {
            Set(command.TypeId!, world.Tick);
        }
    }

    /// <summary>Checks <see cref="DebugSetLookEpoch"/>: debug commands on and a known epoch (no tech level check).</summary>
    public CommandResult CheckDebug(PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_debugCommands)
        {
            return new CommandResult(false, ReasonDebugOff);
        }

        return command.TypeId is { } epoch && _epochs.ContainsKey(epoch) ? new CommandResult(true, null) : new CommandResult(false, ReasonUnknownEpoch);
    }

    /// <summary>
    /// Applies a <see cref="DebugSetLookEpoch"/> from the queue or the log. The debug switch is checked at
    /// submit only: what is in the log happened, and a replay must follow it whatever its own switch says.
    /// </summary>
    public void ApplyDebug(World world, PlayerCommand command)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(command);
        if (command.TypeId is { } epoch && _epochs.ContainsKey(epoch))
        {
            Set(epoch, world.Tick);
        }
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        // The epoch changes by commands only (the trigger from the tech level follows with B4b).
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(Current);
    }

    /// <summary>The epoch to save: null while the world is in its first epoch.</summary>
    public string? Capture() => HasState ? Current : null;

    /// <summary>Restores a saved epoch; null (the first epoch, and save games from before) gives the first epoch.</summary>
    public void Restore(string? epoch)
    {
        if (epoch is not null && !_epochs.ContainsKey(epoch))
        {
            throw new InvalidDataException($"Save game look epoch '{epoch}' is not in content/look/epochs.json.");
        }

        Current = epoch ?? First;
    }

    private void Set(string epoch, long tick)
    {
        if (string.Equals(epoch, Current, StringComparison.Ordinal))
        {
            return;
        }

        var old = Current;
        Current = epoch;
        _report(new WorldEvent(EpochChanged, tick, string.Empty, null, null) { Detail = epoch });
        Changed?.Invoke(old, epoch, tick);
    }
}
