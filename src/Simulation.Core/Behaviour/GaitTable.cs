using System.Collections.Frozen;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Inhabitants;

namespace GlassWorld.Simulation.Behaviour;

/// <summary>
/// How fast inhabitants walk and in which gait (content/behaviour/gaits.json, Marcel F5264 = A). The gait
/// of a walk follows from data only: a walk whose action serves a need below its neglect threshold uses
/// the pressing gait, an action named in the table its own gait, every other walk the default gait. The
/// code names no gait or action; the gait is derived from the state, never stored.
/// </summary>
public sealed class GaitTable
{
    private readonly FrozenDictionary<string, double> _metresPerTick;
    private readonly FrozenDictionary<string, string> _actionGaits;
    private readonly FrozenDictionary<string, string> _actionNeeds;
    private readonly FrozenDictionary<string, double> _neglectBelow;
    private readonly string? _pressingGait;

    private GaitTable(GaitSettings data, FrozenDictionary<string, string> actionNeeds, FrozenDictionary<string, double> neglectBelow)
    {
        Data = data;
        _metresPerTick = data.Gaits.ToFrozenDictionary(g => g.Id, g => data.WalkMPerTick * g.FactorOfWalk, StringComparer.Ordinal);
        _actionGaits = data.ActionGaits.ToFrozenDictionary(a => a.Action, a => a.Gait, StringComparer.Ordinal);
        _actionNeeds = actionNeeds;
        _neglectBelow = neglectBelow;
        _pressingGait = data.PressingNeedGait;
    }

    /// <summary>The data in use.</summary>
    public GaitSettings Data { get; }

    /// <summary>Gait ids in data order.</summary>
    public IEnumerable<string> Gaits => Data.Gaits.Select(g => g.Id);

    /// <summary>
    /// Builds the table and checks it: gait ids unique, every named gait defined, actions known and named
    /// once. The need of an action is the need of its first utility consideration; its threshold is the
    /// need's neglect_below in the inhabitant model (0 = never pressing).
    /// </summary>
    public static GaitTable Create(GaitSettings data, IReadOnlySet<string> actions, UtilityWeights weights, InhabitantModel model)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(model);
        var problems = new List<string>();
        var ids = data.Gaits.Select(g => g.Id).ToList();
        problems.AddRange(ids.GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"gait '{g.Key}' twice"));
        var named = new[] { data.DefaultGait, data.PressingNeedGait }.OfType<string>().Concat(data.ActionGaits.Select(a => a.Gait));
        problems.AddRange(named.Where(g => !ids.Contains(g, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).Select(g => $"unknown gait '{g}'"));
        problems.AddRange(data.ActionGaits.Where(a => !actions.Contains(a.Action)).Select(a => $"unknown action '{a.Action}'"));
        problems.AddRange(data.ActionGaits.GroupBy(a => a.Action, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"action '{g.Key}' named twice"));
        if (problems.Count > 0)
        {
            throw new InvalidDataException("Gaits: " + string.Join("; ", problems) + ".");
        }

        var actionNeeds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var consideration in weights.Considerations.Where(c => c.Need is not null))
        {
            actionNeeds.TryAdd(consideration.Action, consideration.Need!);
        }

        var neglectBelow = model.Needs.Where(n => n.NeglectBelow > 0).ToFrozenDictionary(n => n.Id, n => n.NeglectBelow, StringComparer.Ordinal);
        return new GaitTable(data, actionNeeds.ToFrozenDictionary(StringComparer.Ordinal), neglectBelow);
    }

    /// <summary>Metres per tick of a gait on flat ground (the map's speed factor slows it further).</summary>
    public double MetresPerTick(string gait) =>
        _metresPerTick.TryGetValue(gait, out var metres) ? metres : throw new KeyNotFoundException($"No gait '{gait}'.");

    /// <summary>The gait of a walk to perform <paramref name="action"/>, by the rules of the data.</summary>
    public string GaitFor(Inhabitant inhabitant, string action)
    {
        ArgumentNullException.ThrowIfNull(inhabitant);
        if (_pressingGait is { } pressing
            && _actionNeeds.TryGetValue(action, out var need)
            && _neglectBelow.TryGetValue(need, out var below)
            && inhabitant.Needs.GetValueOrDefault(need, 1.0) < below)
        {
            return pressing;
        }

        return _actionGaits.TryGetValue(action, out var gait) ? gait : Data.DefaultGait;
    }
}
