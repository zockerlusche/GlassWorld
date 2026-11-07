namespace GlassWorld.Localization.Tests;

/// <summary>
/// CI text check (KR4): scenes and UI code show only translation keys, and every key they use
/// exists in the catalogs. The scanner itself is proven with counter-examples below.
/// </summary>
public sealed class GameTextTests
{
    [Fact]
    public void ScenesShowOnlyKeys()
    {
        var violations = Scenes().SelectMany(s => GameTextScanner.ScanScene(s.File, s.Content).Violations).ToList();
        Assert.True(violations.Count == 0, "hard-coded player text in scenes:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void CodeShowsOnlyKeys()
    {
        var violations = CodeFiles().SelectMany(c => GameTextScanner.ScanCode(c.File, c.Content).Violations).ToList();
        Assert.True(violations.Count == 0, "hard-coded player text in code:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void EveryUsedKeyExists()
    {
        var known = TextCatalog.Load(Path.Combine(RepoPaths.I18n, "en.json")).Keys.ToHashSet(StringComparer.Ordinal);
        var unknown = Scenes().SelectMany(s => GameTextScanner.ScanScene(s.File, s.Content).Keys)
            .Concat(KeySourceFiles().SelectMany(c => GameTextScanner.ScanCode(c.File, c.Content).Keys))
            .Where(k => !known.Contains(k.Key))
            .Select(k => $"{k.File}:{k.Line}: {k.Key}")
            .ToList();
        Assert.True(unknown.Count == 0, "keys missing in content/i18n:\n" + string.Join("\n", unknown));
    }

    [Theory]
    [InlineData("""text = "Hello" """)]
    [InlineData("""tooltip_text = "Save the world" """)]
    [InlineData("""placeholder_text = "Type here" """)]
    [InlineData("""title = "Settings" """)]
    [InlineData("""text = "Two\nlines" """)]
    public void SceneLiteralIsAViolation(string line)
    {
        var result = GameTextScanner.ScanScene("x.tscn", "[node name=\"L\" type=\"Label\"]\n" + line.TrimEnd() + "\n");
        Assert.Single(result.Violations);
        Assert.Equal(2, result.Violations[0].Line);
    }

    [Theory]
    [InlineData("""text = "ui.main.title" """)]
    [InlineData("""text = "" """)]
    [InlineData("""text = "?" """)]
    [InlineData("""name = "Hello" """)]
    [InlineData("""script = "res://scripts/Main.cs" """)]
    public void SceneKeyOrNonTextPropertyPasses(string line)
    {
        var result = GameTextScanner.ScanScene("x.tscn", line.TrimEnd() + "\n");
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void SceneKeyIsCollected()
    {
        var result = GameTextScanner.ScanScene("x.tscn", "text = \"ui.main.title\"\n");
        Assert.Equal("ui.main.title", Assert.Single(result.Keys).Key);
    }

    [Theory]
    [InlineData("""label.Text = "Hello";""")]
    [InlineData("""var b = new Button { Text = "Save" };""")]
    [InlineData("""button.TooltipText = cond ? "ui.a.b" : "Plain text";""")]
    [InlineData("""label.Text = "Day " + day;""")]
    [InlineData("""label.Text = $"Day {day}";""")]
    [InlineData("""label.SetText("Hello");""")]
    [InlineData("""var s = Tr("Hello");""")]
    [InlineData("""window.Title = "Settings";""")]
    public void CodeLiteralIsAViolation(string statement)
    {
        var result = GameTextScanner.ScanCode("x.cs", Wrap(statement));
        Assert.Single(result.Violations);
    }

    [Theory]
    [InlineData("""label.Text = "ui.main.title";""")]
    [InlineData("""label.Text = Tr("ui.main.title");""")]
    [InlineData("""label.Text = string.Format(Tr("msg.chronicle.birth"), name);""")]
    [InlineData("""label.Text = $"{day}";""")]
    [InlineData("""label.Text = name;""")]
    [InlineData("""label.Text = "";""")]
    [InlineData("""mark.Text = "?";""")]
    [InlineData("""close.Text = "×";""")]
    [InlineData("""label.Text = "42";""")]
    [InlineData("""GD.Print("GlassWorld started");""")]
    [InlineData("""var id = "move_to";""")]
    public void CodeKeyOrNonUiTextPasses(string statement)
    {
        var result = GameTextScanner.ScanCode("x.cs", Wrap(statement));
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void CodeKeyIsCollectedWithLine()
    {
        var result = GameTextScanner.ScanCode("x.cs", Wrap("""label.Text = Tr("ui.unknown.key");"""));
        var use = Assert.Single(result.Keys);
        Assert.Equal("ui.unknown.key", use.Key);
        Assert.Equal(5, use.Line);
    }

    [Theory]
    [InlineData("vocab.need.hunger", true)]
    [InlineData("ui.settings.language.de", true)]
    [InlineData("time.season.spring", true)]
    [InlineData("hunger", false)]
    [InlineData("ui.title", false)]
    [InlineData("other.a.b", false)]
    [InlineData("ui.Main.title", false)]
    public void KeyShape(string value, bool expected) => Assert.Equal(expected, GameTextScanner.IsKey(value));

    private static string Wrap(string statement) =>
        "class C\n{\n    void M(bool cond, int day, string name)\n    {\n        " + statement + "\n    }\n}\n";

    private static IEnumerable<(string File, string Content)> Scenes() =>
        RepoPaths.PlayerFacingSourceFolders
            .SelectMany(f => RepoPaths.FilesBelow(f, ".tscn"))
            .Select(f => (RepoPaths.Relative(f), File.ReadAllText(f)));

    private static IEnumerable<(string File, string Content)> KeySourceFiles() =>
        RepoPaths.KeySourceFolders
            .SelectMany(f => RepoPaths.FilesBelow(f, ".cs"))
            .Select(f => (RepoPaths.Relative(f), File.ReadAllText(f)));

    private static IEnumerable<(string File, string Content)> CodeFiles() =>
        RepoPaths.PlayerFacingSourceFolders
            .SelectMany(f => RepoPaths.FilesBelow(f, ".cs"))
            .Select(f => (RepoPaths.Relative(f), File.ReadAllText(f)));
}
