using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game.Ui.Components;

/// <summary>
/// A side window docked at the right edge (D366 section 8): mailbox, inhabitants, chronicle, open
/// questions. Same frame as <see cref="GwWindow"/>, fixed width from the tokens, full height inside
/// the safe area; content scrolls vertically. Opened and closed by <see cref="GwSideBar"/>.
/// </summary>
public partial class GwSidePanel : PanelContainer
{
    private const string CloseSymbol = "×";
    // A long title (an inhabitant's long name) ends in an ellipsis beside the '×' and shows whole as a tooltip, so it never
    // widens the window into the free middle.
    private readonly Label _title = new() { ThemeTypeVariation = UiTheme.HeaderLabel, SizeFlagsHorizontal = SizeFlags.ExpandFill, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, MouseFilter = MouseFilterEnum.Pass };
    private readonly Button _close = new() { Name = "Close", Text = CloseSymbol, Flat = true, TooltipText = "ui.common.close", SizeFlagsVertical = SizeFlags.ShrinkBegin };

    // Docks again in one frame at most this often: a layout that does not settle stops instead of filling the call
    // queue (S2 tour: millions of deferred calls, then a crash).
    private const int MaxRedocksPerFrame = 4;

    private readonly ScrollContainer _scroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };

    // The last dock that fits the content (S1b): done again once the content's height settles at the new width.
    private Action? _redock;
    private bool _redockQueued;
    private ulong _redockFrame;
    private int _redocksThisFrame;

    /// <summary>Tabs of the panel's theme in the zones layout (W1717); hidden while empty.</summary>
    public HFlowContainer Tabs { get; } = new() { Visible = false };

    /// <summary>The '×' in the title row was pressed (D957 v4: back to the card, or closed).</summary>
    public event Action? CloseRequested;

    /// <summary>The '×' of the title row (for checks and the tour).</summary>
    public Button CloseButton => _close;

    /// <summary>Content container (inside the scroll area).</summary>
    public VBoxContainer Content { get; } = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };

    /// <summary>Creates the panel.</summary>
    public GwSidePanel()
    {
        ThemeTypeVariation = UiTheme.WindowPanel;
        var column = new VBoxContainer();
        var scroll = _scroll;
        scroll.AddChild(Content);
        column.AddChild(Tabs);
        var titleRow = new HBoxContainer { Name = "TitleRow" };
        titleRow.AddChild(_title);
        titleRow.AddChild(_close);
        _close.Pressed += () => CloseRequested?.Invoke();
        column.AddChild(titleRow);
        column.AddChild(scroll);
        AddChild(column);

        // A wrapping label reports its height for the width it had; after the panel took its new width the content's
        // minimum changes once more - then the fitting dock runs again (review GW3 #25694; else one sentence scrolled).
        Content.MinimumSizeChanged += () =>
        {
            if (_redock is not null && !_redockQueued)
            {
                _redockQueued = true;
                Callable.From(() =>
                {
                    _redockQueued = false;
                    var frame = Engine.GetProcessFrames();
                    _redocksThisFrame = frame == _redockFrame ? _redocksThisFrame + 1 : 1;
                    _redockFrame = frame;
                    if (_redocksThisFrame <= MaxRedocksPerFrame)
                    {
                        _redock?.Invoke();
                    }
                }).CallDeferred();
            }
        };
    }

    /// <summary>Sets the title key.</summary>
    public void Setup(string titleKey)
    {
        _title.AutoTranslateMode = AutoTranslateModeEnum.Inherit;
        _title.Text = titleKey;
        _title.TooltipText = string.Empty;
    }

    /// <summary>Sets the title to finished text (e.g. an inhabitant's name) that is never translated.</summary>
    public void SetTitleText(string text)
    {
        _title.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        _title.Text = text;
        _title.TooltipText = text;
    }

    /// <summary>Places the panel at the right edge of <paramref name="area"/> (logical pixels).</summary>
    /// <param name="area">The screen (logical pixels).</param>
    /// <param name="style">The UI style.</param>
    /// <param name="rightOffset">Room kept free at the right edge (the side bar).</param>
    /// <param name="top">Upper edge; 0 = the safe margin (zones layout: below the top bar).</param>
    /// <param name="left">True docks at the left edge instead (the pinned log of the zones layout).</param>
    /// <param name="heightShare">Share of the free height the panel takes (1 = all).</param>
    /// <param name="widthPx">Width at the reference size; 0 = the side panel width of the window tokens.</param>
    /// <param name="contentPx">Width the content asks for in pixels as measured (a table's headers), without the panel's frame; 0 = no wish. Capped at the room like the token width.</param>
    /// <param name="maxWidth">Widest the panel may be (corner layout: never into the free middle, A1682); 0 = no cap but the room.</param>
    /// <param name="fitContent">True: no higher than its content needs (S1b: no full-height window for one sentence); more scrolls.</param>
    /// <param name="fromBottom">True with <paramref name="fitContent"/>: it stands on the bottom edge and grows upwards, at most up to <paramref name="top"/> (A1684: the log in its corner).</param>
    public void Dock(Vector2 area, UiStyle style, float rightOffset, float top = 0, bool left = false, float heightShare = 1, float widthPx = 0, float contentPx = 0, float maxWidth = 0, bool fitContent = false, bool fromBottom = false)
    {
        ArgumentNullException.ThrowIfNull(style);
        var margin = style.Tokens.Window.SafeMargin;

        // A panel docked on the right grows to the left whenever its content grows later (a chronicle day
        // with a longer date), never under the buttons beside it (review GW1 #243).
        GrowHorizontal = left ? GrowDirection.End : GrowDirection.Begin;

        // Grows with the text size so headers stay readable (D366 section 2), never beyond the screen.
        var wanted = (widthPx > 0 ? widthPx : style.Tokens.Window.SidePanelWidth) * (float)Math.Max(1, style.Settings.TextScale);

        // What the content asks for (a table's headers, measured in pixels already) plus the panel's frame:
        // a wish that is capped at the room like the token width, so the panel never leaves the picture.
        if (contentPx > 0)
        {
            var around = GetThemeStylebox("panel") is { } box ? box.GetContentMargin(Side.Left) + box.GetContentMargin(Side.Right) : 0;
            wanted = Math.Max(wanted, contentPx + around);
        }

        var width = Math.Min(wanted, area.X - (2 * margin) - rightOffset);
        if (maxWidth > 0)
        {
            width = Math.Min(width, maxWidth);
        }
        // A wrapping label measures itself at its current width (one letter per line before the first
        // layout), so wrap the title only when it really does not fit.
        _title.AutowrapMode = TextServer.AutowrapMode.Off;
        _title.CustomMinimumSize = Vector2.Zero;
        // The frame in use says how much room it takes (flat or picture frame, compact or not; review GW1 #202).
        var frame = GetThemeStylebox("panel");
        var padding = frame is null
            ? 2 * (style.Tokens.Window.Padding + style.Frame(GlassWorld.Contracts.UiFrameId.Window).ContentPadding)
            : frame.GetContentMargin(Side.Left) + frame.GetContentMargin(Side.Right);
        if (_title.GetCombinedMinimumSize().X > width - padding)
        {
            _title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _title.CustomMinimumSize = new Vector2(width - padding, 0);
        }

        _redock = fitContent ? () => Dock(area, style, rightOffset, top, left, heightShare, widthPx, contentPx, maxWidth, fitContent: true, fromBottom) : null;
        var y = top > 0 ? top : margin;
        var height = (area.Y - margin - y) * Math.Clamp(heightShare, 0.1f, 1);
        if (fitContent)
        {
            // Frame, tabs and title (the panel's own minimum, the scroll area asks for nothing) plus what the content needs.
            // When all of it fits there is no scroll bar: one that came and went by a pixel narrowed the text, which then
            // wrapped taller, docked taller, lost the bar, widened, wrapped shorter - and so on without end.
            var need = GetCombinedMinimumSize().Y + Content.GetCombinedMinimumSize().Y;
            _scroll.VerticalScrollMode = need <= height ? ScrollContainer.ScrollMode.ShowNever : ScrollContainer.ScrollMode.Auto;
            height = Math.Min(height, need);
        }
        else
        {
            _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Auto;
        }

        Size = new Vector2(width, height);
        if (fitContent && fromBottom)
        {
            y = area.Y - margin - Size.Y;
        }

        // The size is the asked one or, if the content needs more, its minimum: placed by what it really is,
        // a panel on the right grows to the left instead of under the buttons beside it (D747 A2: the
        // chronicle's day row was wider than the panel and hid "later" under the theme buttons).
        Position = new Vector2(left ? margin : Math.Max(margin, area.X - margin - rightOffset - Size.X), y);
    }

    /// <summary>Minimum sizes of the parts (diagnostics for the layout check).</summary>
    public string Diagnose() => $"title {_title.GetCombinedMinimumSize()}, content {Content.GetCombinedMinimumSize()}, panel {GetCombinedMinimumSize()}";

    /// <summary>Empty if the title fits in at most two lines, otherwise why not.</summary>
    public string TitleProblem() => _title.GetLineCount() > 2 ? $"title needs {_title.GetLineCount()} lines" : string.Empty;
}

