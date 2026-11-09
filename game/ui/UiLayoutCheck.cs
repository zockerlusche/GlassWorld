using GlassWorld.Game.Localization;
using GlassWorld.Game.Ui.Components;
using GlassWorld.Game.Ui.Screens;
using GlassWorld.Presentation.Ui;
using GlassWorld.Presentation.World;
using Godot;

namespace GlassWorld.Game.Ui;

/// <summary>
/// Headless layout check of the UI standard (D366 section 14): a sample window in both reference
/// resolutions (KR5), at UI size 75/100/200 % and text size 100/200 %, in DE and EN. It must stay in
/// the safe area, keep its header readable and fit the allowed share of the screen.
/// </summary>
public static class UiLayoutCheck
{
    private static readonly (int Width, int Height)[] Resolutions = [(1920, 1080), (3840, 1600)];
    private static readonly double[] UiScales = [0.75, 1.0, 2.0];
    private static readonly double[] TextScales = [1.0, 2.0];

    /// <summary>Runs all cases below <paramref name="host"/>; returns the problems (empty if fine).</summary>
    /// <param name="host">Node the check stages are added to.</param>
    /// <param name="baseStyle">Style whose UI and text size the cases vary.</param>
    /// <param name="timeChoices">Time control of the top bar.</param>
    /// <param name="windows">Real windows to fit in every case (menus, settings, pause), built for a style; optional.</param>
    public static async Task<IReadOnlyList<string>> RunAsync(Node host, UiStyle baseStyle, IReadOnlyList<TimeChoice> timeChoices, Func<UiStyle, IReadOnlyList<(string Name, GwWindow Window)>>? windows = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(baseStyle);
        var problems = new List<string>();
        var startLocale = TranslationServer.GetLocale();
        var cases = 0;
        try
        {
            foreach (var locale in TextCatalogs.SupportedLocales)
            {
                TextCatalogs.SetLanguage(locale);
                foreach (var (width, height) in Resolutions)
                {
                    foreach (var uiScale in UiScales)
                    {
                        foreach (var textScale in TextScales)
                        {
                            cases++;
                            var style = baseStyle.With(baseStyle.Settings with { UiScale = uiScale, TextScale = textScale });
                            var (w, h) = UiScaling.LogicalSize(width, height, style.Tokens, style.ContentScaleFactor);
                            var label = $"{locale} {width}x{height} ui {uiScale:P0} text {textScale:P0}";
                            problems.AddRange((await CheckOne(host, style, new Vector2((float)w, (float)h), timeChoices, windows)).Select(p => $"{label}: {p}"));
                        }
                    }
                }
            }
        }
        finally
        {
            TranslationServer.SetLocale(startLocale);
        }

        GD.Print($"UI_CHECK cases={cases} problems={problems.Count}");
        return problems;
    }

    private static async Task<IReadOnlyList<string>> CheckOne(Node host, UiStyle style, Vector2 area, IReadOnlyList<TimeChoice> timeChoices, Func<UiStyle, IReadOnlyList<(string Name, GwWindow Window)>>? windows)
    {
        var stage = new Control { Size = area, Theme = UiTheme.Build(style) };
        host.AddChild(stage);
        var window = new GwWindow();
        window.Setup("ui.settings.title", style);
        foreach (var key in new[] { "vocab.need.hunger", "vocab.need.thirst", "vocab.need.sleep", "vocab.need.warmth", "vocab.need.safety", "vocab.need.company", "vocab.need.fun", "vocab.need.purpose" })
        {
            window.Content.AddChild(new Label { Text = key });
        }

        window.AddAction("ui.common.close");
        stage.AddChild(window);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        window.FitInto(area);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);

        var problems = new List<string>();
        if (!window.IsInsideSafeArea(area))
        {
            problems.Add($"window {window.Position}+{window.Size} leaves the safe area of {area}");
        }

        var header = window.HeaderProblem();
        if (header.Length > 0)
        {
            problems.Add("header: " + header);
        }

        if (window.Size.X > area.X * style.Tokens.Window.MaxScreenFraction + 0.5f || window.Size.Y > area.Y * style.Tokens.Window.MaxScreenFraction + 0.5f)
        {
            problems.Add("window larger than the allowed share of the screen");
        }

        window.QueueFree();
        if (windows is not null)
        {
            problems.AddRange(await CheckWindows(host, stage, area, windows(style)));
        }

