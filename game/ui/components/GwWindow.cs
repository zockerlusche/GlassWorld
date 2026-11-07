using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game.Ui.Components;

/// <summary>
/// The one window building block of the UI standard (D366 section 8): driftwood frame, header with
/// title key and close button, scrollable content, optional footer with actions. Non-modal, moved by
/// dragging the header, never leaves the safe area and never covers more of the screen than allowed.
/// Esc closes the topmost window.
/// </summary>
public partial class GwWindow : PanelContainer
{
    private const string CloseSymbol = "×";

    private readonly Label _title = new() { ThemeTypeVariation = UiTheme.HeaderLabel, ClipText = false };
    private readonly Button _close = new() { Text = CloseSymbol, TooltipText = "ui.common.close", FocusMode = FocusModeEnum.All };
    private readonly ScrollContainer _scroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
    private readonly HBoxContainer _footer = new() { Alignment = BoxContainer.AlignmentMode.End, Visible = false };
    private UiStyle? _style;
    private bool _dragging;

    /// <summary>Raised when the player closes the window.</summary>
    [Signal]
    public delegate void ClosedEventHandler();

    /// <summary>Container for the window's content (inside the scroll area).</summary>
    public VBoxContainer Content { get; } = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };

    /// <summary>Creates an empty window; call <see cref="Setup"/> before adding it to the tree.</summary>
    public GwWindow()
    {
        ThemeTypeVariation = UiTheme.WindowPanel;
        var column = new VBoxContainer();
        var header = new HBoxContainer();
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        header.AddChild(_title);
        header.AddChild(_close);
        header.GuiInput += OnHeaderInput;
        _close.Pressed += Close;
        _scroll.AddChild(Content);
        column.AddChild(header);
        column.AddChild(_scroll);
        column.AddChild(_footer);
        AddChild(column);
    }

    /// <summary>Sets title key and style (tokens for safe area and maximum size).</summary>
    public void Setup(string titleKey, UiStyle style)
    {
        _title.Text = titleKey;
        _style = style;
    }

    /// <summary>
    /// Sets a title that is already translated and filled (e.g. "Inhabitant 3"); it is not treated as
    /// a key any more.
    /// </summary>
    public void SetTitleText(string text)
    {
        _title.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        _title.Text = text;
    }

    /// <summary>Removes all content (for views that rebuild themselves).</summary>
    public void ClearContent()
    {
        foreach (var child in Content.GetChildren())
        {
            Content.RemoveChild(child);
            child.QueueFree();
        }
    }

    /// <summary>Adds an action button to the footer; the main action goes last (right).</summary>
    public Button AddAction(string textKey)
    {
        var button = new Button { Text = textKey };
        _footer.AddChild(button);
        _footer.Visible = true;
        return button;
    }

    /// <summary>Closes the window.</summary>
    public void Close()
    {
        EmitSignal(SignalName.Closed);
        QueueFree();
    }

    /// <summary>
    /// Sizes the window to its content within the allowed share of the parent area and centres it;
    /// content that does not fit scrolls vertically.
    /// </summary>
    public void FitInto(Vector2 area)
    {
        if (_style is null)
        {
            return;
        }

        var limit = area * (float)_style.Tokens.Window.MaxScreenFraction;
        var safe = area - (Vector2.One * 2 * _style.Tokens.Window.SafeMargin);
        limit = new Vector2(Math.Min(limit.X, safe.X), Math.Min(limit.Y, safe.Y));
        // Measure with the title on one line; wrap it only if it cannot fit the allowed width.
        _title.AutowrapMode = TextServer.AutowrapMode.Off;
        var wanted = GetCombinedMinimumSize() + new Vector2(0, Content.GetCombinedMinimumSize().Y);
        if (wanted.X > limit.X)
        {
            _title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            wanted = GetCombinedMinimumSize() + new Vector2(0, Content.GetCombinedMinimumSize().Y);
        }

        Size = new Vector2(Math.Min(wanted.X, limit.X), Math.Min(wanted.Y, limit.Y));
        Position = ((area - Size) / 2).Floor();
    }

    /// <summary>True if the window lies fully inside the safe area of <paramref name="area"/>.</summary>
    public bool IsInsideSafeArea(Vector2 area)
    {
        var margin = _style?.Tokens.Window.SafeMargin ?? 0;
        return Position.X >= margin - 0.5f && Position.Y >= margin - 0.5f
            && Position.X + Size.X <= area.X - margin + 0.5f && Position.Y + Size.Y <= area.Y - margin + 0.5f;
    }

    /// <summary>Empty if the header (title and close button) fits without overlapping or clipping, else why not.</summary>
    public string HeaderProblem()
    {
        var window = GetGlobalRect();
        var close = _close.GetGlobalRect();
        if (_title.Size.X < 1)
        {
            return $"title has no width (window {window.Size})";
        }

        if (close.End.X > window.End.X + 0.5f || close.End.Y > window.End.Y + 0.5f)
        {
            return $"close button {close} outside window {window}";
        }

        return _title.GetLineCount() > 2 ? $"title needs {_title.GetLineCount()} lines" : string.Empty;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel") && IsTopmost())
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    private bool IsTopmost() =>
        GetParent() is { } parent && parent.GetChildren().OfType<GwWindow>().LastOrDefault() == this;

    private void OnHeaderInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                _dragging = button.Pressed;
                break;
            case InputEventMouseMotion motion when _dragging && GetParent() is Control parent:
                var margin = _style?.Tokens.Window.SafeMargin ?? 0;
                var max = parent.Size - Size - (Vector2.One * margin);
                Position = (Position + motion.Relative).Clamp(Vector2.One * margin, max);
                break;
            default:
                break;
        }
    }
}
