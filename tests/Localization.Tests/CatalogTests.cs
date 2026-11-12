using System.Text.Json;

namespace GlassWorld.Localization.Tests;

/// <summary>The DE and EN catalogs are complete and consistent (KR4, ADR-0007).</summary>
public sealed class CatalogTests
{
    private static readonly string[] Locales = ["de", "en"];

    private static readonly Lazy<IReadOnlyDictionary<string, TextCatalog>> Catalogs = new(() =>
        Locales.ToDictionary(l => l, l => TextCatalog.Load(Path.Combine(RepoPaths.I18n, l + ".json")), StringComparer.Ordinal));

    [Fact]
    public void ThereIsExactlyOneCatalogPerSupportedLanguage()
    {
        var files = Directory.GetFiles(RepoPaths.I18n, "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal);
        Assert.Equal(Locales, files);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void CatalogDeclaresItsOwnLocale(string locale) => Assert.Equal(locale, Catalogs.Value[locale].Locale);

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void KeysAreUnique(string locale)
    {
        var duplicates = Catalogs.Value[locale].Keys.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, $"{locale}.json: duplicate keys {string.Join(", ", duplicates)}");
    }

    /// <summary>Every object type of the catalogs has a name for the player (gifts, A1340).</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void EveryObjectTypeHasAName(string locale)
    {
        var keys = Catalogs.Value[locale].Keys.ToHashSet(StringComparer.Ordinal);
        var missing = Directory.GetFiles(Path.Combine(RepoPaths.Root, "content", "objects"), "*.json")
            .SelectMany(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("types").EnumerateArray())
            .Select(t => "vocab.object." + t.GetProperty("id").GetString())
            .Where(k => !keys.Contains(k))
            .ToList();
        Assert.True(missing.Count == 0, $"{locale}.json: no name for {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Every need of the inhabitant model has its bar label on the card (W1938, review GW1 #324): the bar names
    /// what it shows (Satt, Ausgeruht ...), so a missing key would show the raw key on the card.
    /// </summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void EveryModelNeedHasACardBarLabel(string locale)
    {
        var keys = Catalogs.Value[locale].Keys.ToHashSet(StringComparer.Ordinal);
        var needs = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "content", "inhabitants", "model.json"))).RootElement
            .GetProperty("needs").EnumerateArray().Select(n => n.GetProperty("id").GetString()!).ToList();
        Assert.NotEmpty(needs);
        var missing = needs.Select(id => "ui.card.need." + id).Where(k => !keys.Contains(k)).ToList();
        Assert.True(missing.Count == 0, $"{locale}.json: no card bar label for {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Values shown side by side on the inhabitant card (needs, moods, traits) need different texts;
    /// "Freude" for both fun and joy was found in the A1335 screenshots.
    /// </summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void CardValuesHaveDistinctTexts(string locale)
    {
        var shown = Catalogs.Value[locale].Entries
            .Where(e => e.Key.StartsWith("ui.card.need.", StringComparison.Ordinal) || e.Key.StartsWith("vocab.mood.", StringComparison.Ordinal) || e.Key.StartsWith("vocab.trait.", StringComparison.Ordinal));
        var clashes = shown.GroupBy(e => e.Value, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => $"'{g.Key}': {string.Join(", ", g.Select(e => e.Key))}").ToList();
        Assert.True(clashes.Count == 0, $"{locale}.json: same text for different card values: {string.Join("; ", clashes)}");
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void TextsAreNotBlank(string locale)
    {
        var blank = Catalogs.Value[locale].Entries.Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Key).ToList();
        Assert.True(blank.Count == 0, $"{locale}.json: blank texts for {string.Join(", ", blank)}");
    }

    [Fact]
    public void BothLanguagesHaveTheSameKeys()
    {
        var de = Catalogs.Value["de"].Keys.ToHashSet(StringComparer.Ordinal);
        var en = Catalogs.Value["en"].Keys.ToHashSet(StringComparer.Ordinal);
        Assert.True(de.SetEquals(en), $"only in de: {string.Join(", ", de.Except(en))}; only in en: {string.Join(", ", en.Except(de))}");
    }

    [Fact]
    public void BothLanguagesUseTheSamePlaceholders()
    {
        var en = Catalogs.Value["en"].Entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        var mismatches = Catalogs.Value["de"].Entries
            .Where(e => en.ContainsKey(e.Key) && !TextCatalog.Placeholders(e.Value).SequenceEqual(TextCatalog.Placeholders(en[e.Key])))
            .Select(e => e.Key)
            .ToList();
        Assert.True(mismatches.Count == 0, $"placeholders differ between de and en: {string.Join(", ", mismatches)}");
    }

    [Fact]
    public void EveryVocabularyIdHasADisplayText()
    {
        var keys = Catalogs.Value["en"].Keys.ToHashSet(StringComparer.Ordinal);
        var missing = VocabularyKeys().Where(k => !keys.Contains(k)).ToList();
        Assert.True(missing.Count == 0, $"vocabulary ids without display text: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryVocabularyKeyNamesAnExistingId()
    {
        var vocabulary = VocabularyKeys().ToHashSet(StringComparer.Ordinal);
        var stale = Catalogs.Value["en"].Keys.Where(k => k.StartsWith("vocab.", StringComparison.Ordinal) && !vocabulary.Contains(k)).ToList();
        Assert.True(stale.Count == 0, $"display texts for unknown vocabulary ids: {string.Join(", ", stale)}");
    }

    [Fact]
    public void EverySeasonAndDayPhaseHasADisplayText()
    {
        var keys = Catalogs.Value["en"].Keys.ToHashSet(StringComparer.Ordinal);
        using var time = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoPaths.Root, "content", "world", "time.json")));
        var expected = time.RootElement.GetProperty("seasons").EnumerateArray()
            .Select(s => $"time.season.{s.GetProperty("id").GetString()}")
            .Concat(VocabularyKeys()
                .Where(k => k.StartsWith("vocab.time_of_day.", StringComparison.Ordinal))
                .Select(k => "time.phase." + k["vocab.time_of_day.".Length..]))
            .ToList();
        Assert.NotEmpty(expected);
        var missing = expected.Where(k => !keys.Contains(k)).ToList();
        Assert.True(missing.Count == 0, $"world time names without display text: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryPlayerDataIdShownInTheUiHasADisplayText()
    {
        var keys = Catalogs.Value["en"].Keys.ToHashSet(StringComparer.Ordinal);
        var player = Path.Combine(RepoPaths.Root, "content", "player");
        var expected = Ids(Path.Combine(player, "message_kinds.json"), "kinds").Select(id => $"msg.mail.kind.{id}")
            .Concat(Ids(Path.Combine(player, "world_options.json"), "sizes").Select(id => $"ui.new_world.size.{id}"))
            .Concat(Ids(Path.Combine(player, "world_options.json"), "climates").Select(id => $"ui.new_world.climate.{id}"))
            .ToList();
        Assert.NotEmpty(expected);
        var missing = expected.Where(k => !keys.Contains(k)).ToList();
        Assert.True(missing.Count == 0, $"player data ids without display text: {string.Join(", ", missing)}");
    }

    [Theory]
    [InlineData("Hello {name}, day {day}.", new[] { "day", "name" })]
    [InlineData("No placeholder.", new string[0])]
    [InlineData("{a} and {a}", new[] { "a" })]
    public void PlaceholdersAreFound(string text, string[] expected) => Assert.Equal(expected, TextCatalog.Placeholders(text));

    private static IEnumerable<string> Ids(string file, string arrayName)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return [.. doc.RootElement.GetProperty(arrayName).EnumerateArray().Select(e => e.GetProperty("id").GetString()!)];
    }

    private static IEnumerable<string> VocabularyKeys()
    {
        foreach (var path in Directory.GetFiles(RepoPaths.Vocabulary, "*.json").Order(StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var kind = doc.RootElement.GetProperty("kind").GetString();
            foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                yield return $"vocab.{kind}.{entry.GetProperty("id").GetString()}";
            }
        }

        // Object types are catalog ids like the vocabulary (names for gifts, A1340).
        foreach (var path in Directory.GetFiles(Path.Combine(RepoPaths.Root, "content", "objects"), "*.json").Order(StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var type in doc.RootElement.GetProperty("types").EnumerateArray())
            {
                yield return $"vocab.object.{type.GetProperty("id").GetString()}";
            }
        }
    }
}
