using GlassWorld.Simulation.Decisions;
using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.Random;

namespace GlassWorld.Simulation.State;

/// <summary>A belief as saved.</summary>
public sealed record SavedBelief(string TypeId, string PropertyId, bool Value, double Confidence);

/// <summary>An inhabitant as saved.</summary>
public sealed record SavedInhabitant(
    string Id,
    string Sex,
    int Group,
    Position Position,
    IReadOnlyDictionary<string, double> Needs,
    IReadOnlyDictionary<string, double> Moods,
    IReadOnlyDictionary<string, double> Traits,
    double WillToLive,
    long NeglectTicks,
    Activity? Activity,
    Activity? Planned,
    IReadOnlyList<SavedBelief> Beliefs);

/// <summary>An open question as saved.</summary>
public sealed record SavedQuestion(string Id, string InhabitantId, string Kind, string Subject, string RequestId, long AskedTick, IReadOnlyList<SavedOption> Options, long? RetryAtTick);

/// <summary>An offered option as saved.</summary>
public sealed record SavedOption(string OptionId, string Action, string? TargetId);

/// <summary>
/// The complete simulation state as plain data (no IO in the core). Simulation.Data stores it
/// in SQLite; restoring it gives exactly the same state hash (ADR-0003).
/// </summary>
public sealed record SaveState(
    ulong Seed,
    long Tick,
    IReadOnlyList<KeyValuePair<string, PcgState>> Streams,
    long NextObjectNumber,
    IReadOnlyList<WorldObject> Objects,
    IReadOnlyList<SavedInhabitant> Inhabitants,
    long QuestionCounter,
    IReadOnlyList<SavedQuestion> Questions,
    IReadOnlyList<KeyValuePair<string, long>> LastAsked,
    IReadOnlyList<DecisionLogEntry> DecisionLog,
    string? WorldPreset = null);

/// <summary>Captures and restores the complete state of a <see cref="SimulationHost"/>.</summary>
public static class StateCodec
{
    /// <summary>Takes the complete state.</summary>
    public static SaveState Capture(SimulationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var (counter, questions, lastAsked) = host.Decisions.CaptureState();
        return new SaveState(
            host.World.Seed,
            host.World.Tick,
            host.World.Random.Snapshot(),
            host.Objects.NextNumber,
            [.. host.Objects.All],
            [.. host.Inhabitants.All.Select(i => new SavedInhabitant(
                i.Id,
                i.Sex,
                i.Group,
                i.Position,
                new Dictionary<string, double>(i.Needs, StringComparer.Ordinal),
                new Dictionary<string, double>(i.Moods, StringComparer.Ordinal),
                new Dictionary<string, double>(i.Traits, StringComparer.Ordinal),
                i.WillToLive,
                i.NeglectTicks,
                i.Activity,
                i.Planned,
                [.. i.Beliefs.All.Select(b => new SavedBelief(b.TypeId, b.PropertyId, b.Belief.Value, b.Belief.Confidence))]))],
            counter,
            [.. questions.Select(q => new SavedQuestion(q.Id, q.InhabitantId, q.Kind, q.Subject, q.RequestId, q.AskedTick, [.. q.Options.Select(o => new SavedOption(o.OptionId, o.Action, o.TargetId))], q.RetryAtTick))],
            lastAsked,
            [.. host.Decisions.Log.Entries],
            host.Map?.Island.PresetId);
    }

    /// <summary>Rebuilds a simulation from a saved state with the given content and decider port.</summary>
    public static SimulationHost Restore(SimulationHostFactory factory, SaveState state)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(state);
        var world = World.Restore(state.Seed, factory.Content.Calendar, state.Tick, state.Streams);
        var host = factory.Assemble(world, state.WorldPreset);
        foreach (var obj in state.Objects)
        {
            host.Objects.Restore(obj, state.NextObjectNumber);
        }

        host.Objects.Restore(state.NextObjectNumber);
        foreach (var saved in state.Inhabitants)
        {
            var inhabitant = new Inhabitant(
                saved.Id,
                saved.Sex,
                saved.Group,
                saved.Position,
                new Dictionary<string, double>(saved.Needs, StringComparer.Ordinal),
                new Dictionary<string, double>(saved.Moods, StringComparer.Ordinal),
                new Dictionary<string, double>(saved.Traits, StringComparer.Ordinal),
                saved.WillToLive)
            {
                NeglectTicks = saved.NeglectTicks,
                Activity = saved.Activity,
                Planned = saved.Planned,
            };
            foreach (var belief in saved.Beliefs)
            {
                inhabitant.Beliefs.Set(belief.TypeId, belief.PropertyId, belief.Value, belief.Confidence);
            }

            host.Inhabitants.Add(inhabitant);
        }

        host.Decisions.RestoreState(
            state.QuestionCounter,
            state.Questions.Select(q => new OpenQuestion(q.Id, q.InhabitantId, q.Kind, q.Subject, q.RequestId, q.AskedTick, [.. q.Options.Select(o => (o.OptionId, o.Action, o.TargetId))], q.RetryAtTick)),
            state.LastAsked,
            state.DecisionLog);
        return host;
    }
}

/// <summary>Everything needed to assemble a simulation around a world (content, decider settings, port, locale).</summary>
public sealed class SimulationHostFactory(Content.ContentSet content, Contracts.DeciderSettings settings, IDecisionPort port, string displayLocale)
{
    /// <summary>The content.</summary>
    public Content.ContentSet Content { get; } = content;

    /// <summary>Assembles the systems around <paramref name="world"/>.</summary>
    public SimulationHost Assemble(World world) => SimulationHost.Assemble(Content, settings, world, port, displayLocale);

    /// <summary>Assembles the systems around <paramref name="world"/>, on the island of the preset if one is given.</summary>
    public SimulationHost Assemble(World world, string? worldPreset) => SimulationHost.Assemble(Content, settings, world, port, displayLocale, worldPreset);
}
