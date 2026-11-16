using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Game.Ui.Components;
using GlassWorld.Presentation.Ui;
using Godot;

namespace GlassWorld.Game.Ui.Screens;

/// <summary>
/// Settings (B6 R4; D957 v4 section 10, A1737): tabs on the left from top to bottom, their rows from the data
/// (content/ui/settings.json); beside the rows a column says what the row under the pointer does (not a tooltip);
/// each tab can restore its defaults. Every change takes effect at once through <see cref="Changed"/>; the caller
/// stores the settings in the user folder, never in a save game.
/// </summary>
public partial class SettingsWindow : GwWindow
{
    private const string TabPrefix = "ui.settings.tab.";
    private const string AiTab = TabPrefix + "ai";
    private const string DescriptionHint = "ui.settings.desc_hint";

    // What the name row can say is wrong; the page keeps room for the longest, so it never grows (B2791).
    private static readonly string[] NameProblems = ["ui.settings.problem.name_long", "ui.settings.problem.name_characters", "ui.settings.problem.name_blocked", "ui.settings.problem.name_empty"];

    private static readonly Lazy<SettingsLayout> Layout = new(() =>
        GlassWorld.Contracts.Validation.ContractSerializer.Deserialize<SettingsLayout>(UiSetup.ReadEmbedded("content.ui.settings.json")));

    private PlayerSettings _settings = new();
    private PlayerSettings _defaults = new();
    private static string _lastTab = string.Empty;
    private readonly ButtonGroup _group = new();
    private readonly Dictionary<string, (Button Tab, VBoxContainer Page)> _pages = new(StringComparer.Ordinal);

