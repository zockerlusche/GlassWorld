using System.Text.Json;
using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;
using GlassWorld.Presentation.Ui;

namespace GlassWorld.Presentation.Tests;

public sealed class SelfCheckViewTests
{
    private static SelfCheck Example() => ContractSerializer.Deserialize<SelfCheck>(
        File.ReadAllText(Path.Combine(TestPaths.Root, "contracts", "examples", "valid", "self-check.nodes-missing.json")));

    [Fact]
    public void ProblemsComeFirstThenTheServiceOrder()
    {
        var lines = SelfCheckView.Lines(Example());

        Assert.Equal(["ui.selfcheck.ai.image", "ui.selfcheck.ai.language", "ui.selfcheck.ai.sound"], lines.Select(l => l.AiKey));
        Assert.Equal(["ui.selfcheck.state.problem", "ui.selfcheck.state.ok", "ui.selfcheck.state.not_planned"], lines.Select(l => l.StateKey));
        Assert.Equal(("msg.selfcheck.image.nodes_missing", "msg.selfcheck.image.nodes_missing.fix", "UnetLoaderGGUF", true), (lines[0].MessageKey, lines[0].FixKey, lines[0].Detail, lines[0].Problem));
        Assert.Null(lines[1].FixKey);
    }

    [Fact]
    public void OnlyAProblemNeedsThePlayer()
    {
        var check = Example();
        Assert.True(SelfCheckView.NeedsPlayer(check));

        foreach (var c in check.Checks)
        {
            c.State = c.State == AiCheckState.Problem ? AiCheckState.Preparing : c.State;
        }

        Assert.False(SelfCheckView.NeedsPlayer(check));
    }

    /// <summary>D1050 find 2: an image AI never set up gives no start hint; once a folder is named, its problems do.</summary>
    [Fact]
    public void AnImageAiNeverSetUpDoesNotCallThePlayer()
    {
        var check = Example();
        var image = check.Checks.Single(c => c.Ai == AiCheckAi.Image);
        image.Reason = SelfCheckView.ImageNotSetUp;
        Assert.False(SelfCheckView.NeedsPlayer(check));
        Assert.True(SelfCheckView.Lines(check)[0].Problem);

        image.Reason = "comfyui_down";
        Assert.True(SelfCheckView.NeedsPlayer(check));

        image.Reason = SelfCheckView.ImageNotSetUp;
        var language = check.Checks.Single(c => c.Ai == AiCheckAi.Language);
        language.State = AiCheckState.Problem;
        language.Reason = SelfCheckView.ImageNotSetUp;
        Assert.True(SelfCheckView.NeedsPlayer(check));
    }

    /// <summary>Every AI, state and view text the settings can show exists in DE and EN (KR4).</summary>
    [Fact]
    public void EveryKeyOfTheViewHasATextInBothLanguages()
    {
        var keys = Enum.GetValues<AiCheckAi>()
            .SelectMany(ai => Enum.GetValues<AiCheckState>().Select(state => SelfCheckView.Line(new AiCheck { Ai = ai, State = state, Reason = "x", MessageKey = "m" })))
            .SelectMany(l => new[] { l.AiKey, l.StateKey })
            .Concat([SelfCheckView.Unavailable, SelfCheckView.Checking, SelfCheckView.UnknownMessage])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var language in new[] { "de", "en" })
        {
            using var catalogue = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Root, "content", "i18n", language + ".json")));
            var known = catalogue.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain(keys, k => !known.Contains(k));
        }
    }

    [Fact]
    public void TheReportCopyTakesOnlyAPlainFileName()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gw-diagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(DiagnosticsFile.Save(folder, "diagnostics-20261110-101500.md", "# report"));
            Assert.Equal("# report", File.ReadAllText(Path.Combine(folder, "diagnostics-20261110-101500.md")));
            Assert.False(DiagnosticsFile.Save(folder, "../outside.md", "x"));
            Assert.False(DiagnosticsFile.Save(folder, "sub/inside.md", "x"));
            Assert.False(DiagnosticsFile.Save(folder, ".hidden", "x"));
            Assert.False(DiagnosticsFile.Save(folder, "diagnostics.bat", "x"));
            Assert.False(DiagnosticsFile.Save(folder, string.Empty, "x"));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
