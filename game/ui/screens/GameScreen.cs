using System.Globalization;
using GlassWorld.Contracts;
using GlassWorld.Game.Ui.Components;
using GlassWorld.Presentation.Ui;
using GlassWorld.Presentation.World;
using GlassWorld.Simulation.Decisions;
using Godot;

namespace GlassWorld.Game.Ui.Screens;

/// <summary>
/// The game screen of B6 (R5, R6, R7, R12, R15; D366 section 1a): world clock and time control at the
/// top, the side bar at the right (inhabitants, open questions), the inhabitant card, question marks
/// over heads (B2487) and calm hints. It only shows what the core reports (WorldFeed) and sends the
/// player's interventions as commands the core checks; it decides nothing for an inhabitant.
/// </summary>
public partial class GameScreen : CanvasLayer
{
    private const string MarkNode = "QuestionMark";

    private readonly Control _root = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly GwSideBar _sideBar = new();
    private readonly GwTable _inhabitants = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly GwTable _questions = new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private readonly List<(Button Button, TimeChoice Choice)> _timeButtons = [];
    private readonly Dictionary<string, ProgressBar> _cardBars = new(StringComparer.Ordinal);
    private Label? _cardAction;
    private Label? _cardQuestions;
    private string _cardShape = string.Empty;
    private readonly GwToasts _toasts = new();
    private PanelContainer? _topBar;
    private Label? _date;
    private Label? _time;
    private WorldPresenter? _presenter;
    private WorldFeed? _feed;
    private UiStyle? _style;
    private ToastBoard? _board;
    private GwWindow? _card;
    private string? _cardId;
    private double _sinceRefreshMs = double.MaxValue;

    /// <summary>Id of the inhabitant whose card is open, or null.</summary>
    public string? CardId => _cardId;

    /// <summary>Binds the screen to the running world and builds it.</summary>
    public void Setup(WorldPresenter presenter, UiStyle style)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _style = style ?? throw new ArgumentNullException(nameof(style));
        _feed = new WorldFeed(presenter.Host ?? throw new InvalidOperationException("world not started"));
        _board = new ToastBoard(style.Tokens.Toast, new ToastSettings(1, UntilDismissed: false));

        AddChild(_root);
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        (_topBar, _date, _time) = CreateTopBar(style, ScreenModel.TimeChoices(presenter.Host.World.Calendar.Settings), speed => presenter.Speed = speed);
        foreach (var (button, choice) in _topBar.GetChild(0).GetChildren().OfType<Button>().Zip(ScreenModel.TimeChoices(presenter.Host.World.Calendar.Settings)))
        {
            _timeButtons.Add((button, choice));
        }
        _root.AddChild(_topBar);

        _inhabitants.CustomMinimumSize = new Vector2(0, style.Tokens.Window.TableMinHeight);
        _questions.CustomMinimumSize = new Vector2(0, style.Tokens.Window.TableMinHeight);
        _inhabitants.Setup(
            [new GwColumn("ui.inhabitants.column.name", false), new GwColumn("ui.inhabitants.column.will_to_live", true), new GwColumn("ui.inhabitants.column.questions", true)],
            "ui.inhabitants.empty");
        _inhabitants.RowSelected += OpenCard;
        _questions.Setup([new GwColumn("ui.inhabitants.column.name", false), new GwColumn("ui.questions.column.count", true)], "ui.questions.empty");
        _questions.RowSelected += OpenCard;
        _sideBar.Add("inhabitants", "ui.inhabitants.title").Content.AddChild(_inhabitants);
        _sideBar.Add("questions", "ui.questions.title").Content.AddChild(_questions);
        _root.AddChild(_sideBar);
        foreach (var panel in _sideBar.Panels)
        {
            _root.AddChild(panel);
        }

