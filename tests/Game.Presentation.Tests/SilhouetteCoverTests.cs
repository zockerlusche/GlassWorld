namespace GlassWorld.Presentation.Tests;

/// <summary>
/// A1569 (D843 a, review GW3 #147): the see-through silhouette of an inhabitant shows only behind trees, rocks and
/// objects. Every opaque surface marks its pixels in the stencil buffer - figures 1, ground and water 2, cover 3 -
/// and the silhouette draws only where it is 3. The shaders cannot run here; this pins the agreement in their text.
/// </summary>
public sealed class SilhouetteCoverTests
{
    [Theory]
    [InlineData("figure_silhouette.gdshader", "stencil_mode read, compare_equal, 3;")]
    [InlineData("figure_comic.gdshader", "stencil_mode write, compare_always, 1;")]
    [InlineData("warm_comic_ground.gdshader", "stencil_mode write, compare_always, 2;")]
    [InlineData("sea.gdshader", "stencil_mode write, compare_always, 2;")]
    [InlineData("stream.gdshader", "stencil_mode write, compare_always, 2;")]
    [InlineData("island_water.gdshader", "stencil_mode write, compare_always, 2;")]
    [InlineData("warm_comic.gdshader", "stencil_mode write, compare_always, 3;")]
    public void EverySurfaceSaysWhetherItIsCover(string shader, string line) =>
        Assert.Contains(line, File.ReadAllText(Path.Combine(TestPaths.Root, "game", "shaders", shader)), StringComparison.Ordinal);

    [Fact]
    public void EveryShaderTheGameLoadsIsInTheAgreement()
    {
        // Review GW3 #299: a new shader for things that forgets its stencil line could never be cover - it must be
        // listed here, as one that marks its pixels or as one that writes nothing on purpose.
        string[] marking = ["figure_silhouette", "figure_comic", "warm_comic_ground", "sea", "stream", "island_water", "warm_comic"];
        string[] silent = ["outline_hull", "warm_comic_fade", "niche_inside", "cloud_cover", "island_sky"];
        var scripts = Directory.GetFiles(Path.Combine(TestPaths.Root, "game", "scripts"), "*.cs", SearchOption.AllDirectories);
        var loaded = scripts
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"res://shaders/(?<name>[a-z_]+)\.gdshader").Select(m => m.Groups["name"].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(loaded);
        Assert.All(loaded, name => Assert.True(marking.Contains(name) || silent.Contains(name), $"{name}.gdshader is not in the stencil agreement (A1569)"));
    }

    [Fact]
    public void TheGroundIsDrawnWithItsOwnShader()
    {
        // The ground used the cover shader before: a figure behind a ridge glowed through the hill.
        var view = File.ReadAllText(Path.Combine(TestPaths.Root, "game", "scripts", "World", "IslandView.cs"));
        Assert.Contains("res://shaders/warm_comic_ground.gdshader", view, StringComparison.Ordinal);
        Assert.Contains("_groundMaterial = new ShaderMaterial { Shader = Ground }", view, StringComparison.Ordinal);
    }
}