        problems.AddRange(await CheckComponents(host, stage, style, area));
        problems.AddRange(await CheckTopBar(host, stage, style, area, timeChoices));
        stage.QueueFree();
        return problems;
    }

    /// <summary>
    /// Real windows (main menu forms, settings, pause; GW1 review #50 K1) fit inside the safe area with a
    /// readable header in this case; long content scrolls.
    /// </summary>
    private static async Task<IReadOnlyList<string>> CheckWindows(Node host, Control stage, Vector2 area, IReadOnlyList<(string Name, GwWindow Window)> windows)
    {
        var problems = new List<string>();
        foreach (var (name, window) in windows)
        {
            stage.AddChild(window);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            window.FitInto(area);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!window.IsInsideSafeArea(area))
            {
                problems.Add($"{name} window {window.Position}+{window.Size} leaves the safe area of {area}");
            }

            var header = window.HeaderProblem();
            if (header.Length > 0)
            {
                problems.Add($"{name} header: {header}");
            }

            window.Free();
        }

        return problems;
    }

    /// <summary>The top bar (clock and time control) fits next to the side bar buttons.</summary>
    private static async Task<IReadOnlyList<string>> CheckTopBar(Node host, Control stage, UiStyle style, Vector2 area, IReadOnlyList<TimeChoice> choices)
    {
        var (bar, date, time) = GameScreen.CreateTopBar(style, choices, _ => { });
        date.Text = TextFill.Fill(host.Tr("ui.clock.date"), new Dictionary<string, string> { ["season"] = host.Tr("time.season.autumn"), ["day"] = "28", ["year"] = "120" });
        time.Text = "23:59";
        var side = new GwSideBar();
        side.Add("inhabitants", "ui.inhabitants.title");
        side.Add("questions", "ui.questions.title");
        stage.AddChild(bar);
        stage.AddChild(side);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        var margin = style.Tokens.Window.SafeMargin;
        var room = area.X - (2 * margin) - side.GetCombinedMinimumSize().X - style.Tokens.Window.Gap;
        bar.Size = new Vector2(room, 0);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        bar.Size = new Vector2(room, 0);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        var problems = new List<string>();
        // The bar flows into more lines at large UI and text sizes (up to 4x on 1080p); it must keep its
        // room next to the side bar and leave at least half of the height to the world (D366 section 2).
        if (bar.GetCombinedMinimumSize().X > room + 0.5f || bar.Size.Y > area.Y / 2)
        {
            problems.Add($"top bar {bar.Size} (minimum {bar.GetCombinedMinimumSize()}) does not fit room {room:0} px or takes more than half the height");
        }

        bar.QueueFree();
        foreach (var panel in side.Panels)
        {
            // The side bar creates its panels; here they never joined the tree, so free them directly.
            panel.Free();
        }

        side.QueueFree();
        return problems;
    }

    /// <summary>Side panel with table, hint stack and a long speech bubble in the same case.</summary>
    private static async Task<IReadOnlyList<string>> CheckComponents(Node host, Control stage, UiStyle style, Vector2 area)
    {
        var problems = new List<string>();
        var margin = style.Tokens.Window.SafeMargin;

        var panel = new GwSidePanel();
        panel.Setup("ui.inhabitants.title");
        var table = new GwTable();
        table.Setup(
            [new GwColumn("ui.inhabitants.column.name", false), new GwColumn("ui.inhabitants.column.activity", false), new GwColumn("ui.inhabitants.column.will_to_live", true), new GwColumn("ui.inhabitants.column.questions", true)],
            "ui.inhabitants.empty");
        table.SetRows([.. Enumerable.Range(1, 100).Select(i => new GwRow($"inh-{i}", [TextFill.Fill(host.Tr("ui.inhabitant.unnamed"), new Dictionary<string, string> { ["number"] = $"{i}" }), host.Tr("ui.activity.show.inhabitant_topic"), $"{i % 100}", $"{i % 3}"], [i, 0, i % 100, i % 3]))]);
        panel.Content.AddChild(table);
        stage.AddChild(panel);

        var board = new ToastBoard(style.Tokens.Toast, new ToastSettings(1, UntilDismissed: true));
        board.Push(new ToastMessage("ui.inhabitants.empty", new Dictionary<string, string>(), null), fastForward: false);
        board.Push(new ToastMessage("msg.mail.kind.knock", new Dictionary<string, string>(), "knock"), fastForward: false);
        for (var i = 0; i < 12; i++)
        {
            board.Push(new ToastMessage("msg.mail.kind.knock", new Dictionary<string, string>(), "knock"), fastForward: true);
        }

        var toasts = new GwToasts { Board = board, CustomMinimumSize = new Vector2(style.Tokens.Window.SidePanelWidth, 0) };
        stage.AddChild(toasts);
        toasts.Sync();

        var bubble = new GwBubble();
        stage.AddChild(bubble);
        bubble.Show(string.Join(' ', Enumerable.Repeat(host.Tr("vocab.property.building_material"), 16)), style, area);

        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        // Dock first with the largest text size, then with the real one: a runtime change of the text
        // size must not leave the panel wider than its place (GW1 review W1).
        panel.Dock(area, style.With(style.Settings with { TextScale = style.Tokens.TextScale.Max }), 0);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        panel.Dock(area, style, 0);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        panel.Dock(area, style, 0);
        var dockedSize = panel.Size;
        toasts.Size = new Vector2(Math.Min(style.Tokens.Window.SidePanelWidth, area.X - (2 * margin)), toasts.GetCombinedMinimumSize().Y);
        toasts.Position = area - toasts.Size - (Vector2.One * margin);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        bubble.ResetSize();
        bubble.Position = ((area - bubble.Size) / 2).Floor();
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);

        // Inhabitant card beside the open side panel (GW1 review #47 H1: in the reference sizes, not
        // only in the headless window). About as many rows as the real card; content scrolls.
        var card = new GwWindow();
        card.Setup("ui.inhabitants.title", style);
        for (var i = 0; i < 26; i++)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = "ui.card.will_to_live", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            row.AddChild(new ProgressBar { MaxValue = 100, Value = 50, ShowPercentage = true, CustomMinimumSize = new Vector2(style.Tokens.Window.CardBarWidth, 0) });
            card.Content.AddChild(row);
        }

        stage.AddChild(card);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        var free = panel.Position.X - style.Tokens.Window.Gap;
        var room = ScreenModel.CardRoom(area.X, free, card.GetCombinedMinimumSize().X, margin);
        card.FitInto(new Vector2((float)room, area.Y));
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        var cardRect = new Rect2(card.Position, card.Size);
        if (!card.IsInsideSafeArea(area) || (room < area.X && cardRect.Intersects(new Rect2(panel.Position, panel.Size))))
        {
            problems.Add($"card {card.Position}+{card.Size} leaves the safe area of {area} or covers the side panel at {panel.Position}");
        }

        card.Free();

        var safe = new Rect2(Vector2.One * margin, area - (Vector2.One * 2 * margin));
        if (!safe.Encloses(new Rect2(panel.Position, panel.Size)))
        {
            problems.Add($"side panel {panel.Position}+{panel.Size} leaves the safe area of {area} (docked {dockedSize}; {panel.Diagnose()})");
        }

        var title = panel.TitleProblem();
        if (title.Length > 0)
        {
            problems.Add("side panel " + title);
        }

        var inner = panel.Size.X - (2 * (style.Tokens.Window.Padding + style.Frame(GlassWorld.Contracts.UiFrameId.Window).ContentPadding));
        if (table.HeaderMinimumWidth() > inner + 0.5f)
        {
            problems.Add($"table header needs {table.HeaderMinimumWidth():0} px, panel offers {inner:0} px");
        }

        if (board.Visible.Count != style.Tokens.Toast.MaxVisible - 1 || board.Visible[1].Count != 13)
        {
            problems.Add("hint bundling does not match the board");
        }

        if (!safe.Encloses(new Rect2(toasts.Position, toasts.Size)) || toasts.Size.Y > area.Y / 2)
        {
            problems.Add($"hints {toasts.Position}+{toasts.Size} leave the safe area or cover more than half the height");
        }

        // A long sentence at the largest UI and text size may fill much of the screen; it must still fit
        // (nothing cut off, D366 section 2) and stay readable as one bubble.
        if (bubble.Size.X > safe.Size.X + 0.5f || bubble.Size.Y > safe.Size.Y + 0.5f || bubble.LineCount() > 10)
        {
            problems.Add($"speech bubble {bubble.Size} with {bubble.LineCount()} lines is too large");
        }

        return problems;
    }
}
