using GlassWorld.Presentation.World;

namespace GlassWorld.Presentation.Tests;

/// <summary>ADR-0008: the presentation layer never references Godot.</summary>
public sealed class ArchitectureTests
{
    private static readonly string[] SourceFolders = ["game", "src"];

    [Fact]
    public void PresentationDoesNotReferenceGodot()
    {
        var references = typeof(WorldFeed).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();
        Assert.DoesNotContain(references, name => name.StartsWith("Godot", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("GlassWorld.Simulation.Core", references);
    }

    /// <summary>
    /// The projects run with InvariantGlobalization (Directory.Build.props, determinism): creating a culture by
    /// name throws CultureNotFoundException in exported builds. Sorting and formatting use the invariant culture
    /// or ordinal comparison; texts come from the translation server.
    /// </summary>
    [Fact]
    public void NoCodeCreatesACultureByName()
    {
        var root = TestPaths.Root;
        var offenders = SourceFolders
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f) is var text && (text.Contains("new CultureInfo(", StringComparison.Ordinal)
                || text.Contains("new System.Globalization.CultureInfo(", StringComparison.Ordinal)
                || text.Contains("CultureInfo.GetCultureInfo(", StringComparison.Ordinal)
                || text.Contains("CreateSpecificCulture(", StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.True(offenders.Count == 0, "creates a culture by name: " + string.Join(", ", offenders));
    }
}
