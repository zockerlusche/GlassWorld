using GlassWorld.Contracts;
using GlassWorld.Contracts.Validation;

namespace GlassWorld.Presentation.Ui;

/// <summary>Player settings that change how the UI looks (D366 sections 2, 9 and 13).</summary>
/// <param name="UiScale">UI size, 1 = 100 % (B2513: 0.75-2).</param>
/// <param name="TextScale">Text size on top of the UI size, 1 = 100 %.</param>
/// <param name="HighContrast">Use the high-contrast palette (XAG 102).</param>
/// <param name="ReduceMotion">All UI animations and the glass tremor off (XAG 117).</param>
public sealed record UiSettings(double UiScale, double TextScale, bool HighContrast, bool ReduceMotion)
{
    /// <summary>Defaults from the tokens.</summary>
    public static UiSettings Defaults(UiTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return new UiSettings(tokens.UiScale.Default, tokens.TextScale.Default, HighContrast: false, ReduceMotion: false);
    }
}

/// <summary>
/// The resolved UI look for one set of settings: everything the Godot theme needs, without Godot
/// types. Built from the validated token and frame files.
/// </summary>
public sealed class UiStyle
{
    /// <summary>Palette used unless high contrast is on.</summary>
    public const string DefaultPalette = "default";

    /// <summary>Palette for the high-contrast mode.</summary>
    public const string HighContrastPalette = "high_contrast";

    private readonly Dictionary<UiColorId, RgbaColor> _colors;

    private UiStyle(UiTokens tokens, UiFrames frames, UiSettings settings)
    {
        Tokens = tokens;
        Frames = frames;
        Settings = settings with
        {
            UiScale = UiScaling.Snap(settings.UiScale, tokens.UiScale),
            TextScale = UiScaling.Snap(settings.TextScale, tokens.TextScale),
        };
        var palette = tokens.Palettes.Single(p => p.Id == (Settings.HighContrast ? HighContrastPalette : DefaultPalette));
        _colors = palette.Colors.ToDictionary(c => c.Id, c => RgbaColor.Parse(c.Rgba));
    }

    /// <summary>The token file.</summary>
    public UiTokens Tokens { get; }

    /// <summary>The frame file.</summary>
    public UiFrames Frames { get; }

    /// <summary>Settings after clamping and snapping.</summary>
    public UiSettings Settings { get; }

    /// <summary>Parses and validates both files; throws <see cref="ContractViolationException"/> or <see cref="InvalidDataException"/>.</summary>
    public static UiStyle FromJson(string tokensJson, string framesJson, UiSettings? settings = null)
    {
        var tokens = ContractSerializer.Deserialize<UiTokens>(tokensJson);
        var frames = ContractSerializer.Deserialize<UiFrames>(framesJson);
        var problems = UiStandardRules.Check(tokens, frames);
        if (problems.Count > 0)
        {
            throw new InvalidDataException("UI tokens break the UI standard: " + string.Join("; ", problems));
        }

        return new UiStyle(tokens, frames, settings ?? UiSettings.Defaults(tokens));
    }

    /// <summary>The same files with other settings (e.g. after the player moved a slider).</summary>
    public UiStyle With(UiSettings settings) => new(Tokens, Frames, settings);

    /// <summary>Godot's <c>Window.ContentScaleFactor</c> for the current UI size.</summary>
    public double ContentScaleFactor => Settings.UiScale;

    /// <summary>Colour of a token in the active palette.</summary>
    public RgbaColor Color(UiColorId id) => _colors[id];

    /// <summary>Font size in logical pixels including the text size setting.</summary>
    public int FontPx(UiFontSizeId step) => UiScaling.FontPx(Tokens, step, Settings.TextScale);

    /// <summary>Animation time; zero when motion is reduced.</summary>
    public int DurationMs(string id) => Settings.ReduceMotion ? 0 : Tokens.DurationsMs.Single(d => d.Id == id).Ms;

    /// <summary>A frame by id.</summary>
    public UiFrame Frame(UiFrameId id) => Frames.Frames.Single(f => f.Id == id);

    /// <summary>9-slice margin of a frame in logical pixels (source pixels / source scale).</summary>
    public double LogicalMargin(int sourcePixels) => (double)sourcePixels / Frames.SourceScale;
}
