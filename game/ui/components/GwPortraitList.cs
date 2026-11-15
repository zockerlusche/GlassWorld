using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game.Ui.Components;

/// <summary>One row of the portrait list: who, the name shown, what it does now (finished text) and the state its portrait shows.</summary>
/// <param name="Id">Inhabitant id.</param>
/// <param name="Name">Name as the screen shows it.</param>
/// <param name="Activity">What it does now, in the player's language.</param>
/// <param name="State">What the portrait shows (A1752, B2792): knock, worry, aha, a question (B2596) or nothing.</param>
public sealed record PortraitEntry(string Id, string Name, string Activity, PortraitState State);

/// <summary>
/// A round portrait (A1682): the still picture of the figure inside a circle, or the name's first letter while
/// there is none (headless, placeholder world); a ring, thicker for the inhabitant whose card is open, and the one state
/// it shows (A1752, B2792) as a badge on its edge - sound arcs for a knock (and a ring pulsing twice short, then a
/// pause), "!" for worry, a star for an aha (the ring lights up once, then stays calmly light), "?" for a question.
/// Told apart by shape and motion, never by colour alone (Marcel is colour-blind); with reduced motion nothing moves.
/// Drawn, so the circle needs no mask picture.
/// </summary>
public partial class GwPortrait : Control
{
    private Texture2D? _picture;
    private string _initial = "?";
    private PortraitState _state;
    private bool _selected;
    private double _stateSeconds;