    // How each row shows a settings value again without raising a change (after "restore defaults").
    private readonly List<Action<PlayerSettings>> _shows = [];
    // The parts are made once here and placed by Build; a part made and then replaced would stay outside the tree
    // and never be freed (the layout check builds hundreds of these windows).
    private readonly VBoxContainer _tabs = new() { Name = "Tabs" };
    private readonly VBoxContainer _pagesBox = new() { Name = "Pages", SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly HBoxContainer _body = new() { Name = "Body" };
    private readonly VBoxContainer _pageColumn = new() { Name = "PageColumn", SizeFlagsHorizontal = SizeFlags.ExpandFill };
    private readonly VSeparator _descriptionLine = new();
    private readonly VSeparator _tabsLine = new();
    private readonly HFlowContainer _tabsTop = new() { Name = "TabsTop", Visible = false };
    private readonly Label _description = new()
    {
        Name = "Description",
        Text = DescriptionHint,
        ThemeTypeVariation = UiTheme.SmallLabel,
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
        SizeFlagsVertical = SizeFlags.ShrinkBegin,
    };

    // The page the rows added next go to (set by Section).
    private VBoxContainer _page = null!;
    private Label? _nameProblem;

    // Every text the description column can show (the room of the longest stays reserved, B2791).
    private readonly List<string> _descriptions = [DescriptionHint];
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
        _labelWidth = style.Tokens.Window.SettingsLabelWidth;
        _settings = settings;
        _defaults = PlayerSettings.Parse(null, limits);

        // D957 v4 section 10: the tabs stand on the left from top to bottom, the page beside them, the description on the right.
        Content.AddChild(_tabsTop);
        _description.CustomMinimumSize = new Vector2(_labelWidth, 0);
        UiFit.LongestWordFits(_description, _labelWidth, _descriptions);
        _body.AddChild(_tabs);
        _body.AddChild(_tabsLine);
        _pageColumn.AddChild(_pagesBox);
        _body.AddChild(_pageColumn);
        _body.AddChild(_descriptionLine);
        _body.AddChild(_description);
        Content.AddChild(_body);

        foreach (var tab in Layout.Value.Tabs)
        {
            Section(TabPrefix + tab.Id);
            if (tab.Rows.Count == 0)
            {
                // A tab whose settings come later says so (graphics, controls: A1737 b and c).
                var coming = new Label { Text = "ui.settings.coming", AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                UiFit.LongestWordFits(coming, _labelWidth);
                _page.AddChild(coming);
                continue;
            }

            foreach (var row in tab.Rows)
            {
                AddRow(row, settings, limits, mix, style, guard);
            }

            var rows = tab.Rows.ToList();
            if (rows.Any(r => r != "ai"))
            {
                var restore = new Button { Text = "ui.settings.restore", SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
                restore.Pressed += () => Restore(rows);
                _page.AddChild(restore);
            }
        }

        AddAction("ui.common.close").Pressed += Close;
    }

    /// <summary>The controls of one row of the data (its id names the setting).</summary>
    private void AddRow(string id, PlayerSettings settings, SettingsLimits limits, SoundMix mix, UiStyle style, GlassWorld.Simulation.Safety.TextGuard guard)
    {
        switch (id)
        {
            case "player_name":
                var name = new LineEdit { Text = settings.PlayerName ?? string.Empty, MaxLength = limits.PlayerNameMaxLength };
                UiFit.HintFits(name, style.Tokens.Window.FieldMinWidth, (float)style.Tokens.Window.FieldMaxShare);
                // Always in its place, empty while the name is fine: a message that appears does not move the window (B2791).
                var nameProblem = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
                _nameProblem = nameProblem;
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
                    if (problem is null && text != (_settings.PlayerName ?? string.Empty))
                    {
                        Apply(_settings with { PlayerName = text.Length == 0 ? null : text });
                    }
                }

                name.TextSubmitted += _ => TakeName();
                name.FocusExited += TakeName;
                Row("ui.settings.player_name", name, "ui.settings.player_name.desc");
                _page.AddChild(nameProblem);
                _shows.Add(s => name.Text = s.PlayerName ?? string.Empty);
                break;

            case "language":
                var language = new OptionButton();
                foreach (var locale in limits.Locales.Order(StringComparer.Ordinal))
                {
                    language.AddItem("ui.settings.language." + locale);
                    language.SetItemMetadata(language.ItemCount - 1, locale);
                }

                void ShowLanguage(PlayerSettings s)
                {
                    var wanted = s.Language ?? TranslationServer.GetLocale();
                    for (var i = 0; i < language.ItemCount; i++)
                    {
                        if (language.GetItemMetadata(i).AsString() == wanted)
                        {
                            language.Select(i);
                        }
                    }
                }

                ShowLanguage(settings);
                language.ItemSelected += index => Apply(_settings with { Language = language.GetItemMetadata((int)index).AsString() });
                Row("ui.settings.language", language, "ui.settings.language.desc");
                _shows.Add(ShowLanguage);
                break;

            case "ui_scale":
                Row("ui.settings.ui_scale", SizeSlider(limits.Tokens.UiScale, settings.UiScale, v => _settings with { UiScale = v }, s => s.UiScale, style), "ui.settings.ui_scale.desc");
                break;

            case "text_scale":
                Row("ui.settings.text_scale", SizeSlider(limits.Tokens.TextScale, settings.TextScale, v => _settings with { TextScale = v }, s => s.TextScale, style), "ui.settings.text_scale.desc");
                break;

            case "high_contrast":
                Row("ui.settings.high_contrast", Toggle(settings.HighContrast, v => _settings with { HighContrast = v }, s => s.HighContrast), "ui.settings.high_contrast.desc");
                break;

            case "reduce_motion":
                Row("ui.settings.reduce_motion", Toggle(settings.ReduceMotion, v => _settings with { ReduceMotion = v }, s => s.ReduceMotion), "ui.settings.reduce_motion.desc");
                break;

            case "feeling_symbols":
                Row("ui.settings.feeling_symbols", Toggle(settings.FeelingSymbols, v => _settings with { FeelingSymbols = v }, s => s.FeelingSymbols), "ui.settings.feeling_symbols.desc");
                break;

            case "hide_ui":
                Row("ui.settings.hide_ui", Toggle(settings.HideUi, v => _settings with { HideUi = v }, s => s.HideUi), "ui.settings.hide_ui.desc");
                break;

            case "hints_until_dismissed":
                Row("ui.settings.hints_until_dismissed", Toggle(settings.HintsUntilDismissed, v => _settings with { HintsUntilDismissed = v }, s => s.HintsUntilDismissed), "ui.settings.hints_until_dismissed.desc");
                break;

            case "hint_duration":
                var duration = new SpinBox { MinValue = 1, MaxValue = limits.Tokens.Toast.MaxDurationFactor, Step = 1, Value = settings.HintDurationFactor };
                duration.ValueChanged += v => Apply(_settings with { HintDurationFactor = (int)v });
                Row("ui.settings.hint_duration", duration, "ui.settings.hint_duration.desc");
                _shows.Add(s => duration.SetValueNoSignal(s.HintDurationFactor));
                break;

            case "volume":
                foreach (var bus in mix.Buses)
                {
                    var busId = BusKey(bus.Id);
                    var standard = bus.VolumeDb;
                    var slider = new HSlider
                    {
                        MinValue = limits.VolumeDb.Min,
                        MaxValue = limits.VolumeDb.Max,
                        Step = 1,
                        Value = settings.BusVolumesDb.TryGetValue(busId, out var db) ? db : standard,
                        SizeFlagsHorizontal = SizeFlags.ExpandFill,
                        CustomMinimumSize = new Vector2(style.Tokens.Window.CardBarWidth, 0),
                    };
                    slider.ValueChanged += v => Apply(_settings with { BusVolumesDb = new Dictionary<string, double>(_settings.BusVolumesDb, StringComparer.Ordinal) { [busId] = v } });
                    if (bus.Off == true)
                    {
                        // Switched off in the data until replaced (W1699): locked, the tooltip says why.
                        slider.Editable = false;
                        slider.TooltipText = "ui.settings.volume.off_tip";
                    }

                    Row("ui.settings.volume." + busId, slider, "ui.settings.volume.desc");
                    _shows.Add(s => slider.SetValueNoSignal(s.BusVolumesDb.TryGetValue(busId, out var v) ? v : standard));
                }

                break;

            case "ai":
                // The AI tab is filled by AddAi (it needs the running world's service).
                break;

            // A1749 (D957 v4 section 10): the game options.
            case "autosave":
                Row("ui.settings.autosave", Choice(
                    [.. GameOptions.AutoSaveModes.Select(m => ((string?)m, GameOptions.AutoSaveKey(m)))],
                    s => s.AutoSave, v => _settings with { AutoSave = v ?? GameOptions.AutoSaveDay }), "ui.settings.autosave.desc");
                break;

            case "start_speed":
                string SpeedText(int speed) => speed switch
                {
                    0 => Tr("ui.time.pause"),
                    1 => Tr("ui.time.normal"),
                    _ => TextFill.Fill(Tr("ui.time.fast"), new Dictionary<string, string> { ["factor"] = speed.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
                };
                Row("ui.settings.start_speed", Choice(
                    [(null, "ui.settings.start_speed.saved"), .. limits.SpeedChoices.Select(c => ((string?)c.ToString(System.Globalization.CultureInfo.InvariantCulture), SpeedText(c)))],
                    s => s.StartSpeed?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    v => _settings with { StartSpeed = v is null ? null : int.Parse(v, System.Globalization.CultureInfo.InvariantCulture) }), "ui.settings.start_speed.desc");
                break;

            case "pause_on_knock":
                Row("ui.settings.pause_on_knock", Toggle(settings.PauseOnKnock, v => _settings with { PauseOnKnock = v }, s => s.PauseOnKnock), "ui.settings.pause_on_knock.desc");
                break;

            case "data_folder":
                // A1787 (B2824, GW1 #27301): where this game keeps worlds, settings and logs - one click opens it; the path in
                // the tooltip. Nothing to restore: it is no setting.
                var folder = new Button { Text = "ui.settings.data_folder.open", SizeFlagsHorizontal = SizeFlags.ShrinkEnd, TooltipText = GameData.Root, TooltipAutoTranslateMode = AutoTranslateModeEnum.Disabled };
                folder.Pressed += () => OS.ShellOpen(GameData.Root);
                Row("ui.settings.data_folder", folder, "ui.settings.data_folder.desc");
                break;

            // A1748 (D957 v4 section 10): the graphics. Mode and size rebuild the window and ask 'Behalten?' (Main).
            case "window_mode":
                Row("ui.settings.window_mode", Choice(
                    [(null, "ui.settings.window_mode.start"), .. DisplayChoices.WindowModes.Select(m => ((string?)m, DisplayChoices.ModeKey(m)))],
                    s => s.WindowMode, v => _settings with { WindowMode = v }), "ui.settings.window_mode.desc");
                break;

            case "resolution":
                var screen = DisplayServer.ScreenGetSize(DisplayServer.WindowGetCurrentScreen()); // the screen the window is on (review GW3 #530)
                Row("ui.settings.resolution", Choice(
                    [(null, "ui.settings.resolution.start"), .. DisplayChoices.Fitting(limits.Tokens.Graphics.Resolutions, screen.X, screen.Y).Select(r => ((string?)r, r))],
                    s => s.Resolution, v => _settings with { Resolution = v }), "ui.settings.resolution.desc");
                break;

            // A1749 (B2825): the mouse axes, each on its own; the default is as before.
            case "invert_turn_x":
                Row("ui.settings.invert_turn_x", Toggle(settings.InvertTurnX, v => _settings with { InvertTurnX = v }, s => s.InvertTurnX), "ui.settings.invert_turn_x.desc");
                break;

            case "invert_turn_y":
                Row("ui.settings.invert_turn_y", Toggle(settings.InvertTurnY, v => _settings with { InvertTurnY = v }, s => s.InvertTurnY), "ui.settings.invert_turn_y.desc");
                break;

            case "invert_pan_x":
                Row("ui.settings.invert_pan_x", Toggle(settings.InvertPanX, v => _settings with { InvertPanX = v }, s => s.InvertPanX), "ui.settings.invert_pan_x.desc");
                break;

            case "invert_pan_y":
                Row("ui.settings.invert_pan_y", Toggle(settings.InvertPanY, v => _settings with { InvertPanY = v }, s => s.InvertPanY), "ui.settings.invert_pan_y.desc");
                break;

            case "vsync":
                Row("ui.settings.vsync", Toggle(settings.Vsync, v => _settings with { Vsync = v }, s => s.Vsync), "ui.settings.vsync.desc");
                break;

            case "max_fps":
                Row("ui.settings.max_fps", Choice(
                    [.. limits.Tokens.Graphics.FpsChoices.Select(f => ((string?)f.ToString(System.Globalization.CultureInfo.InvariantCulture), f == 0 ? "ui.settings.max_fps.none" : f.ToString(System.Globalization.CultureInfo.InvariantCulture)))],
                    s => s.MaxFps.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    v => _settings with { MaxFps = int.Parse(v ?? "0", System.Globalization.CultureInfo.InvariantCulture) }), "ui.settings.max_fps.desc");
                break;

            case "quality":
                Row("ui.settings.quality", Choice(
                    [.. limits.QualityLevels.Select(l => ((string?)l, DisplayChoices.QualityKey(l)))],
                    s => s.Quality ?? limits.QualityDefault,
                    v => _settings with { Quality = v == limits.QualityDefault ? null : v }), "ui.settings.quality.desc");
                break;

            case "log_folder":
                // A1787 part 2 (Marcel W2246, #525): the logs lie in their own folder (log\ next to the game) - for a bug report.
                var logs = new Button { Text = "ui.settings.log_folder.open", SizeFlagsHorizontal = SizeFlags.ShrinkEnd, TooltipText = GameData.Logs, TooltipAutoTranslateMode = AutoTranslateModeEnum.Disabled };
                logs.Pressed += () => OS.ShellOpen(GameData.Logs);
                Row("ui.settings.log_folder", logs, "ui.settings.log_folder.desc");
                break;

            default:
                throw new InvalidDataException($"content/ui/settings.json names the unknown setting '{id}'.");
        }
    }

    /// <summary>"Restore defaults" of a tab (D957 v4 section 10): only its own rows go back, the controls show it at once.</summary>
    private void Restore(IReadOnlyList<string> rows)
    {
        var d = _defaults;
        var s = _settings;
        foreach (var row in rows)
        {
            s = row switch
            {
                "language" => s with { Language = d.Language },
                "player_name" => s with { PlayerName = d.PlayerName },
                "ui_scale" => s with { UiScale = d.UiScale },
                "text_scale" => s with { TextScale = d.TextScale },
                "high_contrast" => s with { HighContrast = d.HighContrast },
                "reduce_motion" => s with { ReduceMotion = d.ReduceMotion },
                "feeling_symbols" => s with { FeelingSymbols = d.FeelingSymbols },
                "hide_ui" => s with { HideUi = d.HideUi },
                "hints_until_dismissed" => s with { HintsUntilDismissed = d.HintsUntilDismissed },
                "hint_duration" => s with { HintDurationFactor = d.HintDurationFactor },
                "volume" => s with { BusVolumesDb = new Dictionary<string, double>(d.BusVolumesDb, StringComparer.Ordinal) },
                "window_mode" => s with { WindowMode = d.WindowMode },
                "resolution" => s with { Resolution = d.Resolution },
                "vsync" => s with { Vsync = d.Vsync },
                "autosave" => s with { AutoSave = d.AutoSave },
                "start_speed" => s with { StartSpeed = d.StartSpeed },
                "pause_on_knock" => s with { PauseOnKnock = d.PauseOnKnock },
                "invert_turn_x" => s with { InvertTurnX = d.InvertTurnX },
                "invert_turn_y" => s with { InvertTurnY = d.InvertTurnY },
                "invert_pan_x" => s with { InvertPanX = d.InvertPanX },
                "invert_pan_y" => s with { InvertPanY = d.InvertPanY },
                "max_fps" => s with { MaxFps = d.MaxFps },
                "quality" => s with { Quality = d.Quality },
                _ => s,
            };
        }

        foreach (var show in _shows)
        {
            show(s);
        }

        Apply(s);
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

    private HBoxContainer SizeSlider(UiScaleRange range, double value, Func<double, PlayerSettings> change, Func<PlayerSettings, double> read, UiStyle style)
    {
        var minWidth = style.Tokens.Window.CardBarWidth;
        var applyMs = style.Tokens.Window.SettingsApplyMs;
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
        _shows.Add(s =>
        {
            slider.SetValueNoSignal(read(s));
            label.Text = Percent(read(s));
        });
        return box;
    }

    /// <summary>
    /// A1748: a choice of values (null = 'as the game starts') with their texts - a key, or a plain number or size shown
    /// as it is; it shows the settings' value and applies a new one.
    /// </summary>
    private OptionButton Choice(IReadOnlyList<(string? Value, string Text)> items, Func<PlayerSettings, string?> read, Func<string?, PlayerSettings> change)
    {
        var choice = new OptionButton();
        foreach (var (_, text) in items)
        {
            choice.AddItem(text);
        }

        void Show(PlayerSettings s)
        {
            var value = read(s);
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].Value == value)
                {
                    choice.Select(i);
                }
            }
        }

        Show(_settings);
        choice.ItemSelected += index => Apply(change(items[(int)index].Value));
        _shows.Add(Show);
        return choice;
    }

    /// <summary>A1748: shows these settings in every control without applying them again (after 'Behalten?' fell back).</summary>
    public void Show(PlayerSettings settings)
    {
        _settings = settings;
        foreach (var show in _shows)
        {
            show(settings);
        }
    }

    private CheckButton Toggle(bool value, Func<bool, PlayerSettings> change, Func<PlayerSettings, bool> read)
    {
        var toggle = new CheckButton { ButtonPressed = value };
        toggle.Toggled += v => Apply(change(v));
        _shows.Add(s => toggle.SetPressedNoSignal(read(s)));
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
        // The AI tab of the data (D957 v4 section 10: state on top, learning by day below, report at the bottom).
        if (_pages.TryGetValue(AiTab, out var aiTab))
        {
            _page = aiTab.Page;
        }
        else
        {
            Section(AiTab);
        }

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

            // The answer came later than the window's fit: every tab takes the new height at once (B2791).
            FitAgain();
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

            // Not offered (any more): a world that learned by day goes back to the night - done here, before the
            // controls are built, not as a side effect of building them (review GW2 #293).
            if (answer.Check.LearnByDay is { Offered: false } && learnByDay?.Invoke() == true)
            {
                setLearnByDay?.Invoke(false);
            }

            Show([.. SelfCheckView.Lines(answer.Check).SelectMany(AiLine), .. LearnByDayControls(answer.Check.LearnByDay)]);
        }

        // "Auch tagsüber lernen" (A1568, B2652): offered only when the service says the PC is strong enough; then a
        // switch (off by default) with the hint what it costs. Not offered: the reason in words.
        IEnumerable<Control> LearnByDayControls(Contracts.LearnByDayOffer? offer)
        {
            if (offer is null || learnByDay is null || setLearnByDay is null)
            {
                yield break;
            }

            yield return new Label { Text = "ui.settings.learn_by_day", ThemeTypeVariation = UiTheme.SubheaderLabel };
            if (!offer.Offered)
            {
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
            FitAgain();
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
            FitAgain();
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

        // Into the room of the last fit (the layout check fits into a smaller picture than the viewport).
        FitAgain();
    }

    /// <inheritdoc/>
    protected override void Arrange(Vector2 limit)
    {
        // The description beside the rows (D957 v4 section 10); where that is wider than the room (UI 200 %, large
        // text) it stands below them. Every tab as wide as the widest, so the window does not jump (D705).
        PlaceTabs(left: true);
        PlaceDescription(beside: true);
        if (GetCombinedMinimumSize().X > limit.X)
        {
            PlaceDescription(beside: false);
        }

        // Still too wide (the smallest picture with the largest text): the tabs go on top as a row that wraps.
        if (GetCombinedMinimumSize().X > limit.X)
        {
            PlaceTabs(left: false);
        }

        ReserveHeights();
    }

    /// <summary>
    /// One size for every tab (B2791): the pages as high as the highest, measured at the width they share; the
    /// description with room for its longest text and the name row with room for its longest message, so neither
    /// grows the window when the pointer moves or the player types.
    /// </summary>
    private void ReserveHeights()
    {
        var width = _pagesBox.CustomMinimumSize.X;
        if (_nameProblem is not null)
        {
            _nameProblem.CustomMinimumSize = new Vector2(0, UiFit.HighestText(_nameProblem, NameProblems, width));
        }

        // Beside the rows the description is as wide as its own minimum, below them as wide as the pages.
        var beside = _description.GetParent() == _body;
        var descriptionWidth = beside ? _description.CustomMinimumSize.X : width;
        _description.CustomMinimumSize = new Vector2(_description.CustomMinimumSize.X, UiFit.HighestText(_description, _descriptions, descriptionWidth));

        _pagesBox.CustomMinimumSize = new Vector2(width, 0);
        var height = _pages.Values.Max(p => UiFit.HeightAt(p.Page, width));
        _pagesBox.CustomMinimumSize = new Vector2(width, height);
    }

    private void PlaceTabs(bool left)
    {
        var target = left ? (Container)_tabs : _tabsTop;
        foreach (var (tab, _) in _pages.Values)
        {
            if (tab.GetParent() != target)
            {
                tab.GetParent()?.RemoveChild(tab);
                target.AddChild(tab);
            }
        }

        _tabs.Visible = left;
        _tabsLine.Visible = left;
        _tabsTop.Visible = !left;
    }

    private void PlaceDescription(bool beside)
    {
        var parent = beside ? (Container)_body : _pageColumn;
        if (_description.GetParent() != parent)
        {
            _description.GetParent()?.RemoveChild(_description);
            parent.AddChild(_description);
        }

        _descriptionLine.Visible = beside;
        _pagesBox.CustomMinimumSize = Vector2.Zero;
        _pagesBox.CustomMinimumSize = new Vector2(_pages.Values.Max(p => p.Page.GetCombinedMinimumSize().X), 0);
    }

    /// <summary>Starts the tab of a section: the controls added next belong to it. The tab last used opens again.</summary>
    private void Section(string key)
    {
        var first = _pages.Count == 0;
        var tab = new Button { Text = key, ToggleMode = true, ButtonGroup = _group, Alignment = HorizontalAlignment.Left };
        _page = new VBoxContainer { Name = "Page" + _pages.Count, Visible = first };
        tab.SetPressedNoSignal(first);
        tab.Pressed += () => ShowTab(key);
        _tabs.AddChild(tab);
        _pagesBox.AddChild(_page);
        _pages[key] = (tab, _page);
        if (key == _lastTab)
        {
            ShowTab(key);
        }
    }

    private void Row(string key, Control control, string descriptionKey)
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
        // A switch is only as wide as itself: stretched it was a wide empty box with the switch at its end (B2791 pictures).
        control.SizeFlagsHorizontal = control is CheckButton ? SizeFlags.ShrinkBegin : SizeFlags.ExpandFill;
        row.AddChild(control);

        // The description column says what the row under the pointer (or with the keyboard focus) does (D957 v4 section 10).
        if (!_descriptions.Contains(descriptionKey))
        {
            _descriptions.Add(descriptionKey);
        }

        row.MouseFilter = MouseFilterEnum.Pass;
        row.MouseEntered += () => _description.Text = descriptionKey;
        control.FocusEntered += () => _description.Text = descriptionKey;
        _page.AddChild(row);
    }

    private static string Percent(double value) => ((int)Math.Round(value * 100)).ToString(CultureInfo.InvariantCulture) + " %";
}
