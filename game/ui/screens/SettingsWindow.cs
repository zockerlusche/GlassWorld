using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Game.Ui.Components;
using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game.Ui.Screens;

/// <summary>
/// Settings (B6 R4): language, UI size and text size (separate, B2513), contrast, reduced motion,
/// hints, hiding the UI and the volume per bus (B8). Every change takes effect at once through
/// <see cref="Changed"/>; the caller stores the settings in the user folder, never in a save game.
/// AI level and computing night follow with B7.
/// </summary>
public partial class SettingsWindow : GwWindow
{
    private PlayerSettings _settings = new();
    private static string _lastTab = string.Empty;
    private readonly ButtonGroup _group = new();
    private readonly Dictionary<string, (Button Tab, VBoxContainer Page)> _pages = new(StringComparer.Ordinal);
    private HFlowContainer _tabs = new();
    private VBoxContainer _page = new();
    private float _labelWidth;

    /// <summary>Raised after every change with the new, already clamped settings.</summary>
    public event Action<PlayerSettings>? Changed;

    /// <summary>Builds the controls for the current settings.</summary>
    public void Build(PlayerSettings settings, SettingsLimits limits, SoundMix mix, UiStyle style, GlassWorld.Simulation.Safety.TextGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(mix);
        ArgumentNullException.ThrowIfNull(style);
        Setup("ui.settings.title", style);

        // One tab per section (TL #19986): the window stays short, each page shows only its own controls.
        _tabs = new HFlowContainer { Name = "Tabs" };
        Content.AddChild(_tabs);
        _labelWidth = style.Tokens.Window.SettingsLabelWidth;
        _settings = settings;

        Section("ui.settings.section.player");
        var name = new LineEdit { Text = settings.PlayerName ?? string.Empty, MaxLength = limits.PlayerNameMaxLength, PlaceholderText = "ui.settings.player_name_hint" };
        UiFit.HintFits(name, style.Tokens.Window.FieldMinWidth, (float)style.Tokens.Window.FieldMaxShare);
        var nameProblem = new Label { Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        void TakeName()
        {
            // Empty = no name; otherwise the same checks as every player input (length, content guard).
            var text = name.Text.Trim();
            var problem = text.Length == 0 ? null : Presentation.World.NewWorldModel.InputProblem(text, limits.PlayerNameMaxLength, guard);
            nameProblem.Text = problem switch
            {
                "long" => "ui.settings.problem.name_long",
                "characters" => "ui.settings.problem.name_characters",
                "blocked" => "ui.settings.problem.name_blocked",
                null => string.Empty,
                _ => "ui.settings.problem.name_empty",
            };
            nameProblem.Visible = problem is not null;
            if (problem is null && text != (_settings.PlayerName ?? string.Empty))
            {
                Apply(_settings with { PlayerName = text.Length == 0 ? null : text });
            }
        }

        name.TextSubmitted += _ => TakeName();
        name.FocusExited += TakeName;
        Row("ui.settings.player_name", name);
        _page.AddChild(nameProblem);

        Section("ui.settings.section.language");
        var language = new OptionButton();
        foreach (var locale in limits.Locales.Order(StringComparer.Ordinal))
        {
            language.AddItem("ui.settings.language." + locale);
            language.SetItemMetadata(language.ItemCount - 1, locale);
            if (locale == (settings.Language ?? TranslationServer.GetLocale()))
            {
                language.Selected = language.ItemCount - 1;
            }
        }

        language.ItemSelected += index => Apply(_settings with { Language = language.GetItemMetadata((int)index).AsString() });
        Row("ui.settings.language", language);

        Section("ui.settings.section.display");
        Row("ui.settings.ui_scale", SizeSlider(limits.Tokens.UiScale, settings.UiScale, v => _settings with { UiScale = v }, style.Tokens.Window.CardBarWidth, style.Tokens.Window.SettingsApplyMs));
        Row("ui.settings.text_scale", SizeSlider(limits.Tokens.TextScale, settings.TextScale, v => _settings with { TextScale = v }, style.Tokens.Window.CardBarWidth, style.Tokens.Window.SettingsApplyMs));
        Row("ui.settings.high_contrast", Toggle(settings.HighContrast, v => _settings with { HighContrast = v }));
        Row("ui.settings.reduce_motion", Toggle(settings.ReduceMotion, v => _settings with { ReduceMotion = v }));
        Row("ui.settings.feeling_symbols", Toggle(settings.FeelingSymbols, v => _settings with { FeelingSymbols = v }));
        Row("ui.settings.hide_ui", Toggle(settings.HideUi, v => _settings with { HideUi = v }));
        Row("ui.settings.zones_preview", Toggle(settings.ZonesPreview, v => _settings with { ZonesPreview = v }));

        Section("ui.settings.section.hints");
        var duration = new SpinBox { MinValue = 1, MaxValue = limits.Tokens.Toast.MaxDurationFactor, Step = 1, Value = settings.HintDurationFactor };
        duration.ValueChanged += v => Apply(_settings with { HintDurationFactor = (int)v });
        Row("ui.settings.hint_duration", duration);
        Row("ui.settings.hints_until_dismissed", Toggle(settings.HintsUntilDismissed, v => _settings with { HintsUntilDismissed = v }));

        Section("ui.settings.section.audio");
        foreach (var bus in mix.Buses)
        {
            var id = BusKey(bus.Id);
            var slider = new HSlider
            {
                MinValue = limits.VolumeDb.Min,
                MaxValue = limits.VolumeDb.Max,
                Step = 1,
                Value = settings.BusVolumesDb.TryGetValue(id, out var db) ? db : bus.VolumeDb,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(style.Tokens.Window.CardBarWidth, 0),
            };
            slider.ValueChanged += v => Apply(_settings with { BusVolumesDb = new Dictionary<string, double>(_settings.BusVolumesDb, StringComparer.Ordinal) { [id] = v } });
            if (bus.Off == true)
            {
                // Switched off in the data until replaced (W1699): locked, the tooltip says why.
                slider.Editable = false;
                slider.TooltipText = "ui.settings.volume.off_tip";
            }

            Row("ui.settings.volume." + id, slider);
        }

        AddAction("ui.common.close").Pressed += Close;
    }

    /// <summary>Id of a bus in the data and in the settings file (e.g. <c>music</c>).</summary>
    public static string BusKey(MixBusId id)
    {
#pragma warning disable CA1308 // The data ids are lower-case ASCII.
        return id.ToString().ToLowerInvariant();
#pragma warning restore CA1308
    }

    private void Apply(PlayerSettings settings)
    {
        _settings = settings;
        Changed?.Invoke(settings);
    }

    private HBoxContainer SizeSlider(UiScaleRange range, double value, Func<double, PlayerSettings> change, float minWidth, int applyMs)
    {
        // The value label is a number, not a key. With the mouse the size applies on release, so the
        // window does not jump under the pointer; with keyboard or controller (no drag) it applies after
        // a short pause (token settings_apply_ms; GW1 review #50 W1: before, it never applied).
        var box = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var slider = new HSlider { MinValue = range.Min, MaxValue = range.Max, Step = range.Step, Value = value, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(minWidth, 0) };
        var label = new Label { AutoTranslateMode = AutoTranslateModeEnum.Disabled, Text = Percent(value) };
        var pause = new Godot.Timer { OneShot = true, WaitTime = applyMs / 1000.0 };
        var dragging = false;
        slider.DragStarted += () => dragging = true;
        slider.ValueChanged += v =>
        {
            label.Text = Percent(v);
            if (!dragging)
            {
                pause.Start();
            }
        };
        slider.DragEnded += changed =>
        {
            dragging = false;
            if (changed)
            {
                pause.Stop();
                Apply(change(slider.Value));
            }
        };
        pause.Timeout += () => Apply(change(slider.Value));
        box.AddChild(slider);
        box.AddChild(label);
        box.AddChild(pause);
        return box;
    }

    private CheckButton Toggle(bool value, Func<bool, PlayerSettings> change)
    {
        var toggle = new CheckButton { ButtonPressed = value };
        toggle.Toggled += v => Apply(change(v));
        return toggle;
    }

    /// <summary>
    /// The AI self-check (W1703 rules 2-4): per AI a calm line (state, clear text, what to do), "check again",
    /// and a diagnostics report the player can pass on. <paramref name="check"/> and <paramref name="report"/>
    /// never throw; null (no service, e.g. in the main menu) shows the calm "not running" text.
    /// The report is also copied into <paramref name="reportFolder"/>, a folder the player can open.
    /// </summary>
    /// <param name="check">The service's self-check, or null without a running world.</param>
    /// <param name="report">Makes a diagnostics report, or null.</param>
    /// <param name="reportFolder">Where the player's copy of a report goes.</param>
    /// <param name="learnByDay">Whether the world learns by day now (A1568, B2652), or null without a world.</param>
    /// <param name="setLearnByDay">Switches learning by day on or off (a logged command of the core), or null.</param>
    public void AddAi(Func<Task<AiClient.SelfCheckAnswer>>? check, Func<Task<AiClient.DiagnosticsAnswer>>? report, string reportFolder, Func<bool>? learnByDay = null, Action<bool>? setLearnByDay = null)
    {
        Section("ui.settings.section.ai");

        // What the language AI does and what it does not (D763 Z30): familiar things out of habit, new ones ask it.
        _page.AddChild(new Label { Text = "ui.selfcheck.how", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var lines = new VBoxContainer { Name = "AiLines" };
        _page.AddChild(lines);
        var buttons = new HBoxContainer();
        var again = new Button { Text = "ui.selfcheck.again", Disabled = check is null };
        var make = new Button { Text = "ui.selfcheck.report", Disabled = report is null };
        var open = new Button { Text = "ui.selfcheck.open_folder", Visible = false };
        buttons.AddChild(again);
        buttons.AddChild(make);
        buttons.AddChild(open);
        _page.AddChild(buttons);
        var status = new Label { Name = "AiReportStatus", Visible = false, AutoTranslateMode = AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _page.AddChild(status);

        void Show(IEnumerable<Control> controls)
        {
            foreach (var child in lines.GetChildren())
            {
                lines.RemoveChild(child);
                child.QueueFree();
            }

            foreach (var control in controls)
            {
                lines.AddChild(control);
            }
        }

        Label Text(string key) => new() { Text = key, AutowrapMode = TextServer.AutowrapMode.WordSmart };

        async void Run()
        {
            if (check is null)
            {
                Show([Text(SelfCheckView.Unavailable)]);
                return;
            }

            again.Disabled = true;
            Show([Text(SelfCheckView.Checking)]);
            var answer = await check();
            if (!IsInstanceValid(this) || !IsInsideTree())
            {
                return;
            }

            again.Disabled = false;
            if (answer.Check is null)
            {
                GD.Print($"SELFCHECK_UNAVAILABLE {answer.Problem}");
                Show([Text(SelfCheckView.Unavailable)]);
                return;
            }

            Show([.. SelfCheckView.Lines(answer.Check).SelectMany(AiLine), .. LearnByDayControls(answer.Check.LearnByDay)]);
        }

        // "Auch tagsüber lernen" (A1568, B2652): offered only when the service says the PC is strong enough; then a
        // switch (off by default) with the hint what it costs. Not offered: the reason in words, and a switch that was
        // on goes off - new things are learned at night again.
        IEnumerable<Control> LearnByDayControls(Contracts.LearnByDayOffer? offer)
        {
            if (offer is null || learnByDay is null || setLearnByDay is null)
            {
                yield break;
            }

            yield return new Label { Text = "ui.settings.learn_by_day", ThemeTypeVariation = UiTheme.SubheaderLabel };
            if (!offer.Offered)
            {
                if (learnByDay())
                {
                    setLearnByDay(false);
                }

                yield return Text(offer.MessageKey);
                yield break;
            }

            var toggle = new CheckButton { Text = "ui.settings.learn_by_day_switch", ButtonPressed = learnByDay() };
            toggle.Toggled += on => setLearnByDay(on);
            yield return toggle;
            yield return Text(offer.HintKey);
        }

        IEnumerable<Control> AiLine(SelfCheckLine line)
        {
            // "Bild-KI: bereit" as a small header, the clear text below, then what to do.
            var head = new Label
            {
                Text = TextFill.Fill(Tr("ui.selfcheck.line"), new Dictionary<string, string> { ["ai"] = Tr(line.AiKey), ["state"] = Tr(line.StateKey) }),
                AutoTranslateMode = AutoTranslateModeEnum.Disabled,
                ThemeTypeVariation = UiTheme.SubheaderLabel,
            };
            yield return head;

            // A message the game does not know yet (newer service) gets the general text, never a raw key.
            var message = Tr(line.MessageKey).ToString();
            yield return new Label
            {
                Text = message == line.MessageKey ? Tr(SelfCheckView.UnknownMessage) : message,
                AutoTranslateMode = AutoTranslateModeEnum.Disabled,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            if (line.Detail.Length > 0)
            {
                yield return new Label { Text = line.Detail, AutoTranslateMode = AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            }

            if (line.FixKey is { } fix && Tr(fix).ToString() is var hint && hint != fix)
            {
                yield return new Label { Text = hint, AutoTranslateMode = AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            }
        }

        async void Report()
        {
            if (report is null)
            {
                return;
            }

            make.Disabled = true;
            status.Text = Tr("ui.selfcheck.report_running");
            status.Visible = true;
            var answer = await report();
            if (!IsInstanceValid(this) || !IsInsideTree())
            {
                return;
            }

            make.Disabled = false;
            var saved = answer.Report is { } made && DiagnosticsFile.Save(reportFolder, made.FileName, made.Text);
            if (!saved)
            {
                GD.Print($"DIAGNOSTICS_FAILED {answer.Problem ?? "could not write the copy"}");
            }

            status.Text = saved
                ? TextFill.Fill(Tr("ui.selfcheck.report_saved"), new Dictionary<string, string> { ["file"] = answer.Report!.FileName, ["folder"] = reportFolder })
                : Tr("ui.selfcheck.report_failed");
            open.Visible = saved;
        }

        again.Pressed += Run;
        make.Pressed += Report;
        open.Pressed += () => OS.ShellOpen(reportFolder);
        Run();
    }

    /// <summary>The text keys of the tabs, in their order (layout check: every tab is looked at).</summary>
    public IReadOnlyList<string> TabKeys => [.. _pages.Keys];

    /// <summary>Shows the tab of a section (its text key); the window fits itself again.</summary>
    public void ShowTab(string key)
    {
        if (!_pages.TryGetValue(key, out var chosen))
        {
            return;
        }

        _lastTab = key;
        foreach (var (tab, page) in _pages.Values)
        {
            page.Visible = page == chosen.Page;
            tab.SetPressedNoSignal(tab == chosen.Tab);
        }

        if (IsInsideTree())
        {
            FitInto(GetViewport().GetVisibleRect().Size);
        }
    }

    /// <summary>Starts the tab of a section: the controls added next belong to it. The tab last used opens again.</summary>
    private void Section(string key)
    {
        var first = _pages.Count == 0;
        var tab = new Button { Text = key, ToggleMode = true, ButtonGroup = _group };
        _page = new VBoxContainer { Name = "Page" + _pages.Count, Visible = first };
        tab.SetPressedNoSignal(first);
        tab.Pressed += () => ShowTab(key);
        _tabs.AddChild(tab);
        Content.AddChild(_page);
        _pages[key] = (tab, _page);
        if (key == _lastTab)
        {
            ShowTab(key);
        }
    }

    private void Row(string key, Control control)
    {
        // Long labels wrap (large text in 1920x1080 at UI 200 %); the minimum width keeps them from
        // collapsing to one character per line.
        var row = new HBoxContainer();
        var label = new Label
        {
            Text = key,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };

        // No word breaks inside, whatever the language and text size (D705 A6: "Gesamtlautstärk / e").
        UiFit.LongestWordFits(label, _labelWidth);
        row.AddChild(label);
        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(control);
        _page.AddChild(row);
    }

    private static string Percent(double value) => ((int)Math.Round(value * 100)).ToString(CultureInfo.InvariantCulture) + " %";
}
