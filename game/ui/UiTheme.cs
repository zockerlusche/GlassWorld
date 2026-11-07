using GlassWorld.Contracts;
using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game.Ui;

/// <summary>
/// Builds the Godot theme from the validated UI style (D366): fonts and sizes, colours of the
/// active palette, driftwood frames as 9-slice (B2521) or flat fallbacks while a picture is missing.
/// No UI value is written here; everything comes from content/ui.
/// </summary>
public static class UiTheme
{
    /// <summary>Theme type variation for window titles.</summary>
    public const string HeaderLabel = "HeaderLabel";

    /// <summary>Theme type variation for big titles.</summary>
    public const string TitleLabel = "TitleLabel";

    /// <summary>Theme type variation for section headings.</summary>
    public const string SubheaderLabel = "SubheaderLabel";

    /// <summary>Theme type variation for small print.</summary>
    public const string SmallLabel = "SmallLabel";

    /// <summary>Theme type variation for the window frame.</summary>
    public const string WindowPanel = "GwWindowPanel";

    /// <summary>Theme type variation for speech and thought bubbles.</summary>
    public const string BubblePanel = "GwBubblePanel";

    private const string FontFolder = "res://ui/fonts/";
    private const string FrameFolder = "res://ui/frames/";

    /// <summary>Creates the theme for a style.</summary>
    public static Theme Build(UiStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var theme = new Theme
        {
            DefaultFont = LoadFont("AtkinsonHyperlegible-Regular.ttf"),
            DefaultFontSize = style.FontPx(UiFontSizeId.Body),
        };

        var text = ToGodot(style.Color(UiColorId.Text));
        foreach (var type in new[] { "Label", "Button", "LineEdit", "TooltipLabel" })
        {
            theme.SetColor("font_color", type, text);
        }

        theme.SetColor("font_disabled_color", "Button", ToGodot(style.Color(UiColorId.Text_muted)));
        theme.SetColor("font_hover_color", "Button", text);
        theme.SetColor("font_pressed_color", "Button", text);
        theme.SetColor("font_focus_color", "Button", text);

        Variation(theme, TitleLabel, style.FontPx(UiFontSizeId.Display));
        Variation(theme, HeaderLabel, style.FontPx(UiFontSizeId.Header));
        Variation(theme, SubheaderLabel, style.FontPx(UiFontSizeId.Subheader));
        Variation(theme, SmallLabel, style.FontPx(UiFontSizeId.Small));
        theme.SetFontSize("font_size", "TooltipLabel", style.FontPx(UiFontSizeId.Small));

        var bold = LoadFont("AtkinsonHyperlegible-Bold.ttf");
        foreach (var variation in new[] { TitleLabel, HeaderLabel, SubheaderLabel })
        {
            theme.SetFont("font", variation, bold);
        }

        var window = Frame(style, UiFrameId.Window, UiColorId.Panel);
        theme.SetTypeVariation(WindowPanel, "PanelContainer");
        theme.SetStylebox("panel", WindowPanel, window);
        theme.SetStylebox("panel", "TooltipPanel", Frame(style, UiFrameId.Tooltip, UiColorId.Panel_strong));
        theme.SetTypeVariation(BubblePanel, "PanelContainer");
        theme.SetStylebox("panel", BubblePanel, Frame(style, UiFrameId.Bubble, UiColorId.Bubble_inhabitant));

        theme.SetStylebox("normal", "Button", Frame(style, UiFrameId.Button, UiColorId.Panel_strong));
        var button = style.Tokens.Button;
        theme.SetStylebox("hover", "Button", Frame(style, UiFrameId.Button, UiColorId.Panel_strong, (UiColorId.Accent, button.HoverTint)));
        theme.SetStylebox("pressed", "Button", Frame(style, UiFrameId.Button, UiColorId.Panel_strong, (UiColorId.Focus, button.PressedTint)));
        theme.SetStylebox("disabled", "Button", Frame(style, UiFrameId.Button, UiColorId.Panel, (UiColorId.Text_muted, button.DisabledTint)));
        theme.SetConstant("h_separation", "Button", style.Tokens.Window.Gap);
        theme.SetStylebox("focus", "Button", FocusRing(style));
        theme.SetConstant("separation", "VBoxContainer", style.Tokens.Window.Gap);
        theme.SetConstant("separation", "HBoxContainer", style.Tokens.Window.Gap);
        return theme;
    }

