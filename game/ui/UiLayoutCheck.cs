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
/// the safe area, keep its header readable and fit the allowed share of the screen. The corners of Variante A
/// (A1682) are checked in three resolutions at UI size 75-200 %: nothing overlaps, the middle stays free.
/// </summary>
public static class UiLayoutCheck
{
    // The card's bars: will to live and the needs under their card words (B2642).
    private static readonly string[] CardBarKeys = ["ui.card.will_to_live", "ui.card.need.hunger", "ui.card.need.thirst", "ui.card.need.sleep", "ui.card.need.warmth", "ui.card.need.safety", "ui.card.need.company", "ui.card.need.fun", "ui.card.need.purpose"];

    private static readonly (int Width, int Height)[] Resolutions = [(1920, 1080), (3840, 1600)];
    private static readonly double[] UiScales = [0.75, 1.0, 2.0];
    private static readonly double[] TextScales = [1.0, 2.0];

    // The corners of Variante A (A1682, D957 v4 section 5): three resolutions (the third a window with borders),
    // UI size 75-200 %, and the largest text size at 100 %.
    private static readonly (int Width, int Height)[] CornerResolutions = [(1920, 1080), (1887, 1043), (3840, 1600)];
    private static readonly (double Ui, double Text)[] CornerSizes = [(0.75, 1.0), (1.0, 1.0), (1.5, 1.0), (2.0, 1.0), (1.0, 2.0)];

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
                            // B2791: at the normal sizes (UI up to 100 %, normal text) the settings never scroll.
                            var strict = uiScale <= 1.0 && textScale <= 1.0;
                            problems.AddRange((await CheckOne(host, style, new Vector2((float)w, (float)h), timeChoices, windows, strict)).Select(p => $"{label}: {p}"));
                        }
                    }
                }

                foreach (var (width, height) in CornerResolutions)
                {
                    foreach (var (uiScale, textScale) in CornerSizes)
                    {
                        cases++;
                        var style = baseStyle.With(baseStyle.Settings with { UiScale = uiScale, TextScale = textScale });
                        var (w, h) = UiScaling.LogicalSize(width, height, style.Tokens, style.ContentScaleFactor);
                        var label = $"{locale} corners {width}x{height} ui {uiScale:P0} text {textScale:P0}";
                        problems.AddRange((await CheckCorners(host, style, new Vector2((float)w, (float)h), timeChoices)).Select(p => $"{label}: {p}"));
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

    private static async Task<IReadOnlyList<string>> CheckOne(Node host, UiStyle style, Vector2 area, IReadOnlyList<TimeChoice> timeChoices, Func<UiStyle, IReadOnlyList<(string Name, GwWindow Window)>>? windows, bool strict)
    {
        var stage = new Control { Size = area, Theme = UiTheme.Build(style) };
        host.AddChild(stage);
        var window = new GwWindow();
        window.Setup("ui.settings.title", style);
        foreach (var key in CardBarKeys.Skip(1))
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
            problems.AddRange(await CheckWindows(host, stage, area, windows(style), strict));
        }

        problems.AddRange(await CheckComponents(host, stage, style, area));
        stage.QueueFree();
        return problems;
    }

    /// <summary>
    /// Real windows (main menu forms, settings, pause; GW1 review #50 K1) fit inside the safe area with a
    /// readable header in this case; long content scrolls. The settings keep one size in all their tabs and, at the
    /// normal sizes (<paramref name="strict"/>), show every tab without scrolling (B2791).
    /// </summary>
    private static async Task<IReadOnlyList<string>> CheckWindows(Node host, Control stage, Vector2 area, IReadOnlyList<(string Name, GwWindow Window)> windows, bool strict)
    {
        var problems = new List<string>();
        var tabsWalked = false;
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

            // No hint cut while its field has room, no word broken inside (D705 A5/A6, review GW1 #226).
            problems.AddRange(UiFit.CutTexts(window).Select(cut => $"{name}: {cut}"));

            // Every tab of the settings shows its content, as a player opens them one after the other (D809 M1).
            // The list holds the settings several times (opened at different tabs); walking the tabs once
            // per case is enough and keeps the check within the smoke run's frame budget.
            var tabs = window is SettingsWindow settings && !tabsWalked ? settings.TabKeys : [];
            tabsWalked |= tabs.Count > 0;
            var sizes = new List<(string Tab, Vector2 Size)>();
            foreach (var tab in tabs)
            {
                ((SettingsWindow)window).ShowTab(tab);
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                var hidden = window.HiddenContentProblem(area);
                if (hidden.Length > 0)
                {
                    problems.Add($"{name} tab {tab}: {hidden}");
                }
                else if (strict && window.HiddenContent() > 1)
                {
                    problems.Add($"{name} tab {tab}: scrolls ({window.HiddenContent():0} px hidden in a window of height {window.Size.Y:0})");
                }

                sizes.Add((tab, window.Size));
            }

            // B2791: the window does not jump when the player changes tabs.
            if (sizes.Count > 1 && sizes.Any(s => (s.Size - sizes[0].Size).Abs().Length() > 0.5f))
            {
                problems.Add($"{name}: size changes between tabs ({string.Join(", ", sizes.Select(s => $"{s.Tab.Replace("ui.settings.tab.", string.Empty, StringComparison.Ordinal)} {s.Size.X:0}x{s.Size.Y:0}"))})");
            }

            if (tabs.Count == 0)
            {
                // The window settles in the frame after its fit (GwWindow.Settle); the settings whose tabs
                // were already walked in this case are looked at on the tab they open on (review GW1 #263).
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                var other = window.HiddenContentProblem(area);
                if (other.Length > 0)
                {
                    problems.Add($"{name}: {other}");
                }
            }

            window.Free();
        }

        return problems;
    }

    /// <summary>
    /// The corners (A1682): time block, portraits (a few with names, many as portraits only), the log and an open
    /// window placed by the game's own rule stay in the safe area, do not overlap, and leave the rectangle in the
    /// middle free; the many portraits turn compact.
    /// </summary>
    private static async Task<IReadOnlyList<string>> CheckCorners(Node host, UiStyle style, Vector2 area, IReadOnlyList<TimeChoice> choices)
    {
        var stage = new Control { Size = area, Theme = UiTheme.Build(style) };
        host.AddChild(stage);
        var date = DateSample(host);
        var corner = GameScreen.CornerSample(style, choices, date, "23:59", host.Tr("ui.favor.name"));
        var portraits = new GwPortraitList();
        var log = new GwSidePanel();
        log.Setup("ui.log.title");

        // A1684: the opened log with its filter (every category) and search over its lines; it must not widen the log
        // into the free middle at any size.
        var logTools = new HBoxContainer();
        var logFilter = new OptionButton();
        logFilter.AddItem("ui.log.category.all");
        foreach (var category in style.Tokens.Log.Categories)
        {
            logFilter.AddItem(LogModel.CategoryKey(category.Id));
        }

        logTools.AddChild(logFilter);
        logTools.AddChild(new LineEdit { PlaceholderText = "ui.log.search", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        log.Content.AddChild(logTools);
        var window = new GwSidePanel();
        window.Setup("ui.inhabitants.title");
        for (var i = 0; i < 26; i++)
        {
            window.Content.AddChild(new Label { Text = "ui.card.need.company", AutowrapMode = TextServer.AutowrapMode.WordSmart });
            log.Content.AddChild(new Label { Text = "ui.log.no_answer", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        }

        // The parts on the world take the world theme as in the game (S1b).
        var world = UiTheme.BuildWorld(style);
        portraits.Theme = world;
        log.ThemeTypeVariation = UiTheme.WorldPanel;
        log.Theme = world;
        // The card (A1683) with long texts: a long name, a long question, the hint of the talk field in the form for the inhabitant.
        var longName = TextFill.Fill(host.Tr("ui.inhabitant.unnamed"), new Dictionary<string, string> { ["number"] = "12" }) + " Maximiliane-Friederike";
        var ask = TextFill.Fill(host.Tr(QuestionKeys.CardAsk("unknown_object")), new Dictionary<string, string> { ["thing"] = host.Tr("vocab.object.dry_wood") });
        var talkLine = TextFill.Fill(host.Tr("ui.card.talk_them"), new Dictionary<string, string> { ["name"] = longName, ["text"] = host.Tr("ui.question.reason.unknown_inhabitant.female") });
        var (card, talkField) = GameScreen.CardSample(style, longName, host.Tr("ui.activity.show.inhabitant_topic"), ask, talkLine, new[] { host.Tr("ui.card.talk_hint"), host.Tr("ui.card.whisper_hint") }.MaxBy(h => h.Length)!, [.. CardBarKeys]);

        // A1736 (TL #26282): the window and the card on the right stand on the world's dark ground as in the game.
        var windowTheme = UiTheme.BuildWindowOnWorld(style);
        foreach (var onWorld in new[] { window, card })
        {
            onWorld.ThemeTypeVariation = UiTheme.WorldPanel;
            onWorld.Theme = windowTheme;
        }
        stage.AddChild(corner);
        stage.AddChild(portraits);
        stage.AddChild(log);
        stage.AddChild(window);
        stage.AddChild(card);
        portraits.Setup(style);
        var problems = new List<string>();
        var margin = style.Tokens.Window.SafeMargin;
        var safe = new Rect2(Vector2.One * margin, area - (Vector2.One * 2 * margin));
        var (mx, my, mw, mh) = CornerModel.Middle(area.X, area.Y, style.Tokens.Zones.MiddleShare);
        var middle = new Rect2((float)mx, (float)my, (float)mw, (float)mh);
        var activity = host.Tr("ui.activity.show.inhabitant_topic");
        // B2749: a few (full rows), some (portraits only), many (the important ones and +N).
        var zones = style.Tokens.Zones;
        foreach (var (count, right, rightName) in new (int, GwSidePanel, string)[] { (2, window, "window"), (zones.PortraitsCompactFrom + 1, window, "window"), (50, window, "window"), (2, card, "card") })
        {
            window.Visible = right == window;
            card.Visible = right == card;
            var showing = count >= zones.PortraitsImportantFrom ? zones.PortraitsImportantMax : count;
            portraits.SetRows([.. Enumerable.Range(1, showing).Select(i => new PortraitEntry($"inh-{i}", TextFill.Fill(host.Tr("ui.inhabitant.unnamed"), new Dictionary<string, string> { ["number"] = $"{i}" }) + " Maximiliane-Friederike", activity, (PortraitState)(i % 5)))], count - showing);
            for (var pass = 0; pass < 2; pass++)
            {
                await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
                GameScreen.PlaceCorners(area, style, corner, portraits, log, [(right, 0f, right == card)]);
            }

            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            var parts = new (string Name, Rect2 Box)[]
            {
                ("time block", new Rect2(corner.Position, corner.Size)),
                ($"{count} portraits", new Rect2(portraits.Position, portraits.Size)),
                ("log", new Rect2(log.Position, log.Size)),
                (rightName, new Rect2(right.Position, right.Size)),
            };
            foreach (var (name, box) in parts)
            {
                if (!safe.Grow(0.5f).Encloses(box))
                {
                    problems.Add($"{name} {box} leaves the safe area of {area}");
                }

                if (box.Intersects(middle))
                {
                    problems.Add($"{name} {box} reaches into the free middle {middle}");
                }
            }

            foreach (var (a, b) in parts.SelectMany((a, i) => parts.Skip(i + 1).Select(b => (a, b))))
            {
                if (a.Box.Intersects(b.Box))
                {
                    problems.Add($"{a.Name} {a.Box} overlaps {b.Name} {b.Box}");
                }
            }

            // TL #25715: the text on the world reads over snow, sand and light grass (the grounds of the data).
            if (!portraits.Compact)
            {
                // Compact rows show the portraits only, no names.
                problems.AddRange(WorldTextProblems(style, "portraits", portraits));
            }

            problems.AddRange(WorldTextProblems(style, "log", log));
            problems.AddRange(WorldTextProblems(style, rightName, right));
            problems.AddRange(FramedLabelProblems(rightName, right));
            problems.AddRange(WorldTextProblems(style, "time block", corner));

            // The talk field shows its whole hint (D1060 find 7: 'Was mö…' in a card too narrow).
            if (right == card && talkField.GetThemeFont("font") is { } font)
            {
                var hint = font.GetStringSize(talkField.PlaceholderText, HorizontalAlignment.Left, -1, talkField.GetThemeFontSize("font_size")).X;
                var inner = talkField.Size.X - (talkField.GetThemeStylebox("normal")?.GetMinimumSize().X ?? 0);
                if (hint > inner + 0.5f)
                {
                    problems.Add($"card: the talk field shows {inner:0} of {hint:0} px of its hint");
                }
            }

            if (portraits.Compact != (count >= style.Tokens.Zones.PortraitsCompactFrom))
            {
                problems.Add($"{count} portraits: compact is {portraits.Compact}");
            }

            if (portraits.MoreButton.Visible != (count > showing))
            {
                problems.Add($"{count} portraits: '+N' shown is {portraits.MoreButton.Visible}");
            }
        }

        stage.QueueFree();
        return problems;
    }

    /// <summary>
    /// A1736 (review GW3 #470): the window theme makes every label light. A label that stands inside a light framed part
    /// (a panel with its own opaque fill) would be light on light - each visible label is measured against the fill of
    /// the nearest framed parent it really stands on, not against the window's dark ground.
    /// </summary>
    private static IEnumerable<string> FramedLabelProblems(string name, Control window)
    {
        static RgbaColor Rgba(Color c) => new(c.R, c.G, c.B, c.A);
        foreach (var label in window.FindChildren("*", nameof(Label), true, false).OfType<Label>().Where(l => l.IsVisibleInTree() && l.Text.Length > 0))
        {
            for (var parent = label.GetParent() as Control; parent is not null && parent != window; parent = parent.GetParent() as Control)
            {
                if (parent is PanelContainer panel && panel.GetThemeStylebox("panel") is StyleBoxFlat { BgColor.A: > 0.5f } fill)
                {
                    var ratio = RgbaColor.Contrast(Rgba(label.GetThemeColor("font_color")), Rgba(fill.BgColor with { A = 1 }));
                    if (ratio < UiStandardRules.TextContrast)
                    {
                        yield return $"{name}: label '{label.Text}' on the fill of {parent.Name} has contrast {ratio:0.00}, needs {UiStandardRules.TextContrast:0.0}";
                    }

                    break;
                }
            }
        }
    }

    /// <summary>
    /// Text that stands on the world (S1b, TL #25715): its outline is thick enough, and its colour against the outline
    /// over every light ground of the data (snow, sand, light grass, sky) reaches the text contrast. Headless renders
    /// nothing, so the check reads the colours the part really draws with from its theme.
    /// </summary>
    private static IEnumerable<string> WorldTextProblems(UiStyle style, string name, Control part)
    {
        var label = part.FindChildren("*", nameof(Label), true, false).OfType<Label>().FirstOrDefault(l => l.IsVisibleInTree() && l.Text.Length > 0);
        if (label is null)
        {
            yield return $"{name}: no text on the world found";
            yield break;
        }

        var size = label.GetThemeConstant("outline_size");
        if (size < UiStandardRules.MinWorldOutlinePx)
        {
            yield return $"{name}: text outline {size} px, needs {UiStandardRules.MinWorldOutlinePx}";
        }

        static RgbaColor Rgba(Color c) => new(c.R, c.G, c.B, c.A);
        var text = Rgba(label.GetThemeColor("font_color"));
        var outline = Rgba(label.GetThemeColor("font_outline_color"));
        var white = new RgbaColor(1, 1, 1, 1);
        foreach (var ground in style.Tokens.Window.WorldCheckGrounds.Select(RgbaColor.Parse))
        {
            var ring = outline.Over(ground.Over(white));
            var ratio = RgbaColor.Contrast(text.Over(ring), ring);
            if (ratio < UiStandardRules.TextContrast)
            {
                yield return $"{name}: text in its outline over the ground {ground} has contrast {ratio:0.00}, needs {UiStandardRules.TextContrast:0.0}";
            }
        }
    }

    /// <summary>A long date (B2759): late in a season, many game days, a high year.</summary>
    private static Dictionary<string, string> DateValues(Node host) =>
        new() { ["season"] = host.Tr("time.season.autumn"), ["day"] = "28", ["days"] = "28", ["game_day"] = "9999", ["year"] = "120" };

    /// <summary>Season and game day as the time block shows them side by side.</summary>
    private static string DateSample(Node host) =>
        TextFill.Fill(host.Tr("ui.clock.date"), DateValues(host)) + "  " + TextFill.Fill(host.Tr("ui.clock.day_year"), DateValues(host));

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

        // A1338 (B6-6, KR5): 100 inhabitants in the list without stutter - the game refills the table on every refresh; the
        // table (a Tree) draws only the visible rows, so refilling 100 must stay well inside one frame (a quarter of 16.7 ms).
        var hundred = Enumerable.Range(1, 100).Select(i => new GwRow($"inh-{i}", [$"Name {i}", "…", $"{(i * 7) % 100}", $"{i % 3}"], [i, 0, (i * 7) % 100, i % 3])).ToList();
        // The median of five blocks of ten refills, so one busy moment of the machine does not decide (review GW2 #596).
        var blocks = new List<double>();
        for (var block = 0; block < 5; block++)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 10; i++)
            {
                table.SetRows(hundred);
            }

            blocks.Add(clock.Elapsed.TotalMilliseconds / 10);
        }

        var refillMs = blocks.Order().ElementAt(blocks.Count / 2);
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"UI_TABLE rows=100 refill_ms={refillMs:0.00}"));
        if (refillMs > 16.7 / 4)
        {
            problems.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"table: refilling 100 rows takes {refillMs:0.00} ms (more than a quarter frame)"));
        }

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
        // Beside a side bar as in the game, and with the width the table asks for (review GW1 #243: with the
        // whole width to itself the panel could not show that it leaves the picture).
        var bar = new GwSideBar();
        bar.Add("inhabitants", "ui.inhabitants.title");
        bar.Add("questions", "ui.questions.title");
        stage.AddChild(bar);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        var beside = bar.GetCombinedMinimumSize().X + style.Tokens.Window.Gap;
        if (table.WantedWidth <= 0)
        {
            problems.Add("the table has not measured its headers in the tree (WantedWidth is 0)");
        }

        panel.Dock(area, style.With(style.Settings with { TextScale = style.Tokens.TextScale.Max }), beside, contentPx: table.WantedWidth);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        panel.Dock(area, style, beside, contentPx: table.WantedWidth);
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        panel.Dock(area, style, beside, contentPx: table.WantedWidth);
        foreach (var other in bar.Panels)
        {
            other.Free();
        }

        bar.QueueFree();
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
        // The header shows in full while the panel has room to grow; a panel that already takes all the room
        // beside the side bar (smallest picture, largest text) lets the table scroll sideways instead of
        // leaving the picture (review GW1 #243).
        var atRoom = panel.Size.X + 0.5f >= area.X - (2 * margin) - beside;
        if (table.HeaderMinimumWidth() > inner + 0.5f && !atRoom)
        {
            problems.Add($"table header needs {table.HeaderMinimumWidth():0} px, panel offers {inner:0} px");
        }

        // One plain hint and thirteen knocks bundled into one (A1684: the box shows at most two).
        if (board.Visible.Count != Math.Min(2, style.Tokens.Toast.MaxVisible) || board.Visible[^1].Count != 13)
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
