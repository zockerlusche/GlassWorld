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
    public void TheGroundIsDrawnWithItsOwnShader()
    {
        // The ground used the cover shader before: a figure behind a ridge glowed through the hill.
        var view = File.ReadAllText(Path.Combine(TestPaths.Root, "game", "scripts", "World", "IslandView.cs"));
        Assert.Contains("res://shaders/warm_comic_ground.gdshader", view, StringComparison.Ordinal);
        Assert.Contains("_groundMaterial = new ShaderMaterial { Shader = Ground }", view, StringComparison.Ordinal);
    }
}
