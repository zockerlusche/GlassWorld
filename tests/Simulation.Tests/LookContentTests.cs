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

    /// <summary>Words in GLB clip names that mean violence (B2466); never approved.</summary>
    private static readonly string[] Refused =
        ["Sword", "Spell", "Shoot", "Punch", "Kick", "Hit", "Death", "Dead", "Die", "Gun", "Pistol", "Rifle", "Bow", "Arrow",
         "Attack", "Roll", "Stab", "Slash", "Shield", "Fight", "Knife", "Melee", "Aim", "Reload"];

    private static bool IsRefused(string asset) => Refused.Any(w => asset.Contains(w, StringComparison.Ordinal));

    /// <summary>Clip names of a GLB (the importer drops "_Loop").</summary>
    private static HashSet<string> GlbClips(string file)
    {
        var glb = File.ReadAllBytes(file);
        var jsonLength = BitConverter.ToInt32(glb, 12);
        using var document = System.Text.Json.JsonDocument.Parse(glb.AsMemory(20, jsonLength));
        return document.RootElement.GetProperty("animations").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString()!.Replace("_Loop", string.Empty, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Coverage of the figure clips (B2 R27): the asset clips of the library are the allowlist of approved
    /// clips (the game plays no other); every clip the library uses is on it, every approved clip exists in
    /// every figure GLB that figures.json names, and no approved clip shows violence
    /// (B2466). The clips still to be made are the non-asset ones.
    /// </summary>
    [Fact]
    public void ApprovedClipsExistInEveryFigureGlbAndShowNoViolence()
    {
        var library = Read<AnimationLibrary>("animation", "library.json");
        var approved = library.Clips.Where(c => c.Source == AnimationClipSource.Asset).ToList();
        Assert.NotEmpty(approved);
        Assert.All(approved, c => Assert.False(c.Asset is null || IsRefused(c.Asset), $"clip '{c.Id}' names '{c.Asset}', not approvable"));
        Assert.All(library.Clips.Where(c => c.Source != AnimationClipSource.Asset), c => Assert.Null(c.Asset));
        var fallback = Assert.Single(library.Clips, c => c.Id == library.FallbackClip);
        Assert.Equal(AnimationClipSource.Asset, fallback.Source);
        Assert.All(library.Gaits, g => Assert.Contains(approved, c => c.Id == g.Clip));

        var models = Read<LookFigures>("look", "figures.json").Bodies.Select(b => b.Model).Distinct(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(models);
        var first = File.ReadAllBytes(Path.Combine(TestData.Root, "game", "assets", "characters", "glassworld", models[0] + ".glb"));
        Assert.SkipWhen(
            first.Length > 7 && System.Text.Encoding.ASCII.GetString(first, 0, 7) == "version",
            "LFS files not checked out; the game smoke run plays the clips (ISLAND_OK figures=N).");
        foreach (var model in models)
        {
            var available = GlbClips(Path.Combine(TestData.Root, "game", "assets", "characters", "glassworld", model + ".glb"));
            Assert.All(approved, c => Assert.True(available.Contains(c.Asset!), $"clip '{c.Id}' names '{c.Asset}', not in {model}.glb"));

            // The check itself: every violent clip the figure GLB brings is refused.
            Assert.All(available.Where(a => a.StartsWith("Sword", StringComparison.Ordinal) || a.StartsWith("Spell", StringComparison.Ordinal)
                || a.StartsWith("Pistol", StringComparison.Ordinal) || a.StartsWith("Punch", StringComparison.Ordinal)
                || a.StartsWith("Hit", StringComparison.Ordinal) || a.StartsWith("Death", StringComparison.Ordinal)
                || a.StartsWith("Roll", StringComparison.Ordinal)), a => Assert.True(IsRefused(a), a));
        }
    }

    [Theory]
    [InlineData("Idle_Sword", true)]
    [InlineData("Spell_Simple_Shoot", true)]
    [InlineData("Spell_Simple_Idle", true)]
    [InlineData("Roll_RM", true)]
    [InlineData("Kick_Low", true)]
    [InlineData("Bow_Draw", true)]
    [InlineData("Walk_Formal", false)]
    [InlineData("Jog_Fwd", false)]
    [InlineData("Idle_Talking", false)]
    public void ViolentClipNamesAreRefusedAnywhereInTheName(string asset, bool refused) => Assert.Equal(refused, IsRefused(asset));

    [Fact]
    public void TheLibraryHasNoHopGaitAndRunIsAJog()
    {
        // Calm world (B2466, review of PR #99): run jogs, there is no hop gait; TL may revisit.
        var library = Read<AnimationLibrary>("animation", "library.json");
        Assert.DoesNotContain(library.Gaits, g => g.Id == "hop");
        Assert.Equal("Jog_Fwd", library.Clips.Single(c => c.Id == "run").Asset);
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
    public void QualityDefaultExists()
    {
        // Detail distances live in content/look/terrain.json (detail levels, B2 R18) only.
        var quality = Read<LookQuality>("look", "quality.json");
        Assert.Contains(quality.DefaultLevel, quality.Levels.Select(l => l.Id));

        var camera = Read<LookCamera>("look", "camera.json");
        Assert.InRange(camera.DistanceM.Start, camera.DistanceM.Min, camera.DistanceM.Max);
        Assert.InRange(camera.PitchDeg, camera.PitchRangeDeg.Min, camera.PitchRangeDeg.Max);
        Assert.True(camera.PitchRangeDeg.Min < camera.PitchRangeDeg.Max, "the pitch range goes from flat to steep");
    }

    [Fact]
    public void ShotListsReferToKnownPhasesEpochsSeasonsAndWeather()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(TestData.Root, "content", "look"), "shots*.json"))
        {
            ShotListsReferToKnownIds(ContractSerializer.Deserialize<LookShots>(File.ReadAllText(file)));
        }

        // The default set stays the six T1 pictures; the dimensions set shows epochs, seasons and weather (R32).
        var plain = Read<LookShots>("look", "shots.json");
        Assert.True(plain.Epochs is null && plain.Seasons is null && plain.Weather is null);
        var dimensions = Read<LookShots>("look", "shots_dimensions.json");
        Assert.Equal(Seasons().Order(StringComparer.Ordinal), dimensions.Seasons!.Order(StringComparer.Ordinal));
        Assert.True(dimensions.Epochs!.Count >= 2 && dimensions.Weather!.Count >= 1);
    }

    private static void ShotListsReferToKnownIds(LookShots shots)
    {
        var phases = Read<LookDaylight>("look", "daylight.json").Phases.Select(p => p.Phase);
        Assert.All(shots.Phases, p => Assert.Contains(p, phases));
        var epochs = Read<LookEpochs>("look", "epochs.json").Epochs.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(shots.Epochs ?? [], e => Assert.Contains(e, epochs));
        Assert.All(shots.Seasons ?? [], s => Assert.Contains(s, Seasons()));
        Assert.All(shots.Weather ?? [], w => Assert.Contains(w, Vocabulary("weather.json")));
    }

    [Fact]
    public void TerrainDetailLevelsStartAtTheBaseGridAndGetCoarser()
    {
        var detail = Read<LookTerrain>("look", "terrain.json").Detail;
        var levels = detail.Levels.ToList();
        Assert.Equal(1, levels[0].CellStep);
        Assert.True(levels.Count >= 2, "at least one coarse level (B2 R18)");
        Assert.Equal(levels.Select(l => l.CellStep).Order(), levels.Select(l => l.CellStep));
        Assert.Null(levels[^1].UntilM);
        Assert.All(levels.Skip(1), l => Assert.True(l.MaxErrorMm is not null && l.MaxColorDeltaPermille is not null, "coarser levels need their tolerances"));
        var ends = levels.SkipLast(1).Select(l => l.UntilM!.Value).ToList();
        Assert.Equal(ends.Order(), ends);
        Assert.All(ends.Zip(ends.Skip(1)), p => Assert.True(p.Second - p.First > 4 * detail.SwitchMarginM));
        Assert.True(detail.SeaFloorMarginMm > 0, "merged sea floor stays clearly below the surface");
    }
}
