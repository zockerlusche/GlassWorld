using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;

namespace GlassWorld.Simulation.Tests;

/// <summary>
/// Cross-checks of the look and animation data (B2 R19–R29, acceptance 4) against the vocabularies
/// and the calendar. The schemas check each file alone; these tests check the references.
/// </summary>
public sealed class LookContentTests
{
    private static T Read<T>(params string[] path)
        where T : class =>
        ContractSerializer.Deserialize<T>(File.ReadAllText(Path.Combine([TestData.Root, "content", .. path])));

    private static HashSet<string> Vocabulary(string file) =>
        Read<Vocabulary>("vocabulary", file).Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Seasons() =>
        TestData.Calendar().Settings.Seasons.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void EveryActionAndExpressionHasAnAnimation()
    {
        var library = Read<AnimationLibrary>("animation", "library.json");
        var mapped = library.Mappings.Select(m => (m.Kind, m.Id)).ToHashSet();
        var missing = Vocabulary("actions.json").Where(a => !mapped.Contains((AnimationMappingKind.Action, a)))
            .Concat(Vocabulary("expressions.json").Where(e => !mapped.Contains((AnimationMappingKind.Expression, e))))
            .ToList();
        Assert.True(missing.Count == 0, $"no animation for: {string.Join(", ", missing)}");
    }

    [Fact]
    public void AnimationReferencesExist()
    {
        var library = Read<AnimationLibrary>("animation", "library.json");
        var clips = library.Clips.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var used = library.Mappings.Select(m => m.Clip)
            .Concat(library.Gaits.Select(g => g.Clip))
            .Concat(library.Idle.Select(i => i.Clip))
            .Append(library.FallbackClip);
        Assert.All(used, c => Assert.Contains(c, clips));
        Assert.Equal(library.Clips.Count, clips.Count);

        var coreValues = Vocabulary("needs.json").Concat(Vocabulary("moods.json")).Concat(Vocabulary("traits.json")).ToHashSet(StringComparer.Ordinal);
        Assert.All(library.Idle.SelectMany(i => i.Factors), f => Assert.Contains(f.Value, coreValues));
        var speeds = library.Gaits.Select(g => g.MinSpeedCmS).ToList();
        Assert.Equal(speeds.Order(), speeds);
    }

    /// <summary>
    /// Coverage of the figure clips (B2 R27): every clip taken from the figure asset exists in the GLB
    /// (the importer drops "_Loop"), no clip that shows violence is used (B2466), and the clips still to be
    /// made are listed by the test output.
    /// </summary>
    [Fact]
    public void AssetClipsExistInTheFigureGlbAndShowNoViolence()
    {
        var library = Read<AnimationLibrary>("animation", "library.json");
        var glb = File.ReadAllBytes(Path.Combine(TestData.Root, "game", "assets", "characters", "quaternius-ual1", "ual1_standard.glb"));
        var jsonLength = BitConverter.ToInt32(glb, 12);
        using var document = System.Text.Json.JsonDocument.Parse(glb.AsMemory(20, jsonLength));
        var available = document.RootElement.GetProperty("animations").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString()!.Replace("_Loop", string.Empty, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var assets = library.Clips.Where(c => c.Source == AnimationClipSource.Asset).ToList();
        Assert.NotEmpty(assets);
        Assert.All(assets, c => Assert.True(c.Asset is not null && available.Contains(c.Asset), $"clip '{c.Id}' names '{c.Asset}', not in the GLB"));
        string[] violent = ["Death", "Pistol", "Sword", "Punch", "Hit_"];
        Assert.All(assets, c => Assert.DoesNotContain(violent, v => c.Asset!.StartsWith(v, StringComparison.Ordinal)));
        Assert.All(library.Clips.Where(c => c.Source != AnimationClipSource.Asset), c => Assert.Null(c.Asset));
        var fallback = Assert.Single(library.Clips, c => c.Id == library.FallbackClip);
        Assert.Equal(AnimationClipSource.Asset, fallback.Source);
    }

    [Fact]
    public void EpochsMatchTheVocabularyInOrderOfTechLevel()
    {
        var epochs = Read<LookEpochs>("look", "epochs.json").Epochs.ToList();
        Assert.Equal(Vocabulary("epochs.json").Order(StringComparer.Ordinal), epochs.Select(e => e.Id).Order(StringComparer.Ordinal));
        Assert.Equal(epochs.Select(e => e.TechLevel).Order(), epochs.Select(e => e.TechLevel));
        var palette = Read<LookPalette>("look", "palette.json").Colors.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(epochs.SelectMany(e => e.Colors), c => Assert.Contains(c.Id, palette));
    }

    [Fact]
    public void PaletteSeasonsAndDaylightPhasesCoverTheCalendar()
    {
        var palette = Read<LookPalette>("look", "palette.json");
        var ids = palette.Colors.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(Seasons().Order(StringComparer.Ordinal), palette.Seasons.Select(s => s.Season).Order(StringComparer.Ordinal));
        Assert.All(palette.Seasons.SelectMany(s => s.Colors), c => Assert.Contains(c.Id, ids));
        var ramp = palette.Ramp.Select(r => r.Threshold).ToList();
        Assert.Equal(ramp.Order(), ramp);

        var phases = Read<LookDaylight>("look", "daylight.json").Phases.Select(p => p.Phase);
        Assert.Equal(Vocabulary("time_of_day.json").Order(StringComparer.Ordinal), phases.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void QualityDefaultExistsAndDetailDistancesRise()
    {
        var quality = Read<LookQuality>("look", "quality.json");
        Assert.Contains(quality.DefaultLevel, quality.Levels.Select(l => l.Id));
        foreach (var level in quality.Levels)
        {
            var d = level.DetailDistancesM.ToList();
            Assert.Equal(d.Order(), d);
        }

        var camera = Read<LookCamera>("look", "camera.json");
        Assert.InRange(camera.DistanceM.Start, camera.DistanceM.Min, camera.DistanceM.Max);
    }
}
