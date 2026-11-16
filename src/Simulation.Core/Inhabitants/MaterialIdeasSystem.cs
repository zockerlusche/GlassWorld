using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Simulation.Behaviour;
using GlassWorld.Simulation.Night;
using GlassWorld.Simulation.Objects;
using GlassWorld.Simulation.State;

namespace GlassWorld.Simulation.Inhabitants;

/// <summary>
/// A1581 (Marcel W2243 "wood in front of me - what could I make of it?", D1212 c): when an inhabitant finishes one of the
/// data's actions on a thing of a material kind it knows something about, an idea may sprout - "what could I make from
/// it?" - rooted on that type, in its own ideas (the store the nightly look back fills; no new memory, D1203). It grows
/// there like any idea and may ripen into a wish to invent (B2687). How often follows curiosity and drive (data curves);
/// whether it sprouts is a roll from the world seed, the inhabitant, the type and the day, so a replay and a loaded world
/// do the same. Never twice for the same material, at most the data's number a day. It holds no state of its own: what
/// it did is in the ideas.
/// </summary>
public sealed class MaterialIdeasSystem : IWorldSystem
{
    /// <summary>Detail of the idea event of an idea that sprouted from material seen by day.</summary>
    public const string Seen = "seen";

    private readonly KnowledgeMaterialIdeas _data;
    private readonly KnowledgeReview _review;
    private readonly InhabitantsSystem _inhabitants;
    private readonly WorldObjects _objects;
    private readonly HashSet<string> _actions;
    private readonly List<(string Who, string TargetId)> _pending = [];

    /// <summary>Creates the system.</summary>
    public MaterialIdeasSystem(KnowledgeMaterialIdeas data, KnowledgeReview review, InhabitantsSystem inhabitants, WorldObjects objects)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _review = review ?? throw new ArgumentNullException(nameof(review));
        _inhabitants = inhabitants ?? throw new ArgumentNullException(nameof(inhabitants));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _actions = [.. data.Actions];
    }

    /// <inheritdoc/>
    public string Name => "material_ideas";

    /// <summary>An inhabitant event: a finished action of the data on a thing is looked at in this tick's step.</summary>
    public void Observe(WorldEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Kind == InhabitantsSystem.ActionFinished && e.Action is { } action && _actions.Contains(action) && e.TargetId is { } target)
        {
            _pending.Add((e.InhabitantId, target));
        }
    }

    /// <inheritdoc/>
    public void Advance(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        foreach (var (who, targetId) in _pending)
        {
            if (_inhabitants.TryGet(who, out var inhabitant) && _objects.TryGet(targetId, out var thing))
            {
                Sprout(world, inhabitant, thing.TypeId);
            }
        }

        _pending.Clear();
    }

    /// <inheritdoc/>
    public void WriteState(StateHasher hasher)
    {
    }

    private void Sprout(World world, Inhabitant inhabitant, string typeId)
    {
        if (!_data.Properties.Any(p => _objects.Catalog.HasProperty(typeId, p))
            || !inhabitant.Beliefs.About(typeId).Any()
            || inhabitant.Ideas.Ideas.Any(i => i.Roots.Any(r => r.Kind == IdeaRootKinds.Type && r.Id == typeId)))
        {
            return;
        }

        var ticksPerDay = world.Calendar.TicksPerDay;
        var day = world.Tick / ticksPerDay;
        var prefix = _data.Text.Split("{type}")[0];
        if (inhabitant.Ideas.Ideas.Count(i => i.Tick / ticksPerDay == day && i.Text.StartsWith(prefix, StringComparison.Ordinal)) >= _data.MaxPerDay)
        {
            return;
        }

        var chance = _data.Chance;
        foreach (var curve in _data.ChanceByTrait ?? [])
        {
            chance *= TraitFactor.Of(curve, inhabitant.Traits);
        }

        if (Roll(world.Seed, inhabitant.Id, typeId, day) >= chance)
        {
            return;
        }

        // D1086 (B2728): with more drive an idea ripens faster - the same growth the nightly look back gives.
        var grow = _review.IdeaGrow * TraitFactor.Of(_review.IdeaGrowByTrait, inhabitant.Traits);
        var text = _data.Text.Replace("{type}", typeId, StringComparison.Ordinal);
        var idea = inhabitant.Ideas.Take(null, text, [new IdeaRoot(IdeaRootKinds.Type, typeId)], world.Tick, _review.IdeaStart, grow, _review.MaxIdeas);
        _inhabitants.Report(new WorldEvent(NightJobsSystem.IdeaEvent, world.Tick, inhabitant.Id, null, idea.Id) { Detail = Seen });
    }

    /// <summary>A number in 0..1 from the world seed, the inhabitant, the type and the day (FNV-1a; no random stream used).</summary>
    private static double Roll(ulong seed, string inhabitantId, string typeId, long day)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{seed}|material_idea|{inhabitantId}|{typeId}|{day}");
        var hash = 14695981039346656037UL;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        return (hash >> 11) * (1.0 / (1UL << 53));
    }
}
