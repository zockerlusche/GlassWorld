using System.Buffers.Binary;
using System.Text.Json.Nodes;
using GlassWorld.Contracts;
using GlassWorld.Presentation.Ui;

namespace GlassWorld.Presentation.Tests;

/// <summary>UI standard D366: scaling arithmetic, contrast rules and the shipped token/frame files.</summary>
public sealed class UiTests
{
    private static readonly string Root = FindRoot();
    private static readonly string TokensJson = File.ReadAllText(Path.Combine(Root, "content", "ui", "tokens.json"));
    private static readonly string FramesJson = File.ReadAllText(Path.Combine(Root, "content", "ui", "frames.json"));

    [Fact]
    public void ShippedFilesFollowTheStandard()
    {
        var style = UiStyle.FromJson(TokensJson, FramesJson);
        Assert.Empty(UiStandardRules.Check(style.Tokens, style.Frames));
    }

    [Theory]
    [InlineData(0.5, 0.75)]
    [InlineData(0.77, 0.75)]
    [InlineData(0.78, 0.8)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.74, 1.75)]
    [InlineData(3.0, 2.0)]
    public void UiScaleIsClampedAndSnappedToFivePercentSteps(double input, double expected)
    {
        var style = UiStyle.FromJson(TokensJson, FramesJson);
        Assert.Equal(expected, UiScaling.Snap(input, style.Tokens.UiScale), 6);
    }

    [Theory]
    [InlineData(1920, 1080, 1.0, 1920, 1080)]
    [InlineData(1920, 1080, 2.0, 960, 540)]
    [InlineData(1920, 1080, 0.75, 2560, 1440)]
    [InlineData(3840, 1600, 1.0, 2592, 1080)]
    [InlineData(3840, 1600, 2.0, 1296, 540)]
    public void LogicalSizeFollowsStretchAndUiScale(int w, int h, double scale, double expectedW, double expectedH)
    {
        var tokens = UiStyle.FromJson(TokensJson, FramesJson).Tokens;
        var (width, height) = UiScaling.LogicalSize(w, h, tokens, scale);
        Assert.Equal(expectedW, width, 0);
        Assert.Equal(expectedH, height, 0);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 1600)]
    public void SmallestTextMeetsXag101AtHundredPercent(int w, int h)
    {
        var style = UiStyle.FromJson(TokensJson, FramesJson);
        var physical = UiScaling.PhysicalPx(style.FontPx(UiFontSizeId.Small), w, h, style.Tokens, style.ContentScaleFactor);
        Assert.True(physical >= 18 * h / 1080.0 - 0.5, $"smallest text is {physical:0.0} px on {w}x{h}");
    }

    [Fact]
    public void TextScaleOnlyChangesFontSizes()
    {
        var style = UiStyle.FromJson(TokensJson, FramesJson, new UiSettings(1.0, 2.0, HighContrast: false, ReduceMotion: false));
        Assert.Equal(40, style.FontPx(UiFontSizeId.Body));
        Assert.Equal(1.0, style.ContentScaleFactor);
    }

    [Fact]
    public void ReduceMotionSetsEveryDurationToZero()
    {
        var style = UiStyle.FromJson(TokensJson, FramesJson, new UiSettings(1.0, 1.0, HighContrast: false, ReduceMotion: true));
        Assert.All(style.Tokens.DurationsMs, d => Assert.Equal(0, style.DurationMs(d.Id)));
    }

    [Fact]
    public void HighContrastSwitchesThePalette()
    {
        var normal = UiStyle.FromJson(TokensJson, FramesJson);
        var high = normal.With(normal.Settings with { HighContrast = true });
        Assert.NotEqual(normal.Color(UiColorId.Panel), high.Color(UiColorId.Panel));
        Assert.True(RgbaColor.Contrast(high.Color(UiColorId.Text), high.Color(UiColorId.Panel)) >= 7);
    }

    [Theory]
    [InlineData("#000000", "#FFFFFF", 21.0)]
    [InlineData("#FFFFFF", "#FFFFFF", 1.0)]
    [InlineData("#767676", "#FFFFFF", 4.54)]
    public void ContrastMatchesWcag(string a, string b, double expected) =>
        Assert.Equal(expected, RgbaColor.Contrast(RgbaColor.Parse(a), RgbaColor.Parse(b)), 2);

    [Fact]
    public void LowContrastTextIsRejected()
    {
        var tokens = JsonNode.Parse(TokensJson)!;
        var text = tokens["palettes"]![0]!["colors"]!.AsArray().Single(c => (string)c!["id"]! == "text")!;
        text["rgba"] = "#C8BCA8";
        var ex = Assert.Throws<InvalidDataException>(() => UiStyle.FromJson(tokens.ToJsonString(), FramesJson));
        Assert.Contains("text on Panel", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FontBelowMinimumIsRejected()
    {
        var tokens = JsonNode.Parse(TokensJson)!;
        tokens["font_sizes"]![4]!["px"] = 14;
        var ex = Assert.Throws<InvalidDataException>(() => UiStyle.FromJson(tokens.ToJsonString(), FramesJson));
        Assert.Contains("below min_font_px", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameWithoutCentreIsRejected()
    {
        var frames = JsonNode.Parse(FramesJson)!;
        frames["frames"]![0]!["margins"]!["left"] = 450;
        var ex = Assert.Throws<InvalidDataException>(() => UiStyle.FromJson(TokensJson, frames.ToJsonString()));
        Assert.Contains("leave no centre", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FramePicturesMatchTheFrameFile()
    {
        // The pictures arrive with GW2's frames (A1362); until then the theme falls back to flat styles.
        var folder = Path.Combine(Root, "game", "ui", "frames");
        var style = UiStyle.FromJson(TokensJson, FramesJson);
        foreach (var frame in style.Frames.Frames)
        {
            var png = Path.Combine(folder, frame.File);
            if (!File.Exists(png))
            {
                continue;
            }

            var header = File.ReadAllBytes(png).AsSpan(16, 8);
            Assert.Equal((frame.Width, frame.Height), (BinaryPrimitives.ReadInt32BigEndian(header), BinaryPrimitives.ReadInt32BigEndian(header[4..])));

            var sidecar = Path.ChangeExtension(png, ".json");
            if (File.Exists(sidecar))
            {
                var margins = JsonNode.Parse(File.ReadAllText(sidecar))!["margins"]!;
                Assert.Equal(
                    (frame.Margins.Left, frame.Margins.Top, frame.Margins.Right, frame.Margins.Bottom),
                    ((int)margins["left"]!, (int)margins["top"]!, (int)margins["right"]!, (int)margins["bottom"]!));
            }
        }
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GlassWorld.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("GlassWorld.slnx not found");
    }
}
