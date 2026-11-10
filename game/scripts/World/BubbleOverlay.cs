using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game.World;

/// <summary>
/// The point above a head where an inhabitant's thought or said sentence belongs. It holds the text; the
/// <see cref="BubbleOverlay"/> draws it on screen. Visible = shown now (one line per inhabitant).
/// </summary>
public partial class ScreenBubble : Node3D
{
    /// <summary>The text shown.</summary>
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Draws the shown <see cref="ScreenBubble"/>s as screen text (W1687): one per inhabitant, laid out with
/// <see cref="BubbleLayout"/> so two never lie on top of each other, kept inside the free area (below the top
/// bar, beside the side panel, within the window). Presentation only.
/// </summary>
public partial class BubbleOverlay : CanvasLayer
{
    private readonly Dictionary<string, Label> _labels = new(StringComparer.Ordinal);
    private readonly List<BubbleRequest> _requests = [];
    private List<string> _order = [];
    private readonly Dictionary<string, (int FontSize, float Width)> _textWidth = new(StringComparer.Ordinal);
    private readonly Control _root = new() { Name = "BubbleRoot", MouseFilter = Control.MouseFilterEnum.Ignore };

    /// <summary>The bubbles by inhabitant id.</summary>
    public Func<IEnumerable<(string Id, ScreenBubble Bubble)>> Bubbles { get; set; } = () => [];

    /// <summary>The free screen area (set by the game screen); the whole window when null.</summary>
    public Func<Rect2?> Area { get; set; } = () => null;

    /// <summary>Largest bubble width in pixels.</summary>
    public Func<float> WidthPx { get; set; } = () => 440;

    /// <summary>Two heads within this many pixels of the same height keep their bubbles' order.</summary>
    public Func<float> OrderHysteresisPx { get; set; } = () => 0;

    /// <summary>Gap between stacked bubbles in pixels.</summary>
    public float GapPx { get; set; } = 6;

    /// <summary>Smoke only: the screen rectangles of the bubbles shown now.</summary>
    public IReadOnlyList<(string Id, Rect2 Box)> Shown() =>
        [.. _labels.Where(l => l.Value.Visible).Select(l => (l.Key, new Rect2(l.Value.Position, l.Value.Size)))];

    // Below the UI (layer 0): windows and the pause menu cover the bubbles, the world does not.
    public override void _Ready()
    {
        Layer = -1;

        // The game's UI theme (fonts, sizes) also below this layer.
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        GlassWorld.Game.Ui.UiSetup.Bind(_root);
    }

    public override void _Process(double delta)
    {
        var camera = GetViewport()?.GetCamera3D();
        var view = GetViewport()?.GetVisibleRect() ?? new Rect2();
        var area = Area() ?? view;
        _requests.Clear();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, bubble) in Bubbles())
        {
            var label = LabelOf(id);
            seen.Add(id);
            // An inhabitant outside the view shows no bubble (review GW1 #152): clamped to the edge it would
            // point at nobody.
            var show = camera is not null && bubble.IsVisibleInTree() && bubble.Text.Length > 0 && !camera.IsPositionBehind(bubble.GlobalPosition)
                && view.HasPoint(camera.UnprojectPosition(bubble.GlobalPosition));
            label.Visible = show;
            if (!show)
            {
                continue;
            }

            // The text's natural width, measured once per new text or font size (many figures, review GW1 #152).
            var fontSize = label.GetThemeFontSize("font_size");
            if (label.Text != bubble.Text || !_textWidth.TryGetValue(id, out var known) || known.FontSize != fontSize)
            {
                label.Text = bubble.Text;
                known = (fontSize, label.GetThemeFont("font").GetStringSize(bubble.Text, HorizontalAlignment.Left, -1, fontSize).X + 8);
                _textWidth[id] = known;
            }

            // Width: as wide as the text needs, at most the data's width; height follows the wrapping.
            var width = Math.Min(WidthPx(), known.Width);
            label.CustomMinimumSize = new Vector2(width, 0);
            label.Size = new Vector2(width, 0);
            var height = label.GetCombinedMinimumSize().Y;
            var at = camera!.UnprojectPosition(bubble.GlobalPosition);
            _requests.Add(new BubbleRequest(id, at.X, at.Y, width, height));
        }

        foreach (var gone in _labels.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _labels[gone].QueueFree();
            _labels.Remove(gone);
            _textWidth.Remove(gone);
        }

        var places = BubbleLayout.Place(_requests, (area.Position.X, area.Position.Y, area.End.X, area.End.Y), GapPx, _order, OrderHysteresisPx());
        _order = [.. places.Select(p => p.Id)];
        foreach (var place in places)
        {
            var label = _labels[place.Id];
            label.Position = new Vector2((float)place.Left, (float)place.Top);
        }
    }

    private Label LabelOf(string id)
    {
        if (_labels.TryGetValue(id, out var label))
        {
            return label;
        }

        label = new Label
        {
            Name = "Bubble_" + id,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };

        // Readable over grass, sand and water: light text with a dark outline (as before, now in screen space).
        label.AddThemeColorOverride("font_color", new Color(1, 1, 1));
        label.AddThemeColorOverride("font_outline_color", new Color(0.1f, 0.1f, 0.14f));
        label.AddThemeConstantOverride("outline_size", 6);
        _root.AddChild(label);
        _labels[id] = label;
        return label;
    }
}
