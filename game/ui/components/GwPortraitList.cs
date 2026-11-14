using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game.Ui.Components;

/// <summary>One row of the portrait list: who, the name shown, what it does now (finished text) and whether a question puzzles it.</summary>
/// <param name="Id">Inhabitant id.</param>
/// <param name="Name">Name as the screen shows it.</param>
/// <param name="Activity">What it does now, in the player's language.</param>
/// <param name="Brooding">A question still puzzles it (B2596): the portrait shows a "?".</param>
public sealed record PortraitEntry(string Id, string Name, string Activity, bool Brooding);

/// <summary>
/// A round portrait (A1682): the still picture of the figure inside a circle, or the name's first letter while
/// there is none (headless, placeholder world); a ring, thicker for the inhabitant whose card is open, and a "?"
/// on the edge while a question puzzles it. Drawn, so the circle needs no mask picture.
/// </summary>
public partial class GwPortrait : Control
{
    private Texture2D? _picture;
    private string _initial = "?";
    private bool _brooding;
    private bool _selected;

    /// <summary>Creates the portrait.</summary>
    public GwPortrait()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>Colour of the "?" (the theme's attention colour, set by the list).</summary>
    public Color Attention { get; set; } = new(1, 0.75f, 0.2f);

    /// <summary>Sets what the portrait shows; redraws only on a change.</summary>
    public void Show(Texture2D? picture, string initial, bool brooding, bool selected)
    {
        if (picture == _picture && initial == _initial && brooding == _brooding && selected == _selected)
        {
            return;
        }

        (_picture, _initial, _brooding, _selected) = (picture, initial, brooding, selected);
        QueueRedraw();
    }

    /// <summary>Whether the still picture is there (for checks and the tour).</summary>
    public bool HasPicture => _picture is not null;

    /// <inheritdoc/>
    public override void _Notification(int what)
    {
        if (what == NotificationThemeChanged)
        {
            QueueRedraw();
        }
    }

    /// <inheritdoc/>
    public override void _Draw()
    {
        var d = Mathf.Min(Size.X, Size.Y);
        if (d <= 2)
        {
            return;
        }

        var c = Size / 2;
        var r = d / 2;
        var text = GetThemeColor("font_color", "Label");
        var back = GetThemeStylebox("panel", UiTheme.WindowPanel) is StyleBoxFlat panel ? panel.BgColor with { A = 1 } : new Color(0.2f, 0.2f, 0.25f);
        const int Steps = 40;
        var points = new Vector2[Steps];
        var uvs = new Vector2[Steps];
        for (var i = 0; i < Steps; i++)
        {
            var a = Mathf.Tau * i / Steps;
            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            points[i] = c + (dir * r);
            uvs[i] = new Vector2(0.5f, 0.5f) + (dir * 0.5f);
        }

        DrawColoredPolygon(points, back);
        if (_picture is not null)
        {
            DrawPolygon(points, new[] { Colors.White }, uvs, _picture);
        }
        else
        {
            var font = GetThemeFont("font", "Label");
            var size = Math.Max(8, (int)(d * 0.5f));
            var width = font.GetStringSize(_initial, HorizontalAlignment.Left, -1, size).X;
            DrawString(font, new Vector2(c.X - (width / 2), c.Y + (font.GetAscent(size) / 2) - (size * 0.08f)), _initial, HorizontalAlignment.Left, -1, size, text);
        }

        DrawArc(c, r - 1, 0, Mathf.Tau, Steps, _selected ? Attention : text with { A = 0.6f }, _selected ? Mathf.Max(2, d * 0.07f) : Mathf.Max(1, d * 0.03f), true);
        if (_brooding)
        {
            var mark = c + new Vector2(r * 0.72f, -r * 0.72f);
            var mr = Mathf.Max(7, d * 0.2f);
            DrawCircle(mark, mr, Attention);
            var font = GetThemeFont("bold_font", "RichTextLabel") ?? GetThemeFont("font", "Label");
            var size = Math.Max(8, (int)(mr * 1.5f));
            var width = font.GetStringSize("?", HorizontalAlignment.Left, -1, size).X;
            DrawString(font, new Vector2(mark.X - (width / 2), mark.Y + (font.GetAscent(size) / 2) - (size * 0.1f)), "?", HorizontalAlignment.Left, -1, size, back);
        }
    }
}

/// <summary>
/// The corner top left (A1682, D957 v4 section 1): one row per inhabitant with round portrait, name and what it
/// does now; a "?" on the portrait while it broods. A click chooses it (card and camera). From the data's number
/// of inhabitants on the list shows portraits only, the name on hover, and scrolls; only the rows in view ask for
/// their still picture.
/// </summary>
public partial class GwPortraitList : PanelContainer
{
    private readonly ScrollContainer _scroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, Name = "Scroll" };
    private readonly VBoxContainer _rows = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill, Name = "Rows" };
    private readonly List<GwPortraitRow> _shown = [];
    private IReadOnlyList<PortraitEntry> _entries = [];
    private UiStyle? _style;
    private bool _compact;

    /// <summary>Creates the list.</summary>
    public GwPortraitList()
    {
        Name = "Portraits";
        ThemeTypeVariation = UiTheme.CompactPanel;
        _scroll.AddChild(_rows);
        AddChild(_scroll);
        _scroll.GetVScrollBar().ValueChanged += _ => AskForPictures();
    }

    /// <summary>A click on a row: the inhabitant is chosen.</summary>
    public event Action<string>? Chosen;

    /// <summary>A row in view has no still picture yet: the screen renders one.</summary>
    public event Action<string>? PictureWanted;

    /// <summary>The still picture of an inhabitant, or null (then the first letter shows).</summary>
    public Func<string, Texture2D?> PictureOf { get; set; } = _ => null;

    /// <summary>The inhabitant whose card is open (a thicker ring).</summary>
    public string? Selected { get; set; }

    /// <summary>Whether the list shows portraits only.</summary>
    public bool Compact => _compact;

    /// <summary>Number of rows (for checks and the tour).</summary>
    public int Count => _entries.Count;

    /// <summary>The row buttons in order (for checks and the tour).</summary>
    public IReadOnlyList<Button> RowButtons => [.. _shown.Take(_entries.Count)];

    /// <summary>How many rows show their still picture (the rest show a letter; for checks and the tour).</summary>
    public int PicturesShown => _shown.Take(_entries.Count).Count(r => r.HasPicture);

    /// <summary>Height of all rows (the list asks for at most a share of the free height; more scrolls).</summary>
    public float ContentHeight => _rows.GetCombinedMinimumSize().Y;

    /// <summary>Width the list asks for: the data's width with names, the portrait alone when compact.</summary>
    public float WantedWidth
    {
        get
        {
            if (_style is null)
            {
                return 0;
            }

            var frame = GetThemeStylebox("panel") is { } box ? box.GetContentMargin(Side.Left) + box.GetContentMargin(Side.Right) : 0;
            var bar = _scroll.GetVScrollBar().GetCombinedMinimumSize().X;
            return _compact
                ? _style.Tokens.Zones.CompactPortraitPx + frame + bar + (2 * _style.Tokens.Window.Gap)
                : (float)(_style.Tokens.Zones.PortraitListWidth * Math.Max(1, _style.Settings.TextScale));
        }
    }

    /// <summary>Takes the sizes of a (new) style.</summary>
    public void Setup(UiStyle style)
    {
        _style = style ?? throw new ArgumentNullException(nameof(style));
        foreach (var row in _shown)
        {
            row.Resize(style, _compact);
        }

        Fill();
    }

    /// <summary>Shows the rows; nodes are kept and refilled, so a refresh every quarter second costs little.</summary>
    public void SetRows(IReadOnlyList<PortraitEntry> entries)
    {
        _entries = entries ?? throw new ArgumentNullException(nameof(entries));
        if (_style is not null)
        {
            var compact = CornerModel.Compact(entries.Count, _style.Tokens.Zones);
            if (compact != _compact)
            {
                _compact = compact;
                foreach (var row in _shown)
                {
                    row.Resize(_style, _compact);
                }
            }
        }

        Fill();
    }

    /// <summary>Asks again for the pictures of the rows in view (after a picture arrived or the list was scrolled).</summary>
    public void Redraw() => Fill();

    private void Fill()
    {
        if (_style is null)
        {
            return;
        }

        while (_shown.Count < _entries.Count)
        {
            var row = new GwPortraitRow { Name = $"Portrait{_shown.Count}" };
            var index = _shown.Count;
            row.Pressed += () => Click(index);
            row.Resize(_style, _compact);
            _rows.AddChild(row);
            _shown.Add(row);
        }

        var attention = UiTheme.ToGodot(_style.Color(GlassWorld.Contracts.UiColorId.Attention));
        for (var i = 0; i < _shown.Count; i++)
        {
            var row = _shown[i];
            row.Visible = i < _entries.Count;
            if (i < _entries.Count)
            {
                var entry = _entries[i];
                row.Fill(entry, PictureOf(entry.Id), entry.Id == Selected, _compact, attention);
            }
        }

        AskForPictures();
    }

    private void AskForPictures()
    {
        if (_entries.Count == 0 || _shown.Count == 0)
        {
            return;
        }

        var rowPx = _shown[0].Size.Y + _rows.GetThemeConstant("separation");
        var (first, last) = CornerModel.InView(_entries.Count, Math.Max(1, rowPx), _scroll.Size.Y > 0 ? _scroll.Size.Y : rowPx * _entries.Count, _scroll.ScrollVertical);
        for (var i = first; i <= last; i++)
        {
            if (PictureOf(_entries[i].Id) is null)
            {
                PictureWanted?.Invoke(_entries[i].Id);
            }
        }
    }

    private void Click(int index)
    {
        if (index < _entries.Count)
        {
            Chosen?.Invoke(_entries[index].Id);
        }
    }
}

