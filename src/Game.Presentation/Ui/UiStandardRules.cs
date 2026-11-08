using GlassWorld.Contracts;

namespace GlassWorld.Presentation.Ui;

/// <summary>
/// Rules of the UI standard D366 that the schema cannot express. The game refuses token files that
/// break them; the tests keep the shipped files green.
/// </summary>
public static class UiStandardRules
{
    /// <summary>Minimum contrast for text (WCAG 2.2 1.4.3, XAG 102).</summary>
    public const double TextContrast = 4.5;

    /// <summary>Minimum contrast for large text, symbols and controls (WCAG 1.4.11, XAG 102).</summary>
    public const double ControlContrast = 3.0;

    /// <summary>Minimum contrast of every text in the high-contrast palette (XAG 102).</summary>
    public const double HighContrast = 7.0;

    private static readonly UiColorId[] TextColors = [UiColorId.Text, UiColorId.Text_muted];
    private static readonly UiColorId[] ControlColors = [UiColorId.Border, UiColorId.Accent, UiColorId.Focus, UiColorId.Positive, UiColorId.Attention];

    /// <summary>All problems found; empty if the files follow the standard.</summary>
    public static IReadOnlyList<string> Check(UiTokens tokens, UiFrames frames)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(frames);
        var problems = new List<string>();

        CheckRange("ui_scale", tokens.UiScale, problems);
        CheckRange("text_scale", tokens.TextScale, problems);

        foreach (var step in Enum.GetValues<UiFontSizeId>())
        {
            var sizes = tokens.FontSizes.Where(f => f.Id == step).ToList();
            if (sizes.Count != 1)
            {
                problems.Add($"font size '{step}' must exist exactly once");
            }
            else if (sizes[0].Px < tokens.MinFontPx)
            {
                problems.Add($"font size '{step}' ({sizes[0].Px}) is below min_font_px ({tokens.MinFontPx})");
            }
        }

        problems.AddRange(tokens.Spacing.Where(s => s.Px % 4 != 0).Select(s => $"spacing '{s.Id}' ({s.Px}) is not on the 4 px grid"));

        if (tokens.Button.MinHit < 40 || tokens.Button.Height < tokens.Button.MinHit)
        {
            problems.Add("buttons need a hit area of at least 40 px and must not be smaller than it");
        }

        CheckPalette(tokens, UiStyle.DefaultPalette, TextContrast, problems);
        CheckPalette(tokens, UiStyle.HighContrastPalette, HighContrast, problems);

        foreach (var frame in frames.Frames)
        {
            var m = frame.Margins;
            if (m.Left + m.Right >= frame.Width || m.Top + m.Bottom >= frame.Height)
            {
                problems.Add($"frame '{frame.Id}': margins leave no centre");
            }

            // Whole logical pixels at UI 100 % (GW1 review #66 F3): no blur, no distortion.
            if (new[] { frame.Width, frame.Height, m.Left, m.Top, m.Right, m.Bottom }.Any(v => v % frames.SourceScale != 0))
            {
                problems.Add($"frame '{frame.Id}': size and margins must be multiples of source_scale {frames.SourceScale}");
            }

            if (frame.NineSlice != (m.Left + m.Top + m.Right + m.Bottom > 0))
            {
                problems.Add($"frame '{frame.Id}': nine_slice and margins disagree");
            }
        }

        var button = frames.Frames.FirstOrDefault(f => f.Id == UiFrameId.Button);
        if (button is not null && (double)(button.Margins.Top + button.Margins.Bottom) / frames.SourceScale > tokens.Button.Height)
        {
            problems.Add($"button height {tokens.Button.Height} cannot hold the button frame's vertical margins");
        }

        foreach (var id in Enum.GetValues<UiFrameId>().Where(id => frames.Frames.Count(f => f.Id == id) != 1))
        {
            problems.Add($"frame '{id}' must exist exactly once");
        }

        return problems;
    }

    private static void CheckRange(string name, UiScaleRange range, List<string> problems)
    {
        if (range.Min > range.Default || range.Default > range.Max)
        {
            problems.Add($"{name}: default must lie between min and max");
        }

        if (range.Max < 2.0)
        {
            problems.Add($"{name}: must reach 200 % (XAG 101, B2513)");
        }
    }

    private static void CheckPalette(UiTokens tokens, string id, double textContrast, List<string> problems)
    {
        var palette = tokens.Palettes.SingleOrDefault(p => p.Id == id);
        if (palette is null)
        {
            problems.Add($"palette '{id}' is missing");
            return;
        }

        var colors = palette.Colors.ToDictionary(c => c.Id, c => RgbaColor.Parse(c.Rgba));
        var missing = Enum.GetValues<UiColorId>().Where(c => !colors.ContainsKey(c)).ToList();
        if (missing.Count > 0)
        {
            problems.Add($"palette '{id}' lacks {string.Join(", ", missing)}");
            return;
        }

        foreach (var surface in new[] { UiColorId.Panel, UiColorId.Panel_strong, UiColorId.Bubble_inhabitant, UiColorId.Bubble_player })
        {
            var back = colors[surface].Over(new RgbaColor(1, 1, 1, 1));
            foreach (var text in TextColors)
            {
                Require(id, text, surface, RgbaColor.Contrast(colors[text].Over(back), back), textContrast, problems);
            }

            if (surface is UiColorId.Panel or UiColorId.Panel_strong)
            {
                // Selected table rows: panel-coloured text on the accent (GW1 review #66 F1).
                var accent = colors[UiColorId.Accent].Over(new RgbaColor(1, 1, 1, 1));
                Require(id, UiColorId.Panel, UiColorId.Accent, RgbaColor.Contrast(colors[UiColorId.Panel].Over(accent), accent), textContrast, problems);

                foreach (var control in ControlColors)
                {
                    Require(id, control, surface, RgbaColor.Contrast(colors[control].Over(back), back), ControlContrast, problems);
                }
            }
        }
    }

    private static void Require(string palette, UiColorId front, UiColorId back, double ratio, double needed, List<string> problems)
    {
        if (ratio < needed)
        {
            problems.Add($"palette '{palette}': {front} on {back} has contrast {ratio:0.00}, needs {needed:0.0}");
        }
    }
}
