using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>Something that happened in a tick (for the UI, sound, chronicle and tests).</summary>
/// <param name="Kind">Event kind (language-neutral id).</param>
/// <param name="Tick">Tick at which it happened.</param>
/// <param name="InhabitantId">Inhabitant involved.</param>
/// <param name="Action">Action id, if any.</param>
/// <param name="TargetId">Target object id, if any.</param>
public sealed record WorldEvent(string Kind, long Tick, string InhabitantId, string? Action, string? TargetId);

/// <summary>
/// All inhabitants (a world system): needs decay, moods follow drivers, the will to live sinks only after
/// long neglect (B2484), and the routine layer chooses and performs actions whose effects come from the
/// TRUE properties of the target. Experience updates beliefs (the frame for learning, extended in B4a).
/// Every number comes from data; the code names no need, mood or action.
/// </summary>
public sealed class InhabitantsSystem : IWorldSystem
{
    /// <summary>Event: an action started (after walking to the target).</summary>
    public const string ActionStarted = "action_started";

    /// <summary>Event: an action finished and its effects applied.</summary>
    public const string ActionFinished = "action_finished";

    /// <summary>Event: an action was cancelled (target gone).</summary>
    public const string ActionCancelled = "action_cancelled";

    private readonly SortedDictionary<string, Inhabitant> _inhabitants = new(StringComparer.Ordinal);
    private readonly List<WorldEvent> _events = [];
    private readonly InhabitantModel _model;
    private readonly ActionRules _actions;
    private readonly UtilityBrain _brain;
    private readonly WorldObjects _objects;
    private readonly int _ticksPerHour;