/// <summary>One row of the portrait list: a flat button holding portrait, name and activity (the parts let the mouse through).</summary>
public partial class GwPortraitRow : Button
{
    private readonly HBoxContainer _line = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly GwPortrait _portrait = new();
    private readonly VBoxContainer _texts = new() { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
    private readonly Label _name = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MouseFilter = MouseFilterEnum.Ignore };
    private readonly Label _activity = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MouseFilter = MouseFilterEnum.Ignore, ThemeTypeVariation = UiTheme.SmallLabel };

    /// <summary>Creates the row.</summary>
    public GwPortraitRow()
    {
        Flat = true;
        AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        FocusMode = FocusModeEnum.All;
        _texts.AddChild(_name);
        _texts.AddChild(_activity);
        _line.AddChild(_portrait);
        _line.AddChild(_texts);
        AddChild(_line);
        _line.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _line.AddThemeConstantOverride("separation", 8);
    }

    /// <summary>Whether the portrait shows its still picture.</summary>
    public bool HasPicture => _portrait.HasPicture;

    /// <summary>Sizes of a style: portrait diameter, names shown or not.</summary>
    public void Resize(UiStyle style, bool compact)
    {
        ArgumentNullException.ThrowIfNull(style);
        var px = compact ? style.Tokens.Zones.CompactPortraitPx : style.Tokens.Zones.PortraitPx;
        _portrait.CustomMinimumSize = new Vector2(px, px);
        _texts.Visible = !compact;

        // The texts may shrink (an ellipsis ends a long name): the row is as high as the portrait or the two lines.
        var lines = compact ? 0 : _name.GetCombinedMinimumSize().Y + _activity.GetCombinedMinimumSize().Y;
        CustomMinimumSize = new Vector2(px, Math.Max(px, lines) + 4);
    }

    /// <summary>Shows an inhabitant.</summary>
    public void Fill(PortraitEntry entry, Texture2D? picture, bool selected, bool compact, Color attention)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _name.Text = entry.Name;
        _activity.Text = entry.Activity;
        _portrait.Attention = attention;
        _portrait.Show(picture, CornerModel.Initial(entry.Name), entry.Brooding, selected);
        var tip = TextFill.Fill(Tr("ui.corner.portrait_tip"), new Dictionary<string, string> { ["name"] = entry.Name, ["activity"] = entry.Activity });
        TooltipText = entry.Brooding ? tip + "\n" + Tr("ui.corner.brooding") : compact ? tip : string.Empty;
    }
}