/// <summary>
/// The right-hand side bar (D366 sections 1a and 8): one button per side window, fixed order and
/// place; at most one side window is open at a time.
/// </summary>
public partial class GwSideBar : VBoxContainer
{
    private readonly Dictionary<string, (Button Button, GwSidePanel Panel)> _entries = new(StringComparer.Ordinal);

    /// <summary>The open panel's key, or null.</summary>
    public string? OpenKey { get; private set; }

    /// <summary>Adds a side window with its title key (also the button text).</summary>
    public GwSidePanel Add(string key, string titleKey)
    {
        var panel = new GwSidePanel { Visible = false };
        panel.Setup(titleKey);
        var button = new Button { Text = titleKey, ToggleMode = true };
        button.Pressed += () => Toggle(key);
        panel.CloseRequested += () => CloseRequested?.Invoke(key);
        AddChild(button);
        _entries[key] = (button, panel);
        return panel;
    }

    /// <summary>The side button of a key (hidden for windows that only the zones layout opens).</summary>
    public Button ButtonOf(string key) => _entries[key].Button;

    /// <summary>The panel of a key.</summary>
    public GwSidePanel PanelOf(string key) => _entries[key].Panel;

    /// <summary>Opens the panel of <paramref name="key"/> (no toggle); null closes the open one.</summary>
    public void Open(string? key)
    {
        if (key != OpenKey)
        {
            Toggle(key ?? OpenKey!);
        }
    }

    /// <summary>All panels (for placing them in the UI layer).</summary>
    public IEnumerable<GwSidePanel> Panels => _entries.Values.Select(e => e.Panel);

    /// <summary>The '×' of a panel was pressed (its key); the screen decides where it leads.</summary>
    public event Action<string>? CloseRequested;

    /// <summary>Raised after a panel opened or closed (e.g. to move the card beside it).</summary>
    public event Action? Toggled;

    /// <summary>Opens a panel and closes the other one; opening the open one closes it.</summary>
    public void Toggle(string key)
    {
        OpenKey = OpenKey == key ? null : key;
        foreach (var (k, entry) in _entries)
        {
            entry.Panel.Visible = k == OpenKey;
            entry.Button.SetPressedNoSignal(k == OpenKey);
        }

        Toggled?.Invoke();
    }
}
