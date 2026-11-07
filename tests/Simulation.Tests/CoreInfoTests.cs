namespace GlassWorld.Simulation.Tests;

public sealed class CoreInfoTests
{
    [Fact]
    public void CoreAssemblyLoads()
    {
        Assert.Equal("GlassWorld.Simulation.Core", CoreInfo.AssemblyName);
    }

    [Fact]
    public void CoreHasNoGodotReference()
    {
        var references = typeof(CoreInfo).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, r => r.Name is not null && r.Name.StartsWith("Godot", StringComparison.Ordinal));
    }
}
