using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game.Ui.Components;

/// <summary>
/// The hint area (D366 section 9) at the bottom right: shows the <see cref="ToastBoard"/> – at most
/// three calm hints, no sound, no blinking; the mouse on a hint stops its time; each hint has a
/// focusable close button, and the Delete key (action <c>gw_dismiss_hint</c>) closes the oldest one.
/// Time runs in real seconds, independent of game speed and pause.
/// </summary>
public partial class GwToasts : VBoxContainer
{
    private readonly Dictionary<long, (PanelContainer Panel, Label Label, int Count)> _shown = [];
    private long? _hovered;
    private long _version = -1;

    /// <summary>Raised when the player presses the "look" button of a hint (its inhabitant id).</summary>
    public event Action<string>? LookRequested;

    /// <summary>The board this area shows.</summary>
    public ToastBoard? Board { get; set; }

    /// <summary>Creates the area.</summary>
    public GwToasts()
    {
        Alignment = AlignmentMode.End;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta)
    {
        if (Board is null)
        {
            return;
        }

        Board.Advance(delta, _hovered);
        if (Board.Version != _version)
        {
            Sync();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // Keyboard players can close hints too (XAG 116), oldest first.
        if (Board is not null && @event.IsActionPressed(DismissAction))
        {
            Board.DismissOldest();
            GetViewport().SetInputAsHandled();
        }
    }

    private const string CloseSymbol = "×";

    /// <summary>Input action that closes the oldest hint (bound in the project's input map).</summary>
    public const string DismissAction = "gw_dismiss_hint";

    public override void _Notification(int what)
    {
        // Hint texts are translated once (AI-free, but filled); after a language switch set them again.
        if (what == NotificationTranslationChanged && Board is not null)
        {
            foreach (var toast in Board.Visible.Where(t => _shown.ContainsKey(t.Id)))
            {
                _shown[toast.Id].Label.Text = Text(toast);
            }
        }
    }

    /// <summary>Rebuilds the shown hints from the board (also used by the layout check).</summary>
    public void Sync()
    {
        if (Board is null)
        {
            return;
        }

        _version = Board.Version;
        var visible = Board.Visible;
        foreach (var gone in _shown.Keys.Except(visible.Select(t => t.Id)).ToList())
        {
            _shown[gone].Panel.QueueFree();
            _shown.Remove(gone);
            if (_hovered == gone)
            {
                _hovered = null;
            }
        }

        foreach (var toast in visible.Where(t => !_shown.ContainsKey(t.Id)))
        {
            var panel = new PanelContainer { ThemeTypeVariation = UiTheme.TooltipPanel, MouseFilter = MouseFilterEnum.Stop };
            var row = new HBoxContainer();
            var label = new Label
            {
                Text = Text(toast),
                AutoTranslateMode = AutoTranslateModeEnum.Disabled,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            var id = toast.Id;
            var close = new Button { Text = CloseSymbol, TooltipText = "ui.common.close", FocusMode = FocusModeEnum.All };
            close.Pressed += () => Board.Dismiss(id);
            row.AddChild(label);
            if (toast.Message.LookAt is { } look)
            {
                // Only this button moves the camera to an event (TL #19976).
                var see = new Button { Text = "ui.toast.look", Name = "Look", FocusMode = FocusModeEnum.All };
                see.Pressed += () =>
                {
                    LookRequested?.Invoke(look);
                    Board.Dismiss(id);
                };
                row.AddChild(see);
            }

            row.AddChild(close);
            panel.AddChild(row);
            panel.MouseEntered += () => _hovered = id;
            panel.MouseExited += () => _hovered = _hovered == id ? null : _hovered;
            panel.GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                {
                    Board.Dismiss(id);
                }
            };
            AddChild(panel);
            _shown[id] = (panel, label, toast.Count);
        }

        foreach (var toast in visible)
        {
            var shown = _shown[toast.Id];
            if (shown.Count != toast.Count)
            {
                shown.Label.Text = Text(toast);
                _shown[toast.Id] = shown with { Count = toast.Count };
            }
        }
    }

    private string Text(VisibleToast toast)
    {
        if (toast.Count > 1 && toast.Message.ManyKey is { } many)
        {
            return TextFill.Fill(Tr(many), toast.Message.Args);
        }

        var text = TextFill.Fill(Tr(toast.Message.TextKey), toast.Message.Args);
        return toast.Count > 1
            ? TextFill.Fill(Tr("ui.toast.bundled"), new Dictionary<string, string> { ["text"] = text, ["count"] = toast.Count.ToString(System.Globalization.CultureInfo.CurrentCulture) })
            : text;
    }
}

/// <summary>
/// A speech or thought bubble over an inhabitant (D366 sections 3 and 12): driftwood bubble frame,
/// text in the player's language generated by the AI – never treated as a key – wrapped at about
/// <c>bubble_max_chars</c> characters; follows its anchor in the world.
/// </summary>
public partial class GwBubble : PanelContainer
{
    private readonly Label _text = new()
    {
        AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
    };

    /// <summary>Creates the bubble.</summary>
    public GwBubble()
    {
        ThemeTypeVariation = UiTheme.BubblePanel;
        MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_text);
    }

    /// <summary>
    /// Shows a text (already in the player's language) wrapped at the token line length, never wider
    /// than the allowed share of <paramref name="area"/> (logical pixels).
    /// </summary>
    public void Show(string text, UiStyle style, Vector2 area)
    {
        ArgumentNullException.ThrowIfNull(style);
        _text.Text = text;
        var font = _text.GetThemeFont("font");
        var size = _text.GetThemeFontSize("font_size");
        var sample = new string('n', style.Tokens.Window.BubbleMaxChars);
        var limit = Math.Min(font.GetStringSize(sample, HorizontalAlignment.Left, -1, size).X, (area.X * (float)style.Tokens.Window.MaxScreenFraction) - (2 * style.Tokens.Window.SafeMargin));
        _text.CustomMinimumSize = new Vector2(Math.Min(font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X, limit), 0);
        // A wrapping label measures its height at its current width; shrink again once laid out.
        ResetSize();
        CallDeferred(Control.MethodName.ResetSize);
    }

    /// <summary>Places the bubble above a point in the world; hidden when the point is behind the camera.</summary>
    public void Follow(Camera3D camera, Vector3 anchor)
    {
        ArgumentNullException.ThrowIfNull(camera);
        Visible = !camera.IsPositionBehind(anchor);
        if (Visible)
        {
            var screen = camera.UnprojectPosition(anchor);
            Position = (screen - new Vector2(Size.X / 2, Size.Y)).Floor();
        }
    }

    /// <summary>Lines the text needs (for the layout check).</summary>
    public int LineCount() => _text.GetLineCount();
}
