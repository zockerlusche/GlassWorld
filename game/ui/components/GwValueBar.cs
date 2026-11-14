using GlassWorld.Contracts;
using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game.Ui.Components;

/// <summary>
/// A value bar after the one rule (B2642, D772): the length shows how much of what the label names is there; in the
/// bad range the bar takes the warning colour and a thumb down, a good value a thumb up. The thumb has its place
/// even when none shows, so rows do not jump. The percentage stays small on the bar.
/// </summary>
public partial class GwValueBar : HBoxContainer
{
    private readonly Label _label = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly ProgressBar _bar = new() { MinValue = 0, MaxValue = 100, ShowPercentage = true, SizeFlagsVertical = SizeFlags.ShrinkCenter };
    private readonly GwIcon _thumb = new() { Name = "Thumb" };

    /// <summary>Creates the bar.</summary>
    public GwValueBar()
    {
        AddChild(_label);
        AddChild(_bar);
        AddChild(_thumb);
    }

    /// <summary>What the bar is about (set once).</summary>
    public BarKind Kind { get; private set; }

    /// <summary>How the bar looks now (for the UI tour and checks).</summary>
    public BarLook Look { get; private set; } = BarLook.Calm;

    /// <summary>The bar itself.</summary>
    public ProgressBar Bar => _bar;

    /// <summary>
    /// Label, kind and sizes. <paramref name="translate"/> false for a name that is already in the player's language
    /// (a need an invention brought).
    /// </summary>
    public void Setup(string text, bool translate, BarKind kind, float barWidth, float thumbPx)
    {
        _label.Text = text;
        _label.AutoTranslateMode = translate ? AutoTranslateModeEnum.Inherit : AutoTranslateModeEnum.Disabled;
        Kind = kind;
        _bar.CustomMinimumSize = new Vector2(barWidth, 0);
        _thumb.CustomMinimumSize = new Vector2(thumbPx, thumbPx);
        _thumb.SizeFlagsVertical = SizeFlags.ShrinkCenter;
    }

    /// <summary>Shows a value (0-100) with its look; the colours are the palette's 'positive' and 'attention'.</summary>
    public void Show(int percent, BarLook look, Color good, Color bad)
    {
        if ((int)_bar.Value != percent)
        {
            _bar.Value = percent;
        }

        Look = look;
        _bar.ThemeTypeVariation = look == BarLook.Bad ? UiTheme.BadBar : string.Empty;
        _thumb.Icon = look == BarLook.Bad ? UiIconChoiceIcon.Thumb_down : UiIconChoiceIcon.Thumb_up;
        _thumb.Tint = look == BarLook.Bad ? bad : good;
        _thumb.Modulate = look == BarLook.Calm ? Colors.Transparent : Colors.White;
        _thumb.QueueRedraw();
    }
}