    /// <summary>Creates the portrait.</summary>
    public GwPortrait()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetProcess(false);
    }

    /// <summary>Colour of the badges and state rings (the theme's attention colour, set by the list).</summary>
    public Color Attention { get; set; } = new(1, 0.75f, 0.2f);

    /// <summary>The states' rhythm, sizes and durations (tokens portrait_states); null = badges only, nothing moves.</summary>
    public GlassWorld.Contracts.UiPortraitStates? States { get; set; }

    /// <summary>Reduced motion (player setting): the knock ring stands still, the aha does not flash.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>The state shown now (for checks and the tour).</summary>
    public PortraitState State => _state;

    /// <summary>Sets what the portrait shows; redraws only on a change. A new state starts its motion from the beginning.</summary>
    public void Show(Texture2D? picture, string initial, PortraitState state, bool selected)
    {
        if (picture == _picture && initial == _initial && state == _state && selected == _selected)
        {
            return;
        }

        if (state != _state)
        {
            _stateSeconds = 0;
        }

        (_picture, _initial, _state, _selected) = (picture, initial, state, selected);
        SetProcess(Moves);
        QueueRedraw();
    }

    /// <summary>Whether the still picture is there (for checks and the tour).</summary>
    public bool HasPicture => _picture is not null;

    // Only a pulsing knock and an aha's one flash move; everything else is drawn once.
    private bool Moves =>
        !ReduceMotion && States is { } data
        && (_state == PortraitState.Knock || (_state == PortraitState.Aha && _stateSeconds < data.AhaFlashS));

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        _stateSeconds += delta;
        QueueRedraw();
        if (!Moves)
        {
            SetProcess(false);
        }
    }

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
        DrawStateRing(c, r, d, Steps);
        if (_state != PortraitState.None)
        {
            DrawBadge(c, r, d, back);
        }
    }

    // The knock ring pulses (thicker and stronger at each tap); the aha ring flashes once wide, then stays calmly light;
    // worry and question have no ring of their own.
    private void DrawStateRing(Vector2 c, float r, float d, int steps)
    {
        var data = States;
        var t = Mathf.Max(2, d * (float)(data?.RingShare ?? 0.09));
        if (_state == PortraitState.Knock)
        {
            var p = data is null ? 1 : (float)CornerModel.KnockPulse(data, _stateSeconds, ReduceMotion);
            DrawArc(c, r + (t * 0.3f * p), 0, Mathf.Tau, steps, Attention with { A = 0.45f + (0.55f * p) }, t * (0.5f + (0.5f * p)), true);
        }
        else if (_state == PortraitState.Aha)
        {
            var flash = data is null || ReduceMotion || data.AhaFlashS <= 0 ? 0 : Mathf.Max(0, 1 - (float)(_stateSeconds / data.AhaFlashS));
            DrawArc(c, r + (t * 0.3f * flash), 0, Mathf.Tau, steps, Colors.White with { A = 0.75f + (0.25f * flash) }, t * (1 + flash), true);
        }
    }

    // The badge on the upper right edge: a filled circle in the attention colour, the state's shape in the ground colour.
    private void DrawBadge(Vector2 c, float r, float d, Color ink)
    {
        var mr = Mathf.Max(7, d * (float)(States?.BadgeShare ?? 0.4) / 2);
        var mark = c + new Vector2(r * 0.72f, -r * 0.72f);
        DrawCircle(mark, mr, Attention);
        switch (_state)
        {
            case PortraitState.Knock:
                // Sound arcs: a dot where the knock is and two arcs opening to the right.
                var source = mark - new Vector2(mr * 0.35f, 0);
                DrawCircle(source, mr * 0.16f, ink);
                for (var i = 1; i <= 2; i++)
                {
                    DrawArc(source, mr * 0.3f * i, -0.9f, 0.9f, 8, ink, Mathf.Max(1.5f, mr * 0.13f), true);
                }

                break;
            case PortraitState.Aha:
                // A five-pointed star.
                var star = new Vector2[10];
                for (var i = 0; i < 10; i++)
                {
                    var a = (-Mathf.Pi / 2) + (i * Mathf.Pi / 5);
                    star[i] = mark + (new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (i % 2 == 0 ? mr * 0.78f : mr * 0.33f));
                }

                DrawColoredPolygon(star, ink);
                break;
            default:
                // "!" for worry, "?" for a question.
                var glyph = _state == PortraitState.Worry ? "!" : "?";
                var font = GetThemeFont("bold_font", "RichTextLabel") ?? GetThemeFont("font", "Label");
                var size = Math.Max(8, (int)(mr * 1.5f));
                var width = font.GetStringSize(glyph, HorizontalAlignment.Left, -1, size).X;
                DrawString(font, new Vector2(mark.X - (width / 2), mark.Y + (font.GetAscent(size) / 2) - (size * 0.1f)), glyph, HorizontalAlignment.Left, -1, size, ink);
                break;
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

    // B2749: with many inhabitants the corner shows only the important ones; '+N' opens the window with all of them.
    private readonly Button _more = new() { Name = "More", Flat = true, Visible = false, AutoTranslateMode = AutoTranslateModeEnum.Disabled, FocusMode = FocusModeEnum.All };
    private int _moreCount;

    /// <summary>Creates the list.</summary>
    public GwPortraitList()
    {
        Name = "Portraits";
        // S1b (D795): on the world without a box; the screen gives it the world theme (light text, dark outline).
        ThemeTypeVariation = UiTheme.WorldPanel;
        _scroll.AddChild(_rows);
        _rows.AddChild(_more);
        _more.Pressed += () => MoreChosen?.Invoke();
        AddChild(_scroll);
        _scroll.GetVScrollBar().ValueChanged += _ => AskForPictures();
        ShowScrollBar();
    }

    /// <summary>
    /// A scroll bar only for the compact list of many (S1b tour: with four rows a bar stood on the world beside the
    /// names); the wheel scrolls either way.
    /// </summary>
    private void ShowScrollBar() =>
        _scroll.VerticalScrollMode = _compact ? ScrollContainer.ScrollMode.Auto : ScrollContainer.ScrollMode.ShowNever;

    /// <summary>A click on a row: the inhabitant is chosen.</summary>
    public event Action<string>? Chosen;

    /// <summary>A click on '+N': the player wants all inhabitants (the inhabitants window).</summary>
    public event Action? MoreChosen;

    /// <summary>How many inhabitants the corner leaves out ('+N'; 0 shows all).</summary>
    public int More => _moreCount;

    /// <summary>The '+N' button (for checks and the tour).</summary>
    public Button MoreButton => _more;

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

    /// <summary>
    /// Shows the rows; nodes are kept and refilled, so a refresh every quarter second costs little. <paramref name="more"/>
    /// inhabitants are left out (B2749): the list shows '+N' for them and stays compact as for all of them.
    /// </summary>
    public void SetRows(IReadOnlyList<PortraitEntry> entries, int more = 0)
    {
        _entries = entries ?? throw new ArgumentNullException(nameof(entries));
        _moreCount = Math.Max(0, more);
        if (_style is not null)
        {
            var compact = CornerModel.Compact(entries.Count + _moreCount, _style.Tokens.Zones);
            if (compact != _compact)
            {
                _compact = compact;
                ShowScrollBar();
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
                row.Fill(entry, PictureOf(entry.Id), entry.Id == Selected, _compact, attention, _style);
            }
        }

        _more.Visible = _moreCount > 0;
        if (_more.Visible)
        {
            var count = _moreCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
            _more.Text = TextFill.Fill(Tr("ui.corner.more"), new Dictionary<string, string> { ["count"] = count });
            _more.TooltipText = TextFill.Fill(Tr("ui.corner.more_tooltip"), new Dictionary<string, string> { ["count"] = (_entries.Count + _moreCount).ToString(System.Globalization.CultureInfo.CurrentCulture) });
            var px = _compact ? _style.Tokens.Zones.CompactPortraitPx : _style.Tokens.Zones.PortraitPx;
            _more.CustomMinimumSize = new Vector2(px, px);
        }

        _rows.MoveChild(_more, -1);
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

    /// <summary>The portrait (for checks and the tour).</summary>
    public GwPortrait Portrait => _portrait;

    /// <summary>Shows an inhabitant.</summary>
    public void Fill(PortraitEntry entry, Texture2D? picture, bool selected, bool compact, Color attention, UiStyle style)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(style);
        _name.Text = entry.Name;
        _activity.Text = entry.Activity;
        _portrait.Attention = attention;
        _portrait.States = style.Tokens.PortraitStates;
        _portrait.ReduceMotion = style.Settings.ReduceMotion;
        _portrait.Show(picture, CornerModel.Initial(entry.Name), entry.State, selected);

        // The state in words too (DE/EN): the badge shows what, the tooltip says it (details stay in the chronicle).
        var tip = TextFill.Fill(Tr("ui.corner.portrait_tip"), new Dictionary<string, string> { ["name"] = entry.Name, ["activity"] = entry.Activity });
        var state = entry.State switch
        {
            PortraitState.Knock => "ui.corner.knocking",
            PortraitState.Worry => "ui.corner.worry",
            PortraitState.Aha => "ui.corner.aha",
            PortraitState.Question => "ui.corner.brooding",
            _ => null,
        };
        TooltipText = state is not null ? tip + "\n" + Tr(state) : compact ? tip : string.Empty;
    }
}
