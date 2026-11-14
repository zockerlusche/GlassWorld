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
    private readonly Label _title = new() { ThemeTypeVariation = UiTheme.HeaderLabel };

    /// <summary>Tabs of the panel's theme in the zones layout (W1717); hidden while empty.</summary>
    public HFlowContainer Tabs { get; } = new() { Visible = false };

    /// <summary>Content container (inside the scroll area).</summary>
    public VBoxContainer Content { get; } = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };

    /// <summary>Creates the panel.</summary>
    public GwSidePanel()
    {
        ThemeTypeVariation = UiTheme.WindowPanel;
        var column = new VBoxContainer();
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        scroll.AddChild(Content);
        column.AddChild(Tabs);
        column.AddChild(_title);
        column.AddChild(scroll);
        AddChild(column);
    }

    /// <summary>Sets the title key.</summary>
    public void Setup(string titleKey)
    {
        _title.AutoTranslateMode = AutoTranslateModeEnum.Inherit;
        _title.Text = titleKey;
    }

    /// <summary>Sets the title to finished text (e.g. an inhabitant's name) that is never translated.</summary>
    public void SetTitleText(string text)
    {
        _title.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        _title.Text = text;
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
    public void Dock(Vector2 area, UiStyle style, float rightOffset, float top = 0, bool left = false, float heightShare = 1, float widthPx = 0, float contentPx = 0, float maxWidth = 0, bool fitContent = false)
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

        var y = top > 0 ? top : margin;
        var height = (area.Y - margin - y) * Math.Clamp(heightShare, 0.1f, 1);
        if (fitContent)
        {
            // Frame, tabs and title (the panel's own minimum, the scroll area asks for nothing) plus what the content needs.
            height = Math.Min(height, GetCombinedMinimumSize().Y + Content.GetCombinedMinimumSize().Y);
        }

        Size = new Vector2(width, height);

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
