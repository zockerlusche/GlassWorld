using GlassWorld.Simulation;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Presentation.World;

/// <summary>One thing an inhabitant believes about a type of thing (its belief set): the property, yes or no, and where it has it from.</summary>
/// <param name="PropertyId">Property id (vocabulary <c>vocab.property.&lt;id&gt;</c>).</param>
/// <param name="Value">What it believes: the thing has the property (true) or not.</param>
/// <param name="Source">Where it has it from (<see cref="KnowledgeSources"/>: experienced, seen, heard, read, guessed).</param>
public sealed record ThingFact(string PropertyId, bool Value, string Source)
{
    /// <summary>Text key of where the belief comes from (<c>ui.thing.source.&lt;source&gt;</c>).</summary>
    public string SourceKey => "ui.thing.source." + Source;
}

/// <summary>What one inhabitant knows about a type of thing.</summary>
/// <param name="InhabitantId">The inhabitant.</param>
/// <param name="Facts">Its beliefs about the type, in property order.</param>
public sealed record ThingKnower(string InhabitantId, IReadOnlyList<ThingFact> Facts);

/// <summary>The ground at a place (A1791, TL #27543): its biome and how fast one walks there.</summary>
/// <param name="Biome">Biome id (vocabulary <c>vocab.biome.&lt;id&gt;</c>).</param>
/// <param name="SpeedFactor">Walking speed there, 1 = full speed (the core's walk cost, <c>WorldMapSystem.SpeedFactor</c>).</param>
public sealed record GroundInfo(string Biome, double SpeedFactor)
{
    /// <summary>Walking speed in whole percent of full speed.</summary>
    public int SpeedPercent => (int)Math.Round(SpeedFactor * 100, MidpointRounding.AwayFromZero);

    /// <summary>True when one walks slower here than on open ground.</summary>
    public bool Slower => SpeedPercent < 100;
}

/// <summary>
/// A1791 (Marcel W2242, D1192 #5/#24): what the player learns by clicking a thing in the world - its neutral name (the
/// type), who built it or invented its kind, what each inhabitant believes about it and where from, and the ground it stands
/// on. Read only from the core (the screen never writes); "who knows what" is the inhabitants' beliefs, never the world's truth.
/// </summary>
/// <param name="ObjectId">The thing.</param>
/// <param name="TypeId">Its type (the neutral name; an invention by its own name).</param>
/// <param name="BuilderId">Who built this very thing, or null.</param>
/// <param name="InventorId">Who invented its kind, or null (natural things).</param>
/// <param name="Knowers">The inhabitants that believe something about its type, in inhabitant order.</param>
/// <param name="Ground">The ground under it, or null without a map.</param>
public sealed record ThingInfo(string ObjectId, string TypeId, string? BuilderId, string? InventorId, IReadOnlyList<ThingKnower> Knowers, GroundInfo? Ground)
{
    /// <summary>
    /// The thing nearest to a point on the ground within <paramref name="radiusM"/> metres (ties by id), or null - what a
    /// click into the world means when it hit no inhabitant.
    /// </summary>
    public static string? Nearest(IEnumerable<WorldObject> things, double x, double z, double radiusM)
    {
        ArgumentNullException.ThrowIfNull(things);
        string? best = null;
        var bestDistance = radiusM * radiusM;
        foreach (var thing in things)
        {
            var dx = thing.Position.X - x;
            var dz = thing.Position.Z - z;
            var distance = (dx * dx) + (dz * dz);
            if (distance < bestDistance || (distance == bestDistance && best is not null && string.CompareOrdinal(thing.Id, best) < 0))
            {
                best = thing.Id;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>The info of a thing in the running world, or null when it is gone.</summary>
    public static ThingInfo? Of(SimulationHost host, string objectId)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!host.Objects.TryGet(objectId, out var thing))
        {
            return null;
        }

        var builder = host.Inventions.Builds.FirstOrDefault(b => b.BuiltObjectId == objectId)?.InhabitantId;
        var inventor = host.Inventions.Invention(thing.TypeId)?.InhabitantId;
        var knowers = host.Inhabitants.All
            .Where(i => i.Beliefs.Knows(thing.TypeId))
            .Select(i => new ThingKnower(i.Id, [.. i.Beliefs.About(thing.TypeId).Select(f => new ThingFact(f.PropertyId, f.Belief.Value, f.Belief.Source))]))
            .Where(k => k.Facts.Count > 0)
            .ToList();
        return new ThingInfo(objectId, thing.TypeId, builder, inventor, knowers, GroundAt(host, thing.Position));
    }

    /// <summary>The ground at a place, or null without a map or outside it.</summary>
    public static GroundInfo? GroundAt(SimulationHost host, Position at)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.Map is { } map && map.CellOf(at) is { } cell ? new GroundInfo(map.Map.BiomeAt(cell.X, cell.Z), map.SpeedFactor(at)) : null;
    }
}
