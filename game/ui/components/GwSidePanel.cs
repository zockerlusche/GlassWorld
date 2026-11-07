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

    /// <summary>Content container (inside the scroll area).</summary>
    public VBoxContainer Content { get; } = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };

    /// <summary>Creates the panel.</summary>
    public GwSidePanel()
    {
        ThemeTypeVariation = UiTheme.WindowPanel;
        var column = new VBoxContainer();
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        scroll.AddChild(Content);
        column.AddChild(_title);
        column.AddChild(scroll);
        AddChild(column);
    }

    /// <summary>Sets the title key.</summary>
    public void Setup(string titleKey) => _title.Text = titleKey;

    /// <summary>Places the panel at the right edge of <paramref name="area"/> (logical pixels).</summary>
    public void Dock(Vector2 area, UiStyle style, float rightOffset)
    {
        ArgumentNullException.ThrowIfNull(style);
        var margin = style.Tokens.Window.SafeMargin;
        // Grows with the text size so headers stay readable (D366 section 2), never beyond the screen.
        var wanted = style.Tokens.Window.SidePanelWidth * (float)Math.Max(1, style.Settings.TextScale);
        var width = Math.Min(wanted, area.X - (2 * margin) - rightOffset);
        // A wrapping label measures itself at its current width (one letter per line before the first
        // layout), so wrap the title only when it really does not fit.
        _title.AutowrapMode = TextServer.AutowrapMode.Off;
        _title.CustomMinimumSize = Vector2.Zero;
        var padding = 2 * (style.Tokens.Window.Padding + style.Frame(GlassWorld.Contracts.UiFrameId.Window).ContentPadding);
        if (_title.GetCombinedMinimumSize().X > width - padding)
        {
            _title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _title.CustomMinimumSize = new Vector2(width - padding, 0);
        }

        Size = new Vector2(width, area.Y - (2 * margin));
        Position = new Vector2(area.X - margin - rightOffset - width, margin);
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

    /// <summary>All panels (for placing them in the UI layer).</summary>
    public IEnumerable<GwSidePanel> Panels => _entries.Values.Select(e => e.Panel);

    /// <summary>Opens a panel and closes the other one; opening the open one closes it.</summary>
    public void Toggle(string key)
    {
        OpenKey = OpenKey == key ? null : key;
        foreach (var (k, entry) in _entries)
        {
            entry.Panel.Visible = k == OpenKey;
            entry.Button.SetPressedNoSignal(k == OpenKey);
        }
    }
}