        _toasts.Board = _board;
        _root.AddChild(_toasts);
        GetViewport().SizeChanged += Layout;
        _sideBar.Toggled += Layout;
        Layout();
    }

    /// <summary>
    /// The top bar: world clock (date and time as two parts) and one toggle per speed (pause, normal,
    /// fast-forward factors). Its parts flow into a second line when the room is too narrow (large UI
    /// or text size), so nothing is cut off (D366 section 2).
    /// </summary>
    public static (PanelContainer Bar, Label Date, Label Time) CreateTopBar(UiStyle style, IReadOnlyList<TimeChoice> choices, Action<double> setSpeed)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(choices);
        var bar = new PanelContainer { ThemeTypeVariation = UiTheme.WindowPanel };
        var row = new HFlowContainer();
        var date = new Label { AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
        var time = new Label { AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
        row.AddChild(date);
        row.AddChild(time);
        var group = new ButtonGroup();
        foreach (var choice in choices)
        {
            var button = new Button { ToggleMode = true, ButtonGroup = group, Text = choice.Symbol };
            button.TooltipText = TextFill.Fill(button.Tr(choice.TooltipKey), choice.Args);
            button.ButtonPressed = choice.Speed == 1;
            var speed = choice.Speed;
            button.Pressed += () => setSpeed(speed);
            row.AddChild(button);
        }

        bar.AddChild(row);
        return (bar, date, time);
    }

    public override void _Process(double delta)
    {
        if (_feed is null || _board is null || _style is null || _presenter is null)
        {
            return;
        }

        var events = _feed.NewEvents();
        if (events.Count > 0)
        {
            foreach (var hint in ScreenModel.Hints(events, _feed.Inhabitants()))
            {
                _board.Push(hint, fastForward: _presenter.Speed > 1);
            }
        }

        _sinceRefreshMs += delta * 1000;
        if (_sinceRefreshMs >= _style.Tokens.Window.ListRefreshMs)
        {
            _sinceRefreshMs = 0;
            Refresh();
        }
    }

    public override void _Notification(int what)
    {
        // Tooltips of the symbol buttons are filled once; refresh them after a language switch.
        if (what == NotificationTranslationChanged)
        {
            foreach (var (button, choice) in _timeButtons)
            {
                button.TooltipText = TextFill.Fill(Tr(choice.TooltipKey), choice.Args);
            }

            _cardShape = string.Empty;
            _sinceRefreshMs = double.MaxValue;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel"))
        {
            // Open windows close themselves on Esc first (GwWindow); only then the pause menu opens.
            PauseRequested?.Invoke();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click && _style is not null)
        {
            var id = PickAt(click.Position);
            if (id is not null)
            {
                Encounter(id);
                GetViewport().SetInputAsHandled();
            }
        }
    }

    /// <summary>The player clicks an inhabitant in the world: a perception for the core (R7) and the card.</summary>
    public void Encounter(string inhabitantId)
    {
        _presenter?.Host?.Decisions.Submit(new PlayerCommand(DecisionsSystem.InhabitantClicked, inhabitantId));
        OpenCard(inhabitantId);
    }

    /// <summary>Time buttons on or off (off while the pause menu is open, GW1 review #50 K3).</summary>
    public void SetTimeControlsEnabled(bool enabled)
    {
        foreach (var (button, _) in _timeButtons)
        {
            button.Disabled = !enabled;
        }
    }

    /// <summary>Esc without an open window: the player wants the pause menu (B6 R5).</summary>
    public event Action? PauseRequested;

    /// <summary>Applies changed settings at once (R4): style, hint duration, hidden UI.</summary>
    public void ApplySettings(PlayerSettings settings, UiStyle style)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _style = style ?? throw new ArgumentNullException(nameof(style));
        if (_board is not null)
        {
            _board.Settings = settings.ToToastSettings();
        }

        // "Watch only" (XAG 117): bars, panels and hints disappear; Esc still opens the pause menu.
        _root.Visible = !settings.HideUi;
        Layout();
    }

    /// <summary>
    /// Shows a window over the game (pause menu, settings) and fits it to the screen. A modal window
    /// blocks clicks on everything behind it until it closes.
    /// </summary>
    public void ShowWindow(GwWindow window, bool modal = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (modal)
        {
            var blocker = new Control { MouseFilter = Control.MouseFilterEnum.Stop };
            blocker.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            AddChild(blocker);
            window.Closed += blocker.QueueFree;
        }

        AddChild(window);
        window.FitInto(GetViewport().GetVisibleRect().Size);
    }

    /// <summary>Shows a calm hint (e.g. saving failed).</summary>
    public void ShowHint(ToastMessage message) => _board?.Push(message, fastForward: false);

    /// <summary>For screenshots: opens the inhabitants panel and the card of the first inhabitant.</summary>
    public void ShowForShots()
    {
        Refresh();
        if (_sideBar.OpenKey != "inhabitants")
        {
            _sideBar.Toggle("inhabitants");
        }

        var rows = _feed?.Inhabitants() ?? [];
        if (rows.Count > 0)
        {
            OpenCard(rows[0].Id);
        }

        Layout();
    }

    /// <summary>Rebuilds lists, clock, card and question marks from the current snapshot.</summary>
    public void Refresh()
    {
        if (_feed is null || _date is null || _time is null)
        {
            return;
        }

        var rows = _feed.Inhabitants();
        _inhabitants.SetRows([.. rows.Select(r => new GwRow(r.Id, [DisplayName(r), Number(r.WillToLivePercent), Number(r.OpenQuestions)], [r.Number, r.WillToLivePercent, r.OpenQuestions]))]);
        _questions.SetRows([.. _feed.OpenQuestions().Select(q => (Row: rows.First(r => r.Id == q.InhabitantId), q.Count)).Select(x => new GwRow(x.Row.Id, [DisplayName(x.Row), Number(x.Count)], [x.Row.Number, x.Count]))]);

        var clock = _feed.Clock();
        _date.Text = TextFill.Fill(Tr("ui.clock.date"), new Dictionary<string, string>
        {
            ["season"] = Tr(clock.SeasonKey),
            ["day"] = Number(clock.Day),
            ["year"] = Number(clock.Year),
        });
        _time.Text = clock.Time;

        foreach (var row in rows)
        {
            UpdateMark(row);
        }

        if (_cardId is not null)
        {
            FillCard(_cardId);
        }
    }

    /// <summary>
    /// Smoke run only: lists show every inhabitant, the clock is translated, a click opens the card and
    /// becomes a question the screen shows. Waits for the presenter's own ticks (never steps the world).
    /// </summary>
    /// <returns>Empty if fine, otherwise the problem.</returns>
    public async Task<string> SelfCheckAsync()
    {
        if (_feed is null || _date is null || _presenter?.Host is null)
        {
            return "screen not set up";
        }

        Refresh();
        var rows = _feed.Inhabitants();
        if (rows.Count == 0 || _date.Text.Length == 0 || _date.Text.Contains("ui.clock", StringComparison.Ordinal))
        {
            return "clock or inhabitant list empty";
        }

        var id = rows[0].Id;
        Encounter(id);
        var speed = _presenter.Speed;
        _presenter.Speed = 1;
        var startTick = _presenter.Host.World.Tick;
        for (var frame = 0; frame < 600 && _presenter.Host.World.Tick <= startTick; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        _presenter.Speed = speed;
        Refresh();
        if (_cardId != id || _card is null || !_card.IsInsideTree())
        {
            return "card did not open";
        }

        var mark = _presenter.GetNodeOrNull<Node3D>(id)?.GetNodeOrNull<Label3D>(MarkNode);
        var open = _feed.Inhabitants().First(r => r.Id == id).OpenQuestions;
        if (open > 0 && (mark is null || !mark.Visible))
        {
            return "question mark missing";
        }

        return await CheckCardPlacementAsync();
    }

    /// <summary>
    /// The card stays inside the safe area and beside the open side panel at the smallest and largest UI
    /// size (found in the 3840x1600 screenshots: the card kept its old place after a size change).
    /// </summary>
    private async Task<string> CheckCardPlacementAsync()
    {
        if (_style is null || _card is null)
        {
            return "card missing";
        }

        if (_sideBar.OpenKey != "inhabitants")
        {
            _sideBar.Toggle("inhabitants");
        }

        var original = _style;
        var problem = string.Empty;
        foreach (var scale in new[] { original.Tokens.UiScale.Max, original.Tokens.UiScale.Min })
        {
            UiSetup.Apply(GetTree().Root, original.With(original.Settings with { UiScale = scale }));
            for (var i = 0; i < 5; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            var area = GetViewport().GetVisibleRect().Size;
            var cardRect = new Rect2(_card.Position, _card.Size);
            var foreground = CardArea(area).X >= area.X;
            var overlap = (!foreground && _sideBar.Panels.Any(p => p.Visible && new Rect2(p.Position, p.Size).Intersects(cardRect)))
                || (_topBar is not null && new Rect2(_topBar.Position, _topBar.Size).Intersects(cardRect));
            if (!_card.IsInsideSafeArea(area) || overlap)
            {
                problem = $"card at UI {scale:0.##}: outside the safe area or over the side panel ({cardRect} in {area})";
                break;
            }
        }

        UiSetup.Apply(GetTree().Root, original);
        return problem;
    }

    private void Layout()
    {
        if (_style is null || _topBar is null)
        {
            return;
        }

        var area = GetViewport().GetVisibleRect().Size;
        var margin = _style.Tokens.Window.SafeMargin;
        var gap = _style.Tokens.Window.Gap;
        _sideBar.Position = new Vector2(area.X - margin - _sideBar.GetCombinedMinimumSize().X, margin);
        _topBar.Position = new Vector2(margin, margin);
        _topBar.Size = new Vector2(area.X - (2 * margin) - _sideBar.GetCombinedMinimumSize().X - gap, 0);
        foreach (var panel in _sideBar.Panels)
        {
            panel.Dock(area, _style, _sideBar.GetCombinedMinimumSize().X + gap);
        }

        var width = Math.Min(_style.Tokens.Window.SidePanelWidth, area.X - (2 * margin));
        _toasts.Size = new Vector2(width, 0);
        _toasts.Position = new Vector2(area.X - margin - width, area.Y - margin - _toasts.GetCombinedMinimumSize().Y);

        // The card stays beside the side bar and its open panel and is re-fitted when the UI size changes.
        FitCard(area);
    }

    /// <summary>Screen part the card is centred in (left of the side bar and its open panel, see <see cref="ScreenModel.CardRoom"/>).</summary>
    /// <summary>
    /// Fits the card beside the side bar and below the top bar (A1335 screenshots: at 175 % it covered
    /// clock and time buttons); content that does not fit scrolls.
    /// </summary>
    private void FitCard(Vector2 area)
    {
        if (_card is null || _style is null || _topBar is null)
        {
            return;
        }

        var window = _style.Tokens.Window;
        var top = Math.Max(0, _topBar.Position.Y + _topBar.Size.Y + window.Gap - window.SafeMargin);
        _card.FitInto(new Vector2(CardArea(area).X, area.Y - top));
        _card.Position += new Vector2(0, top);
    }

    private Vector2 CardArea(Vector2 area)
    {
        if (_style is null || _card is null)
        {
            return area;
        }

        var gap = _style.Tokens.Window.Gap;
        var free = _sideBar.Position.X - gap;
        foreach (var panel in _sideBar.Panels.Where(p => p.Visible))
        {
            free = Math.Min(free, panel.Position.X - gap);
        }

        var width = ScreenModel.CardRoom(area.X, free, _card.GetCombinedMinimumSize().X, _style.Tokens.Window.SafeMargin);
        return new Vector2((float)width, area.Y);
    }

    private void OpenCard(string inhabitantId)
    {
        if (_style is null)
        {
            return;
        }

        _card?.Close();
        _card = new GwWindow();
        _card.Setup("ui.inhabitants.title", _style);
        _card.Closed += () =>
        {
            _card = null;
            _cardId = null;
        };
        _cardId = inhabitantId;
        _cardShape = string.Empty;
        _root.AddChild(_card);
        FillCard(inhabitantId);
        FitCard(GetViewport().GetVisibleRect().Size);
    }

    private void FillCard(string inhabitantId)
    {
        var card = _feed?.Card(inhabitantId);
        if (_card is null || card is null || _style is null)
        {
            _card?.Close();
            return;
        }

        _card.SetTitleText(DisplayName(card.Row));
        var values = new List<(string Key, int Percent)> { ("ui.card.will_to_live", card.Row.WillToLivePercent) };
        values.AddRange(card.Needs.Select(v => (v.Key, v.Percent)));
        values.AddRange(card.Feelings.Select(v => (v.Key, v.Percent)));
        values.AddRange(card.Traits.Select(v => (v.Key, v.Percent)));
        var shape = $"{inhabitantId}|{string.Join(',', card.Needs.Select(v => v.Key))}|{string.Join(',', card.Feelings.Select(v => v.Key))}|{string.Join(',', card.Traits.Select(v => v.Key))}";
        if (shape != _cardShape)
        {
            // Build only when the set of values changes (other inhabitant, a feeling appears/fades).
            _cardShape = shape;
            _cardBars.Clear();
            _card.ClearContent();
            Section("ui.card.wellbeing");
            Bar("ui.card.will_to_live");
            foreach (var need in card.Needs)
            {
                Bar(need.Key);
            }

            Section("ui.card.feelings");
            foreach (var feeling in card.Feelings)
            {
                Bar(feeling.Key);
            }

            Section("ui.card.character");
            foreach (var trait in card.Traits)
            {
                Bar(trait.Key);
            }

            Section("ui.card.doing");
            _cardAction = new Label();
            _cardQuestions = new Label { AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled };
            _card.Content.AddChild(_cardAction);
            _card.Content.AddChild(_cardQuestions);
        }

        foreach (var (key, percent) in values)
        {
            if (_cardBars.TryGetValue(key, out var bar) && (int)bar.Value != percent)
            {
                bar.Value = percent;
            }
        }

        var action = card.Row.ActionKey ?? "ui.card.idle";
        if (_cardAction is not null && _cardAction.Text != action)
        {
            _cardAction.Text = action;
        }

        var questions = TextFill.Fill(Tr("ui.card.questions"), new Dictionary<string, string> { ["count"] = Number(card.Row.OpenQuestions) });
        if (_cardQuestions is not null && _cardQuestions.Text != questions)
        {
            _cardQuestions.Text = questions;
        }

        void Section(string key) => _card!.Content.AddChild(new Label { Text = key, ThemeTypeVariation = UiTheme.SubheaderLabel });

        void Bar(string key)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = key, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
            var bar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = true, CustomMinimumSize = new Vector2(_style!.Tokens.Window.CardBarWidth, 0) };
            row.AddChild(bar);
            _card!.Content.AddChild(row);
            _cardBars[key] = bar;
        }
    }

    private void UpdateMark(InhabitantRow row)
    {
        var figure = _presenter?.GetNodeOrNull<Node3D>(row.Id);
        if (figure is null || _style is null)
        {
            return;
        }

        var mark = figure.GetNodeOrNull<Label3D>(MarkNode);
        if (mark is null)
        {
            mark = new Label3D
            {
                Name = MarkNode,
                Position = new Vector3(0, (float)_style.Tokens.Window.MarkHeightM, 0),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                FontSize = _style.Tokens.Window.MarkFontPx,
                Modulate = UiTheme.ToGodot(_style.Color(UiColorId.Attention)),
                OutlineModulate = UiTheme.ToGodot(_style.Color(UiColorId.Panel)),
                AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled,
                NoDepthTest = true,
            };
            figure.AddChild(mark);
        }

        mark.Text = ScreenModel.QuestionMarks(row.OpenQuestions);
        mark.Visible = row.OpenQuestions > 0;
    }

    private string? PickAt(Vector2 screen)
    {
        var camera = GetViewport().GetCamera3D();
        if (camera is null || _presenter?.Host is null || _style is null)
        {
            return null;
        }

        var figures = (_feed?.Inhabitants() ?? [])
            .Select(i => (i.Id, Node: _presenter.GetNodeOrNull<Node3D>(i.Id)))
            .Where(f => f.Node is not null && !camera.IsPositionBehind(f.Node!.GlobalPosition))
            .Select(f =>
            {
                var p = camera.UnprojectPosition(f.Node!.GlobalPosition);
                return (f.Id, (double)p.X, (double)p.Y);
            });
        return ScreenModel.Pick(figures, screen.X, screen.Y, _style.Tokens.Window.PickRadiusPx);
    }

    private string DisplayName(InhabitantRow row) => TextFill.Fill(Tr("ui.inhabitant.unnamed"), ScreenModel.NameArgs(row));

    /// <summary>Whole numbers as shown (no digit grouping; identical in DE and EN, independent of the OS).</summary>
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
