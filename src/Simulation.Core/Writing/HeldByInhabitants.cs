using GlassWorld.Simulation.Inhabitants;
using GlassWorld.Simulation.Objects;

namespace GlassWorld.Simulation.Writing;

/// <summary>A1357 part 2: what an inhabitant holds in its hand (the hold slot, B2540), as type ids for the tool check.</summary>
public sealed class HeldByInhabitants(InhabitantsSystem inhabitants, WorldObjects objects) : IHeldItems
{
    /// <inheritdoc/>
    public IReadOnlyList<string> HeldTypes(string inhabitantId) =>
        inhabitants.TryGet(inhabitantId, out var who) && who.Held is { } held && objects.TryGet(held, out var thing) ? [thing.TypeId] : [];
}
