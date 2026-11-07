using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>ADR-0008: the presentation layer never references Godot.</summary>
public sealed class ArchitectureTests
{
    [Fact]
    public void PresentationDoesNotReferenceGodot()
    {
        var references = typeof(WorldFeed).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();
        Assert.DoesNotContain(references, name => name.StartsWith("Godot", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("GlassWorld.Simulation.Core", references);
    }
}
