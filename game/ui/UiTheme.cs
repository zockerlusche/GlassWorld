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

    /// <summary>A header over a darkened picture (night scene): the light panel colour instead of the dark text colour.</summary>
    public const string OnDarkHeaderLabel = "OnDarkHeaderLabel";

    /// <summary>Body text over a darkened picture (night scene).</summary>
    public const string OnDarkLabel = "OnDarkLabel";

    /// <summary>A value bar in the bad range (bar rule B2642): its own fill, brighter than a good one (S1b, Marcel sees colours weakly).</summary>
    public const string BadBar = "GwBadBar";

    /// <summary>Theme type variation for big titles.</summary>
    public const string TitleLabel = "TitleLabel";

    /// <summary>Theme type variation for section headings.</summary>
    public const string SubheaderLabel = "SubheaderLabel";

    /// <summary>Theme type variation for small print.</summary>
    public const string SmallLabel = "SmallLabel";

    /// <summary>Theme type variation for the window frame.</summary>
    public const string WindowPanel = "GwWindowPanel";

    /// <summary>The window frame with the small padding of the zones layout (top bar and zone panels, W1717).</summary>
    public const string CompactPanel = "GwCompactPanel";

    /// <summary>Panel type for hints and tooltips (Godot's built-in type name).</summary>
    public const string TooltipPanel = "TooltipPanel";

    /// <summary>
    /// A part that stands on the world without a box (S1b, D795, D1076 section 6: the corners top left and bottom
    /// left): no frame, only the room around; its text comes from <see cref="BuildWorld"/>.
    /// </summary>
    public const string WorldPanel = "GwWorldPanel";

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

        // Text over the night scene's shade: dark text on the dark picture could not be read (first look at
        // the compute night in a window, run L854).
        var onDark = ToGodot(style.Color(UiColorId.Panel));
        Variation(theme, OnDarkHeaderLabel, style.FontPx(UiFontSizeId.Header));
        Variation(theme, OnDarkLabel, style.FontPx(UiFontSizeId.Body));
        theme.SetColor("font_color", OnDarkHeaderLabel, onDark);
        theme.SetColor("font_color", OnDarkLabel, onDark);

        var bold = LoadFont("AtkinsonHyperlegible-Bold.ttf");
        foreach (var variation in new[] { TitleLabel, HeaderLabel, SubheaderLabel, OnDarkHeaderLabel })
        {
            theme.SetFont("font", variation, bold);
        }

        var window = Frame(style, UiFrameId.Window, UiColorId.Panel);
        theme.SetTypeVariation(WindowPanel, "PanelContainer");
        theme.SetStylebox("panel", WindowPanel, window);

        // The same frame with the zones' small padding: a low top bar and slim panels keep the middle free.
        // The frame's own content padding is left out: only the zones' padding, and never less than the corners of
        // a frame picture (a smaller margin would squeeze them), so the top bar is one low line (feedback on D646).
        var compact = (StyleBox)window.Duplicate();
        var pad = (float)style.Tokens.Zones.PanelPadding;
        if (compact is StyleBoxTexture picture)
        {
            picture.ContentMarginLeft = Math.Max(pad, picture.TextureMarginLeft);
            picture.ContentMarginRight = Math.Max(pad, picture.TextureMarginRight);
            picture.ContentMarginTop = Math.Max(pad, picture.TextureMarginTop);
            picture.ContentMarginBottom = Math.Max(pad, picture.TextureMarginBottom);
        }
        else
        {
            compact.SetContentMarginAll(pad + style.Tokens.Window.BorderWidth);
        }
        theme.SetTypeVariation(CompactPanel, "PanelContainer");
        theme.SetStylebox("panel", CompactPanel, compact);
        theme.SetStylebox("panel", TooltipPanel, Frame(style, UiFrameId.Tooltip, UiColorId.Panel_strong));
        // No room of its own: the safe margin already keeps it off the edge, and the time block stays clear of the middle at 200 %.
        var world = new StyleBoxEmpty();
        theme.SetTypeVariation(WorldPanel, "PanelContainer");
        theme.SetStylebox("panel", WorldPanel, world);
        theme.SetTypeVariation(BubblePanel, "PanelContainer");
        theme.SetStylebox("panel", BubblePanel, Frame(style, UiFrameId.Bubble, UiColorId.Bubble_inhabitant));

        theme.SetStylebox("normal", "Button", Frame(style, UiFrameId.Button, UiColorId.Panel_strong));
        var button = style.Tokens.Button;
        theme.SetStylebox("hover", "Button", Frame(style, UiFrameId.Button, UiColorId.Panel_strong, (UiColorId.Accent, button.HoverTint)));
        theme.SetStylebox("pressed", "Button", Frame(style, UiFrameId.Button, UiColorId.Panel_strong, (UiColorId.Focus, button.PressedTint)));
        theme.SetStylebox("disabled", "Button", Frame(style, UiFrameId.Button, UiColorId.Panel, (UiColorId.Text_muted, button.DisabledTint)));
        theme.SetConstant("h_separation", "Button", style.Tokens.Window.Gap);
        theme.SetStylebox("focus", "Button", FocusRing(style));

        // Text fields (new world, talk field): light like the windows, not Godot's dark default, so dark
        // text stays readable; the hint text muted, the focus ring like buttons (B6 R2 UI standard).
        // S1b: a field has a form of its own - a light inset with a line below - so it is not taken for a button.
        theme.SetStylebox("normal", "LineEdit", Field(style, Frame(style, UiFrameId.Button, UiColorId.Field)));
        theme.SetStylebox("read_only", "LineEdit", Field(style, Frame(style, UiFrameId.Button, UiColorId.Panel, (UiColorId.Text_muted, button.DisabledTint))));
        theme.SetStylebox("focus", "LineEdit", FocusRing(style));
        theme.SetColor("font_placeholder_color", "LineEdit", ToGodot(style.Color(UiColorId.Text_muted)));
        theme.SetColor("caret_color", "LineEdit", text);
        TreeStyle(theme, style);
        BarStyle(theme, style);
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
            // Never smaller than the corners: a control below left+right (top+bottom) margins would squeeze
            // the corners into each other (A1362 test with the driftwood frames).
            box.ContentMarginLeft = Math.Max(padding, box.TextureMarginLeft);
            box.ContentMarginRight = Math.Max(padding, box.TextureMarginRight);
            box.ContentMarginTop = Math.Max(padding, box.TextureMarginTop);
            box.ContentMarginBottom = Math.Max(padding, box.TextureMarginBottom);
            return box;
        }

        var flat = new StyleBoxFlat
        {
            BgColor = ToGodot(style.Color(fill)),
            BorderColor = tint is { } c ? ToGodot(style.Color(c.Color)) : ToGodot(style.Color(UiColorId.Border)),
        };
        flat.SetBorderWidthAll(style.Tokens.Window.BorderWidth);

        // S1b (D1076 section 6): buttons and fields a shape of their own, windows rounder and lifted from the world by
        // a soft shadow instead of a hard box.
        flat.SetCornerRadiusAll(id is UiFrameId.Button ? style.Tokens.Window.ButtonRadius : style.Tokens.Window.CornerRadius);
        if (id is UiFrameId.Window or UiFrameId.Tooltip)
        {
            flat.ShadowSize = style.Tokens.Window.ShadowPx;
            flat.ShadowColor = ToGodot(style.Color(UiColorId.Border)) with { A = (float)style.Tokens.Window.ShadowAlpha };
        }

        flat.SetContentMarginAll(padding);
        return flat;
    }

    /// <summary>A text field: the frame with a thin border and a thicker line below (a flat frame; a picture frame stays as it is).</summary>
    private static StyleBox Field(UiStyle style, StyleBox frame)
    {
        if (frame is StyleBoxFlat flat)
        {
            flat.SetBorderWidthAll(Math.Max(1, style.Tokens.Window.BorderWidth / 2));
            flat.BorderWidthBottom = style.Tokens.Window.BorderWidth + 1;
        }

        return frame;
    }

    /// <summary>
    /// The theme of the parts that stand on the world without a box (S1b, D795): light text with a dark outline, so it
    /// reads on grass, water and sky alike; flat buttons (rows of the portrait list and the log) the same. Set on those
    /// parts only; everything else falls back to the main theme.
    /// </summary>
    public static Theme BuildWorld(UiStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var theme = new Theme();
        var text = ToGodot(style.Color(UiColorId.World_text));
        var outline = ToGodot(style.Color(UiColorId.World_outline));
        foreach (var type in new[] { "Label", "Button" })
        {
            theme.SetColor("font_outline_color", type, outline);
            theme.SetConstant("outline_size", type, style.Tokens.Window.WorldOutlinePx);
        }

        theme.SetColor("font_color", "Label", text);
        foreach (var name in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color", "font_hover_pressed_color" })
        {
            theme.SetColor(name, "Button", text);
        }

        // A scroll bar on the world: no track, a thin grip in the outline colour (S1b tour: a dark bar stood beside the log).
        var thin = Math.Max(2, style.Tokens.Window.WorldOutlinePx / 2);
        StyleBoxFlat Grip(float alpha)
        {
            var grip = new StyleBoxFlat { BgColor = outline with { A = outline.A * alpha } };
            grip.SetCornerRadiusAll(thin);
            grip.ContentMarginLeft = grip.ContentMarginRight = thin;
            return grip;
        }

        var track = new StyleBoxEmpty();
        track.ContentMarginLeft = track.ContentMarginRight = thin;
        theme.SetStylebox("scroll", "VScrollBar", track);
        theme.SetStylebox("scroll_focus", "VScrollBar", track);
        theme.SetStylebox("grabber", "VScrollBar", Grip(0.5f));
        theme.SetStylebox("grabber_highlight", "VScrollBar", Grip(0.8f));
        theme.SetStylebox("grabber_pressed", "VScrollBar", Grip(1f));
        return theme;
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

    /// <summary>
    /// Tables (GwTable is a Tree): background, header and text in the token colours, so they follow the
    /// palette and high contrast instead of the engine's dark default (A1362 screenshots).
    /// </summary>
    private static void TreeStyle(Theme theme, UiStyle style)
    {
        StyleBoxFlat Flat(UiColorId id)
        {
            var box = new StyleBoxFlat { BgColor = ToGodot(style.Color(id)) };
            box.SetContentMarginAll(style.Tokens.Window.Padding / 2f);
            return box;
        }

        theme.SetStylebox("panel", "Tree", Flat(UiColorId.Panel));
        theme.SetStylebox("focus", "Tree", FocusRing(style));
        theme.SetStylebox("title_button_normal", "Tree", Flat(UiColorId.Panel_strong));
        theme.SetStylebox("title_button_hover", "Tree", Flat(UiColorId.Panel_strong));
        theme.SetStylebox("title_button_pressed", "Tree", Flat(UiColorId.Panel_strong));
        theme.SetStylebox("selected", "Tree", Flat(UiColorId.Accent));
        theme.SetStylebox("selected_focus", "Tree", Flat(UiColorId.Accent));
        theme.SetColor("font_color", "Tree", ToGodot(style.Color(UiColorId.Text)));
        theme.SetColor("title_button_color", "Tree", ToGodot(style.Color(UiColorId.Text)));
        theme.SetColor("font_selected_color", "Tree", ToGodot(style.Color(UiColorId.Panel)));
        theme.SetColor("guide_color", "Tree", ToGodot(style.Color(UiColorId.Border)));
    }

    /// <summary>
    /// Value bars of the inhabitant card: dark text on light token colours with a thin border, so the
    /// percentage stays readable (M1 photos: the engine default drew light text on light grey). Follows the
    /// palette and high contrast.
    /// </summary>
    private static void BarStyle(Theme theme, UiStyle style)
    {
        // Thin border and small radius derived from the window tokens (half of each).
        StyleBoxFlat Box(Color fill)
        {
            var box = new StyleBoxFlat { BgColor = fill, BorderColor = ToGodot(style.Color(UiColorId.Border)) };
            box.SetBorderWidthAll(Math.Max(1, style.Tokens.Window.BorderWidth / 2));
            box.SetCornerRadiusAll(Math.Max(1, style.Tokens.Window.CornerRadius / 2));
            return box;
        }

        // S1b: the track a light field, the fill its own colour (dark text stays readable on both, review #104); a value
        // in the bad range differs in brightness too (UiStandardRules.BrightnessStep), not only in hue.
        theme.SetStylebox("background", "ProgressBar", Box(ToGodot(style.Color(UiColorId.Field))));
        theme.SetStylebox("fill", "ProgressBar", Box(ToGodot(style.Color(UiColorId.Bar_fill))));
        theme.SetTypeVariation(BadBar, "ProgressBar");
        theme.SetStylebox("fill", BadBar, Box(ToGodot(style.Color(UiColorId.Bar_bad))));
        theme.SetColor("font_color", "ProgressBar", ToGodot(style.Color(UiColorId.Text)));
        theme.SetColor("font_outline_color", "ProgressBar", ToGodot(style.Color(UiColorId.Panel)));
        theme.SetConstant("outline_size", "ProgressBar", 0);

        // Foldable sections of the card (W1688): the engine default drew a dark grey box behind them (seen in
        // the first window shots of the zones preview); title and body now use the palette like everything else.
        StyleBoxFlat Title(Color fill)
        {
            var box = Box(fill);
            box.SetContentMarginAll(style.Tokens.Window.Padding / 4f);
            return box;
        }

        var strong = ToGodot(style.Color(UiColorId.Panel_strong));
        var hover = strong.Lerp(ToGodot(style.Color(UiColorId.Accent)), (float)style.Tokens.Button.HoverTint);
        foreach (var name in new[] { "title_panel", "title_collapsed_panel" })
        {
            theme.SetStylebox(name, "FoldableContainer", Title(strong));
        }

        foreach (var name in new[] { "title_hover_panel", "title_collapsed_hover_panel" })
        {
            theme.SetStylebox(name, "FoldableContainer", Title(hover));
        }

        var body = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0) };
        body.SetContentMarginAll(style.Tokens.Window.Padding / 4f);
        theme.SetStylebox("panel", "FoldableContainer", body);
        theme.SetStylebox("focus", "FoldableContainer", FocusRing(style));
        foreach (var name in new[] { "font_color", "hover_font_color", "collapsed_font_color" })
        {
            theme.SetColor(name, "FoldableContainer", ToGodot(style.Color(UiColorId.Text)));
        }
    }

    private static StyleBoxFlat FocusRing(UiStyle style)
    {
        var ring = new StyleBoxFlat { DrawCenter = false, BorderColor = ToGodot(style.Color(UiColorId.Focus)) };
        ring.SetBorderWidthAll(style.Tokens.Window.BorderWidth);
        ring.SetCornerRadiusAll(style.Tokens.Window.CornerRadius);
        return ring;
    }
}
