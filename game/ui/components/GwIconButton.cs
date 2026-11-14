using GlassWorld.Contracts;
using Godot;

namespace GlassWorld.Game.Ui.Components;

/// <summary>
/// A button that shows a drawn symbol instead of text (A1682: the speeds as symbols, D957 v4 section 1). The
/// tooltip says what it does; the frame is the theme's button frame.
/// </summary>
public partial class GwIconButton : Button
{
    private readonly GwIcon _symbol = new();

    /// <summary>Creates the button.</summary>
    public GwIconButton()
    {
        AddChild(_symbol);
        Resized += Place;
    }

    /// <summary>Sets the symbol (and the arrows of a fast speed) and the size of the button.</summary>
    public void Setup(UiIconChoiceIcon icon, int arrows, Vector2 size)
    {
        _symbol.Icon = icon;
        _symbol.Arrows = arrows;
        CustomMinimumSize = size;
        Place();
    }

    /// <summary>The symbol drawn (for the UI tour and checks).</summary>
    public UiIconChoiceIcon Symbol => _symbol.Icon;

    /// <summary>Position only: the symbol fills the inner part of the button.</summary>
    private void Place()
    {
        var inner = new Vector2(Size.X * 0.8f, Size.Y * 0.62f);
        _symbol.Size = inner;
        _symbol.Position = (Size - inner) / 2;
    }
}
