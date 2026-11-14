using GlassWorld.Contracts;
using Godot;

namespace GlassWorld.Game.Ui.Components;

/// <summary>
/// A round symbol button of the corner top right (A1682, D957 v4 section 1): menu, questions, mail, chronicle,
/// inhabitants. The frame is the theme's button frame with round corners; a small count (open questions, open
/// mail) sits on its upper right edge instead of hints that vanish. The tooltip names what it opens.
/// </summary>
public partial class GwRoundButton : Button
{
    private readonly GwIcon _symbol = new();
    private readonly Label _badge = new()
    {
        Name = "Badge",
        Visible = false,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        MouseFilter = MouseFilterEnum.Ignore,
        ThemeTypeVariation = UiTheme.SmallLabel,
    };

    private float _diameter = 44;
    private bool _styling;

    /// <summary>Creates the button.</summary>
    public GwRoundButton()
    {
        ClipText = true;
        FocusMode = FocusModeEnum.All;
        AddChild(_symbol);
        AddChild(_badge);
        Resized += Place;
    }

    /// <summary>Sets symbol, tooltip key and size (diameter at the reference size, times the UI size by the content scale).</summary>
    public void Setup(UiIconChoiceIcon icon, string tooltipKey, float diameter)
    {
        _symbol.Icon = icon;
        TooltipText = tooltipKey;
        _diameter = diameter;
        CustomMinimumSize = new Vector2(diameter, diameter);
        Restyle();
    }

    /// <summary>The count shown on the edge; empty hides it.</summary>
    public string Count
    {
        get => _badge.Text;
        set
        {
            _badge.Text = value;
            _badge.Visible = value.Length > 0;
            Place();
        }
    }

    /// <summary>The symbol drawn (for the UI tour and checks).</summary>
    public UiIconChoiceIcon Symbol => _symbol.Icon;

    /// <inheritdoc/>
    public override void _Notification(int what)
    {
        if (what == NotificationThemeChanged && !_styling)
        {
            Restyle();
        }
    }

    /// <summary>The theme's button frames with corners as round as the button (a new theme brings new frames).</summary>
    private void Restyle()
    {
        _styling = true;
        try
        {
            foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "focus", "disabled" })
            {
                RemoveThemeStyleboxOverride(state);
                if (GetThemeStylebox(state, "Button") is StyleBoxFlat flat)
                {
                    var round = (StyleBoxFlat)flat.Duplicate();
                    round.SetCornerRadiusAll((int)Math.Ceiling(_diameter / 2));
                    round.SetContentMarginAll(0);
                    AddThemeStyleboxOverride(state, round);
                }
            }

            var badge = new StyleBoxFlat { BgColor = GetThemeColor("font_color", "Button") };
            badge.SetCornerRadiusAll(64);
            badge.SetContentMarginAll(2);
            badge.ContentMarginLeft = badge.ContentMarginRight = 5;
            _badge.AddThemeStyleboxOverride("normal", badge);
            _badge.AddThemeColorOverride("font_color", GetThemeStylebox("normal", "Button") is StyleBoxFlat normal ? normal.BgColor with { A = 1 } : Colors.White);
        }
        finally
        {
            _styling = false;
        }

        Place();
    }

    /// <summary>Position only (trap f of GW3 #25014): the symbol fills the inner circle, the count sits on the upper right edge.</summary>
    private void Place()
    {
        var inner = Size * 0.6f;
        _symbol.Size = inner;
        _symbol.Position = (Size - inner) / 2;
        if (_badge.Visible)
        {
            _badge.Size = _badge.GetCombinedMinimumSize();
            _badge.Position = new Vector2(Size.X - (_badge.Size.X * 0.7f), -_badge.Size.Y * 0.3f);
        }
    }
}
