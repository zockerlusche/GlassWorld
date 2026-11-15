using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Decisions;

/// <content>
/// A1778 (B2814, Marcel W2204): every action that fails at the core becomes an open question. The failures of a tick
/// are collected from the reported events (data: failures.event_kinds; the reason code is the event's detail) and
/// asked at the end of the same tick (<see cref="FailureQuestionsSystem"/>), so nothing waits across a save. A
/// reason that passes by itself (data: transient_reasons) and an event without a reason (the thing is gone) ask
/// nothing. The failed action rests on that target for the data's hours; the question - trigger action_failed -
/// carries the situation (action, kind of target, reason) and the core's usual options, never what would work (PR2).
/// Once per action, kind of target and reason within ask_again_after_hours.
/// </content>
public sealed partial class DecisionsSystem
{
    /// <summary>Trigger and occasion of a question about a failed action (A1778).</summary>
    public const string ActionFailed = "action_failed";

    /// <summary>Subject prefix of a failure question: <c>failed:action|target type or -|reason</c>.</summary>
    public const string FailedSubject = "failed:";

    private readonly List<(string InhabitantId, string Action, string? TargetId, string? TargetType, string Reason)> _failed = [];

    /// <summary>Collects a failed action from a reported event (subscribed by the host); asked in <see cref="AskFailures"/>.</summary>
    public void ObserveFailure(WorldEvent worldEvent)
    {
        ArgumentNullException.ThrowIfNull(worldEvent);
        if (_settings.Failures is not { } failures
            || worldEvent.Action is not { } action
            || worldEvent.Detail is not { Length: > 0 } detail
            || !failures.EventKinds.Contains(worldEvent.Kind)
            || !Enabled(ActionFailed))
        {
            return;
        }

        var slash = detail.IndexOf('/', StringComparison.Ordinal);
        var reason = slash < 0 ? detail : detail[..slash];
        if (failures.TransientReasons.Contains(reason))
        {
            return;
        }

        var targetType = worldEvent.TargetType
            ?? (worldEvent.TargetId is { } id && _objects.TryGet(id, out var thing) ? thing.TypeId : null);
        _failed.Add((worldEvent.InhabitantId, action, worldEvent.TargetId, targetType, reason));
    }

    /// <summary>The failures of this tick: the action rests on its target, and a question opens (once per kind of failure).</summary>
    public void AskFailures(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_failed.Count == 0 || _settings.Failures is not { } failures)
        {
            _failed.Clear();
            return;
        }

        var failed = _failed.ToList();
        _failed.Clear();
        var restTicks = (long)Math.Round(failures.RestHours * world.Calendar.Settings.TicksPerHour);
        foreach (var (inhabitantId, action, targetId, targetType, reason) in failed)
        {
            if (!_inhabitants.TryGet(inhabitantId, out var inhabitant))
            {
                continue;
            }

            if (restTicks > 0)
            {
                inhabitant.Resting.RemoveAll(r => r.Action == action && r.TargetId == targetId);
                inhabitant.Resting.Add(new RestingAction(action, targetId, inhabitant.Position, world.Tick + restTicks));
            }

            var target = targetId is { } id && _objects.TryGet(id, out var thing) ? thing : null;
            Ask(world, inhabitant, ActionFailed, FailedSubject + action + "|" + (targetType ?? "-") + "|" + reason, target);
        }
    }

    /// <summary>The situation of a failure question, read back from its subject; null for another subject.</summary>
    public static FailedAction? FailureOf(string subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (!subject.StartsWith(FailedSubject, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = subject[FailedSubject.Length..].Split('|');
        return parts.Length != 3 ? null : new FailedAction
        {
            Action = parts[0],
            TargetType = parts[1] == "-" ? null : parts[1],
            Reason = parts[2],
        };
    }
}

/// <summary>
/// A1778: asks the failures of a tick at its end - the last system of the world, so a failure reported by any
/// system in the tick is asked in the same tick and nothing waits across a save. Holds no state.
/// </summary>
/// <param name="decisions">The decider system that asks.</param>
public sealed class FailureQuestionsSystem(DecisionsSystem decisions) : IWorldSystem, IOptionalWorldState
{
    /// <inheritdoc/>
    public string Name => "failure_questions";

    /// <inheritdoc/>
    public bool HasState => false;

    /// <inheritdoc/>
    public void Advance(World world) => decisions.AskFailures(world);

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
    }
}