    /// <summary>Converts a token colour.</summary>
    public static Color ToGodot(RgbaColor color) => new((float)color.R, (float)color.G, (float)color.B, (float)color.A);

    private static void Variation(Theme theme, string name, int size)
    {
        theme.SetTypeVariation(name, "Label");
        theme.SetFontSize("font_size", name, size);
    }

    private static FontFile LoadFont(string file) => GD.Load<FontFile>(FontFolder + file);

    /// <summary>
    /// A frame picture as 9-slice, optionally tinted; or a flat box with the same padding while the
    /// picture is missing. The picture is drawn at 1/source_scale of its pixel size (sharp downscale),
    /// so margins and borders are in logical pixels.
    /// </summary>
    private static StyleBox Frame(UiStyle style, UiFrameId id, UiColorId fill, (UiColorId Color, double Strength)? tint = null)
    {
        var frame = style.Frame(id);
        var padding = (float)(frame.ContentPadding + (id == UiFrameId.Window ? style.Tokens.Window.Padding : 0));
        var texture = LoadFrameTexture(frame, style.Frames.SourceScale);
        if (texture is not null)
        {
            var box = new StyleBoxTexture
            {
                Texture = texture,
                TextureMarginLeft = (float)style.LogicalMargin(frame.Margins.Left),
                TextureMarginTop = (float)style.LogicalMargin(frame.Margins.Top),
                TextureMarginRight = (float)style.LogicalMargin(frame.Margins.Right),
                TextureMarginBottom = (float)style.LogicalMargin(frame.Margins.Bottom),
                AxisStretchHorizontal = StyleBoxTexture.AxisStretchMode.TileFit,
                AxisStretchVertical = StyleBoxTexture.AxisStretchMode.TileFit,
                ModulateColor = tint is { } t ? Colors.White.Lerp(ToGodot(style.Color(t.Color)), (float)t.Strength) : Colors.White,
            };
            box.SetContentMarginAll(padding);
            return box;
        }

        var flat = new StyleBoxFlat
        {
            BgColor = ToGodot(style.Color(fill)),
            BorderColor = tint is { } c ? ToGodot(style.Color(c.Color)) : ToGodot(style.Color(UiColorId.Border)),
        };
        flat.SetBorderWidthAll(style.Tokens.Window.BorderWidth);
        flat.SetCornerRadiusAll(style.Tokens.Window.CornerRadius);
        flat.SetContentMarginAll(padding);
        return flat;
    }

    private static ImageTexture? LoadFrameTexture(UiFrame frame, int sourceScale)
    {
        var path = FrameFolder + frame.File;
        if (!ResourceLoader.Exists(path))
        {
            return null;
        }

        var image = GD.Load<Texture2D>(path)?.GetImage();
        if (image is null)
        {
            GD.PushWarning($"ui: frame '{frame.File}' has no image data; using the flat fallback");
            return null;
        }

        image.GenerateMipmaps();
        var texture = ImageTexture.CreateFromImage(image);
        texture.SetSizeOverride(new Vector2I(frame.Width / sourceScale, frame.Height / sourceScale));
        return texture;
    }

    private static StyleBoxFlat FocusRing(UiStyle style)
    {
        var ring = new StyleBoxFlat { DrawCenter = false, BorderColor = ToGodot(style.Color(UiColorId.Focus)) };
        ring.SetBorderWidthAll(style.Tokens.Window.BorderWidth);
        ring.SetCornerRadiusAll(style.Tokens.Window.CornerRadius);
        return ring;
    }
}