    /// <summary>Creates the system.</summary>
    public InhabitantsSystem(InhabitantModel model, ActionRules actions, UtilityBrain brain, WorldObjects objects, int ticksPerHour)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(ticksPerHour, 1);
        _model = model;
        _actions = actions;
        _brain = brain;
        _objects = objects;
        _ticksPerHour = ticksPerHour;
    }

    /// <inheritdoc/>
    public string Name => "inhabitants";

    /// <summary>All inhabitants in id order.</summary>
    public IEnumerable<Inhabitant> All => _inhabitants.Values;

    /// <summary>Events of the last tick.</summary>
    public IReadOnlyList<WorldEvent> Events => _events;

    /// <summary>
    /// Creates the start inhabitants from the world seed (B2503): traits and start needs are drawn from
    /// the data ranges with one stream per inhabitant, so the same seed always gives the same people.
    /// </summary>
    public void SpawnStartInhabitants(World world, IReadOnlyList<Position> positions)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(positions);
        if (positions.Count < _model.Start.Count)
        {
            throw new ArgumentException($"Need {_model.Start.Count} start positions.", nameof(positions));
        }

        for (var i = 0; i < _model.Start.Count; i++)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"inh-{i + 1:000000}");
            var rng = world.Random.Get($"inhabitant/{id}/start");
            var traits = _model.Start.Traits.ToDictionary(t => t.Id, t => t.Min + ((t.Max - t.Min) * rng.NextDouble()), StringComparer.Ordinal);
            var needs = _model.Needs.ToDictionary(n => n.Id, _ => 1.0, StringComparer.Ordinal);
            foreach (var range in _model.Start.Needs)
            {
                needs[range.Id] = range.Min + ((range.Max - range.Min) * rng.NextDouble());
            }

            var moods = _model.Moods.ToDictionary(m => m.Id, m => m.Baseline, StringComparer.Ordinal);
            Add(new Inhabitant(id, positions[i], needs, moods, traits, _model.WillToLive.Start));
        }
    }

    /// <summary>Adds an inhabitant (start, tests, save games).</summary>
    public void Add(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        _inhabitants.Add(inhabitant.Id, inhabitant);
    }

    /// <summary>Looks up an inhabitant.</summary>
    public Inhabitant Get(string id) => _inhabitants[id];

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _events.Clear();
        foreach (var inhabitant in _inhabitants.Values)
        {
            DecayNeeds(inhabitant);
            UpdateMoods(inhabitant);
            UpdateWillToLive(inhabitant);
            Act(world, inhabitant);
        }
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        hasher.Write(_inhabitants.Count);
        foreach (var inhabitant in _inhabitants.Values)
        {
            inhabitant.WriteState(hasher);
        }
    }

    /// <summary>True if the will to live is below the "fading" threshold (consequences in B9).</summary>
    public bool IsFading(Inhabitant inhabitant)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        return inhabitant.WillToLive < _model.WillToLive.FadingBelow;
    }

    private double PerTick(double perHour) => perHour / _ticksPerHour;

    private void DecayNeeds(Inhabitant inhabitant)
    {
        foreach (var need in _model.Needs)
        {
            if (inhabitant.Needs.TryGetValue(need.Id, out var value))
            {
                inhabitant.Needs[need.Id] = Math.Clamp(value - PerTick(need.DecayPerHour), 0.0, 1.0);
            }
        }
    }

    private void UpdateMoods(Inhabitant inhabitant)
    {
        foreach (var mood in _model.Moods)
        {
            var value = inhabitant.Moods.GetValueOrDefault(mood.Id, mood.Baseline);
            var step = PerTick(mood.ReturnPerHour);
            value = value > mood.Baseline ? Math.Max(mood.Baseline, value - step) : Math.Min(mood.Baseline, value + step);
            inhabitant.Moods[mood.Id] = value;
        }

        foreach (var driver in _model.MoodDrivers)
        {
            if (inhabitant.Needs.TryGetValue(driver.Need, out var need) && need < driver.Below && inhabitant.Moods.TryGetValue(driver.Mood, out var mood))
            {
                inhabitant.Moods[driver.Mood] = Math.Clamp(mood + PerTick(driver.DeltaPerHour), 0.0, 1.0);
            }
        }
    }

    private void UpdateWillToLive(Inhabitant inhabitant)
    {
        var w = _model.WillToLive;
        var neglected = _model.Needs.Any(n => n.NeglectBelow > 0 && inhabitant.Needs.GetValueOrDefault(n.Id, 1.0) < n.NeglectBelow);
        inhabitant.NeglectTicks = neglected ? inhabitant.NeglectTicks + 1 : 0;
        if (inhabitant.NeglectTicks > (long)w.NeglectHoursBeforeDecline * _ticksPerHour)
        {
            inhabitant.WillToLive = Math.Max(0.0, inhabitant.WillToLive - PerTick(w.DeclinePerHour));
        }
        else if (!neglected && inhabitant.WillToLive < w.Start)
        {
            inhabitant.WillToLive = Math.Min(w.Start, inhabitant.WillToLive + PerTick(w.RecoverPerHour));
        }
    }

    private void Act(World world, Inhabitant inhabitant)
    {
        inhabitant.Activity ??= Choose(world, inhabitant);
        var activity = inhabitant.Activity;
        if (activity is null)
        {
            return;
        }

        WorldObject? target = null;
        if (activity.TargetId is not null && !_objects.TryGet(activity.TargetId, out target))
        {
            _events.Add(new WorldEvent(ActionCancelled, world.Tick, inhabitant.Id, activity.Action, activity.TargetId));
            inhabitant.Activity = null;
            return;
        }

        if (activity.RemainingTicks is null)
        {
            var timing = _actions.Timing(activity.Action);
            var destination = target?.Position ?? activity.Destination;
            if (!MoveTowards(inhabitant, destination, timing.ReachM))
            {
                return;
            }

            inhabitant.Activity = activity with { RemainingTicks = timing.DurationTicks };
            _events.Add(new WorldEvent(ActionStarted, world.Tick, inhabitant.Id, activity.Action, activity.TargetId));
            return;
        }

        var remaining = activity.RemainingTicks.Value - 1;
        if (remaining > 0)
        {
            inhabitant.Activity = activity with { RemainingTicks = remaining };
            return;
        }

        Finish(world, inhabitant, activity, target);
        inhabitant.Activity = null;
    }

    private Activity? Choose(World world, Inhabitant inhabitant)
    {
        var radius = _brain.Weights.PerceptionRadiusM;
        var perceived = _objects.All.Where(o => DistanceSquared(inhabitant.Position, o.Position) <= radius * radius).ToList();
        var scored = _brain.Score(inhabitant, perceived);
        if (scored.Count == 0)
        {
            return null;
        }

        var best = scored[0];

        if (best.TargetId is not null && _objects.TryGet(best.TargetId, out var target))
        {
            return new Activity(best.Action, best.TargetId, target.Position, null);
        }

        // Actions without a target happen here or, if the data says so, at a random spot nearby
        // (drawn from the inhabitant's own stream).
        var destination = inhabitant.Position;
        if (_actions.Timing(best.Action).RandomNearby)
        {
            var rng = world.Random.Get($"inhabitant/{inhabitant.Id}/wander");
            destination = new Position(
                inhabitant.Position.X + (((rng.NextDouble() * 2.0) - 1.0) * radius),
                inhabitant.Position.Z + (((rng.NextDouble() * 2.0) - 1.0) * radius));
        }

        return new Activity(best.Action, null, destination, null);
    }

    private bool MoveTowards(Inhabitant inhabitant, Position destination, double reach)
    {
        var dx = destination.X - inhabitant.Position.X;
        var dz = destination.Z - inhabitant.Position.Z;
        var distance = Math.Sqrt((dx * dx) + (dz * dz));
        if (distance <= reach)
        {
            return true;
        }

        var step = _brain.Weights.MoveSpeedMPerMinute * 60.0 / _ticksPerHour;
        var travel = Math.Min(step, distance - reach);
        inhabitant.Position = new Position(inhabitant.Position.X + (dx * travel / distance), inhabitant.Position.Z + (dz * travel / distance));
        return distance - travel <= reach;
    }

    private void Finish(World world, Inhabitant inhabitant, Activity activity, WorldObject? target)
    {
        var applying = _actions.Applying(activity.Action, target?.TypeId);
        foreach (var effect in applying.SelectMany(r => r.Effects))
        {
            switch (effect.Kind)
            {
                case EffectKind.Need when inhabitant.Needs.TryGetValue(effect.Id, out var need):
                    inhabitant.Needs[effect.Id] = Math.Clamp(need + effect.Delta, 0.0, 1.0);
                    break;
                case EffectKind.Mood when inhabitant.Moods.TryGetValue(effect.Id, out var mood):
                    inhabitant.Moods[effect.Id] = Math.Clamp(mood + effect.Delta, 0.0, 1.0);
                    break;
                case EffectKind.Will_to_live:
                    inhabitant.WillToLive = Math.Clamp(inhabitant.WillToLive + effect.Delta, 0.0, 1.0);
                    break;
                default:
                    break;
            }
        }

        if (target is not null)
        {
            Learn(inhabitant, activity.Action, target.TypeId);
            if (applying.Any(r => r.ConsumesTarget))
            {
                _objects.Remove(target.Id);
            }
        }

        _events.Add(new WorldEvent(ActionFinished, world.Tick, inhabitant.Id, activity.Action, activity.TargetId));
    }

    /// <summary>
    /// Experience: for every rule of the action that depends on a property, the inhabitant now believes
    /// what really happened (the property was there or not). Confidence grows with repeated experience.
    /// </summary>
    private void Learn(Inhabitant inhabitant, string action, string typeId)
    {
        var step = _brain.Weights.LearningStep;
        foreach (var property in _actions.Rules(action).Select(r => r.RequiresProperty).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var truth = _objects.Catalog.HasProperty(typeId, property);
            var confidence = inhabitant.Beliefs.TryGet(typeId, property, out var old) && old.Value == truth ? old.Confidence + step : step;
            inhabitant.Beliefs.Set(typeId, property, truth, confidence);
        }
    }

    private static double DistanceSquared(Position a, Position b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz);
    }
}
